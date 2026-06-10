using System;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using RGDSCapture.Core;

namespace RGDSCapture.Services
{
    /// <summary>
    /// Manages the SSH connection to the RG DS and the GStreamer pipelines
    /// running on it. Every operation is asynchronous — nothing here ever
    /// blocks the UI thread. Commands are serialized through a semaphore so
    /// concurrent restarts cannot interleave on the remote shell.
    /// </summary>
    public sealed class SshService : IDisposable
    {
        public event Action<string, bool>? StatusChanged;
        public event Action<ScreenId>? StreamStarted;
        public event Action? ConnectionLost;

        public bool IsConnected => _client?.IsConnected ?? false;

        private SshClient? _client;
        private string _hostIp = string.Empty;
        private readonly SemaphoreSlim _commandGate = new(1, 1);
        private volatile bool _intentionalDisconnect;
        private int _lostRaised;

        // ── GStreamer pipelines ───────────────────────────────────────
        // GOP=10 → IDR keyframe every ~333 ms at 30 fps so pixellation from
        // a lost packet self-heals quickly. config-interval=-1 re-sends
        // SPS/PPS with every keyframe so the decoder (and the recorder)
        // can re-sync mid-stream.
        private const string GstTop =
            "nohup gst-launch-1.0 -e " +
            "kmssrc plane-id=98 ! " +
            "videoconvert ! " +
            "videorate ! video/x-raw,framerate=30/1 ! " +
            "mpph264enc rc-mode=vbr bps=2000000 gop=10 ! " +
            "h264parse config-interval=-1 ! " +
            "rtph264pay mtu=1200 pt=96 config-interval=-1 ! " +
            "udpsink host={HOST} port=5000 sync=false buffer-size=2097152 " +
            "> /tmp/gst_top.log 2>&1 &";

        private const string GstBottom =
            "nohup gst-launch-1.0 -e " +
            "kmssrc plane-id=58 ! " +
            "videoconvert ! " +
            "videorate ! video/x-raw,framerate=30/1 ! " +
            "mpph264enc rc-mode=vbr bps=2000000 gop=10 ! " +
            "h264parse config-interval=-1 ! " +
            "rtph264pay mtu=1200 pt=96 config-interval=-1 ! " +
            "udpsink host={HOST} port=5001 sync=false buffer-size=2097152 " +
            "> /tmp/gst_bottom.log 2>&1 &";

