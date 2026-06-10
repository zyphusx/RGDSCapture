using System;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace RGDSCapture
{
    public sealed class SshManager : IDisposable
    {
        private SshClient? _sshClient;
        private bool       _disposed  = false;
        private string     _windowsIp = string.Empty;

        public bool IsConnected => _sshClient?.IsConnected ?? false;

        public event Action<string, bool>? StatusChanged;
        public event Action<StreamType>?   StreamStarted;
        public event Action?               Disconnected;

        // ── GStreamer pipelines ───────────────────────────────────────
        // GOP=10 → IDR keyframe every ~333ms at 30fps so pixellation from
        // a lost packet self-heals quickly.
        // config-interval=-1 re-sends SPS/PPS with every keyframe so the
        // decoder can re-sync without a full pipeline restart.
        private const string GST_TOP =
            "nohup gst-launch-1.0 -e " +
            "kmssrc plane-id=98 ! " +
            "videoconvert ! " +
            "videorate ! video/x-raw,framerate=30/1 ! " +
            "mpph264enc rc-mode=vbr bps=2000000 gop=10 ! " +
            "h264parse config-interval=-1 ! " +
            "rtph264pay mtu=1200 pt=96 config-interval=-1 ! " +
            "udpsink host={HOST} port=5000 sync=false buffer-size=2097152 " +
            "> /tmp/gst_top.log 2>&1 &";

        private const string GST_BOTTOM =
            "nohup gst-launch-1.0 -e " +
            "kmssrc plane-id=58 ! " +
            "videoconvert ! " +
            "videorate ! video/x-raw,framerate=30/1 ! " +
            "mpph264enc rc-mode=vbr bps=2000000 gop=10 ! " +
            "h264parse config-interval=-1 ! " +
            "rtph264pay mtu=1200 pt=96 config-interval=-1 ! " +
            "udpsink host={HOST} port=5001 sync=false buffer-size=2097152 " +
            "> /tmp/gst_bottom.log 2>&1 &";

        // ── Connect ───────────────────────────────────────────────────
        public async Task<bool> ConnectAsync(
            string host, int port, string username, string password,
            string windowsIp,
            CancellationToken ct = default)
        {
            _windowsIp = windowsIp;
            try
            {
                RaiseStatus($"Connecting to {host}:{port}...", false);

                var connInfo = new ConnectionInfo(host, port, username,
                    new PasswordAuthenticationMethod(username, password))
                {
                    Timeout = TimeSpan.FromSeconds(10)
                };

                _sshClient = new SshClient(connInfo);
                await Task.Run(() => _sshClient.Connect(), ct);

                if (!_sshClient.IsConnected)
                {
                    RaiseStatus("SSH connection failed — check IP and credentials.", true);
                    return false;
                }

                RaiseStatus($"SSH connected to {host}. Checking GStreamer...", false);

                string gstCheck = RunCommand("which gst-launch-1.0");
                if (string.IsNullOrWhiteSpace(gstCheck))
                {
                    RaiseStatus("GStreamer not found on device. Install gstreamer1.0-tools.", true);
                    return false;
                }

                RaiseStatus("Cleaning up old GStreamer processes...", false);
                RunCommand("pkill -f gst-launch-1.0; sleep 0.5");

                RaiseStatus("Starting top screen stream (port 5000)...", false);
                RunCommand(GST_TOP.Replace("{HOST}", windowsIp));
                await Task.Delay(500, ct);
                StreamStarted?.Invoke(StreamType.TopScreen);

                RaiseStatus("Starting bottom screen stream (port 5001)...", false);
                RunCommand(GST_BOTTOM.Replace("{HOST}", windowsIp));
                await Task.Delay(500, ct);
                StreamStarted?.Invoke(StreamType.BottomScreen);

                RaiseStatus($"All streams started. Receiving from {host}.", false);
                return true;
            }
            catch (OperationCanceledException)
            {
                RaiseStatus("Connection cancelled.", true);
                return false;
            }
            catch (Exception ex)
            {
                RaiseStatus($"Connection error: {ex.Message}", true);
                return false;
            }
        }

        // ── Stream restart ────────────────────────────────────────────
        public async Task RestartStreamAsync(StreamType stream,
            CancellationToken ct = default)
        {
            if (!IsConnected) return;

            string logFile  = stream == StreamType.TopScreen ? "gst_top.log" : "gst_bottom.log";
            string startCmd = stream == StreamType.TopScreen
                ? GST_TOP.Replace("{HOST}", _windowsIp)
                : GST_BOTTOM.Replace("{HOST}", _windowsIp);
            string label    = stream == StreamType.TopScreen ? "Top" : "Bottom";

            RaiseStatus($"Restarting {label} stream...", false);
            // Kill only the process whose command line references this stream's log file,
            // leaving the other stream running.
            RunCommand($"pkill -f {logFile}");
            await Task.Delay(600, ct);
            RunCommand(startCmd);
            await Task.Delay(500, ct);
            StreamStarted?.Invoke(stream);
            RaiseStatus($"{label} stream restarted.", false);
        }

        // ── Console power ─────────────────────────────────────────────
        public async Task ShutdownConsoleAsync()
        {
            if (!IsConnected) return;
            RaiseStatus("Stopping streams...", false);
            RunCommand("pkill -f gst-launch-1.0");
            await Task.Delay(500);
            RaiseStatus("Sending shutdown command...", false);
            RunCommand("poweroff");
        }

        public async Task RebootConsoleAsync()
        {
            if (!IsConnected) return;
            RaiseStatus("Stopping streams...", false);
            RunCommand("pkill -f gst-launch-1.0");
            await Task.Delay(500);
            RaiseStatus("Sending reboot command...", false);
            RunCommand("reboot");
        }

        // ── Disconnect ────────────────────────────────────────────────
        public void Disconnect()
        {
            try
            {
                if (_sshClient?.IsConnected == true)
                {
                    RaiseStatus("Stopping streams on device...", false);
                    RunCommand("pkill -f gst-launch-1.0");
                    _sshClient.Disconnect();
                }
            }
            catch { }
            finally
            {
                _sshClient?.Dispose();
                _sshClient = null;
                Disconnected?.Invoke();
            }
        }

        // ── Internals ─────────────────────────────────────────────────
        private string RunCommand(string command)
        {
            if (_sshClient?.IsConnected != true) return string.Empty;
            using var cmd = _sshClient.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromSeconds(8);
            return cmd.Execute();
        }

        private void RaiseStatus(string message, bool isError)
            => StatusChanged?.Invoke(message, isError);

        public void Dispose()
        {
            if (_disposed) return;
            Disconnect();
            _disposed = true;
        }
    }

    public enum StreamType
    {
        TopScreen,
        BottomScreen
    }
}