        // ─────────────────────────────────────────────────────────────
        public async Task<bool> ConnectAsync(
            string host, int port, string username, string password,
            string hostIp, CancellationToken ct = default)
        {
            _hostIp = hostIp;
            _intentionalDisconnect = false;
            Interlocked.Exchange(ref _lostRaised, 0);

            try
            {
                RaiseStatus($"Connecting to {host}:{port}...", false);

                var connInfo = new ConnectionInfo(host, port, username,
                    new PasswordAuthenticationMethod(username, password))
                {
                    Timeout = TimeSpan.FromSeconds(10)
                };

                var client = new SshClient(connInfo)
                {
                    KeepAliveInterval = TimeSpan.FromSeconds(3)
                };
                client.ErrorOccurred += OnClientError;

                await Task.Run(client.Connect, ct);

                if (!client.IsConnected)
                {
                    client.Dispose();
                    RaiseStatus("SSH connection failed — check IP and credentials.", true);
                    return false;
                }

                _client = client;
                RaiseStatus($"SSH connected to {host}. Checking GStreamer...", false);

                string gstPath = await RunCommandAsync("which gst-launch-1.0", ct);
                if (string.IsNullOrWhiteSpace(gstPath))
                {
                    RaiseStatus("GStreamer not found on device. Install gstreamer1.0-tools.", true);
                    await DisconnectAsync();
                    return false;
                }

                RaiseStatus("Cleaning up old GStreamer processes...", false);
                await RunCommandAsync("pkill -f gst-launch-1.0; sleep 0.5", ct);

                RaiseStatus("Starting top screen stream (port 5000)...", false);
                await RunCommandAsync(GstTop.Replace("{HOST}", hostIp), ct);
                await Task.Delay(500, ct);
                StreamStarted?.Invoke(ScreenId.Top);

                RaiseStatus("Starting bottom screen stream (port 5001)...", false);
                await RunCommandAsync(GstBottom.Replace("{HOST}", hostIp), ct);
                await Task.Delay(500, ct);
                StreamStarted?.Invoke(ScreenId.Bottom);

                RaiseStatus($"All streams started. Receiving from {host}.", false);
                return true;
            }
            catch (OperationCanceledException)
            {
                RaiseStatus("Connection cancelled.", true);
                CleanupClient();
                return false;
            }
            catch (Exception ex)
            {
                RaiseStatus($"Connection error: {ex.Message}", true);
                CleanupClient();
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────
        public async Task RestartStreamAsync(ScreenId screen, CancellationToken ct = default)
        {
            if (!IsConnected) return;

            string logFile = screen == ScreenId.Top ? "gst_top.log" : "gst_bottom.log";
            string startCmd = (screen == ScreenId.Top ? GstTop : GstBottom)
                .Replace("{HOST}", _hostIp);
            string label = screen == ScreenId.Top ? "Top" : "Bottom";

            RaiseStatus($"Restarting {label} stream...", false);

            // Kill only the pipeline whose command line references this
            // stream's log file, leaving the other stream running.
            await RunCommandAsync($"pkill -f {logFile}", ct);
            await Task.Delay(600, ct);
            await RunCommandAsync(startCmd, ct);
            await Task.Delay(500, ct);

            StreamStarted?.Invoke(screen);
            RaiseStatus($"{label} stream restarted.", false);
        }

        public async Task ShutdownConsoleAsync()
        {
            if (!IsConnected) return;
            _intentionalDisconnect = true;
            RaiseStatus("Stopping streams...", false);
            await RunCommandAsync("pkill -f gst-launch-1.0");
            await Task.Delay(500);
            RaiseStatus("Sending shutdown command...", false);
            await RunCommandAsync("poweroff");
        }

        public async Task RebootConsoleAsync()
        {
            if (!IsConnected) return;
            _intentionalDisconnect = true;
            RaiseStatus("Stopping streams...", false);
            await RunCommandAsync("pkill -f gst-launch-1.0");
            await Task.Delay(500);
            RaiseStatus("Sending reboot command...", false);
            await RunCommandAsync("reboot");
        }

        /// <summary>Stops device pipelines and closes the connection gracefully.</summary>
        public async Task DisconnectAsync()
        {
            _intentionalDisconnect = true;
            try
            {
                if (_client?.IsConnected == true)
                {
                    RaiseStatus("Stopping streams on device...", false);
                    await RunCommandAsync("pkill -f gst-launch-1.0");
                    await Task.Run(() => _client.Disconnect());
                }
            }
            catch
            {
                // Best effort — the device may already be gone.
            }
            finally
            {
                CleanupClient();
            }
        }

        public void Dispose()
        {
            _intentionalDisconnect = true;
            CleanupClient();
            _commandGate.Dispose();
        }

        // ─────────────────────────────────────────────────────────────
        private async Task<string> RunCommandAsync(string command, CancellationToken ct = default)
        {
            var client = _client;
            if (client?.IsConnected != true) return string.Empty;

            await _commandGate.WaitAsync(ct);
            try
            {
                return await Task.Run(() =>
                {
                    using var cmd = client.CreateCommand(command);
                    cmd.CommandTimeout = TimeSpan.FromSeconds(8);
                    return cmd.Execute();
                }, ct);
            }
            finally
            {
                _commandGate.Release();
            }
        }

        private void OnClientError(object? sender, Renci.SshNet.Common.ExceptionEventArgs e)
        {
            if (_intentionalDisconnect) return;
            if (Interlocked.Exchange(ref _lostRaised, 1) == 1) return;
            ConnectionLost?.Invoke();
        }

        private void CleanupClient()
        {
            var client = _client;
            _client = null;
            if (client != null)
            {
                client.ErrorOccurred -= OnClientError;
                try { client.Dispose(); } catch { }
            }
        }

        private void RaiseStatus(string message, bool isError)
            => StatusChanged?.Invoke(message, isError);
    }
}
