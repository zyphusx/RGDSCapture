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

        /// <summary>True when the last failed ConnectAsync was an authentication rejection.</summary>
        public bool LastFailureWasAuth { get; private set; }

        /// <summary>Encoder bitrate per screen, substituted into the pipeline template.</summary>
        public int VideoBitrateBps { get; set; } = 2_000_000;

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
        // {BPS} is the quality preset bitrate; GOP stays fixed at 10 because
        // recovery and recording logic depend on the ~333 ms keyframe cadence.
        private const string GstTop =
            "nohup gst-launch-1.0 -e " +
            "kmssrc plane-id=98 ! " +
            "videoconvert ! " +
            "videorate ! video/x-raw,framerate=30/1 ! " +
            "mpph264enc rc-mode=vbr bps={BPS} gop=10 ! " +
            "h264parse config-interval=-1 ! " +
            "rtph264pay mtu=1200 pt=96 config-interval=-1 ! " +
            "udpsink host={HOST} port=5000 sync=false buffer-size=2097152 " +
            "> /tmp/gst_top.log 2>&1 &";

        private const string GstBottom =
            "nohup gst-launch-1.0 -e " +
            "kmssrc plane-id=58 ! " +
            "videoconvert ! " +
            "videorate ! video/x-raw,framerate=30/1 ! " +
            "mpph264enc rc-mode=vbr bps={BPS} gop=10 ! " +
            "h264parse config-interval=-1 ! " +
            "rtph264pay mtu=1200 pt=96 config-interval=-1 ! " +
            "udpsink host={HOST} port=5001 sync=false buffer-size=2097152 " +
            "> /tmp/gst_bottom.log 2>&1 &";

        private string BuildPipeline(ScreenId screen, string hostIp) =>
            (screen == ScreenId.Top ? GstTop : GstBottom)
                .Replace("{HOST}", hostIp)
                .Replace("{BPS}", VideoBitrateBps.ToString());

        // ─────────────────────────────────────────────────────────────
        public async Task<bool> ConnectAsync(
            string host, int port, string username, string password,
            string hostIp, CancellationToken ct = default)
        {
            _hostIp = hostIp;
            _intentionalDisconnect = false;
            LastFailureWasAuth = false;
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
                await RunCommandAsync(BuildPipeline(ScreenId.Top, hostIp), ct);
                await Task.Delay(500, ct);
                StreamStarted?.Invoke(ScreenId.Top);

                RaiseStatus("Starting bottom screen stream (port 5001)...", false);
                await RunCommandAsync(BuildPipeline(ScreenId.Bottom, hostIp), ct);
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
            catch (Renci.SshNet.Common.SshAuthenticationException ex)
            {
                LastFailureWasAuth = true;
                RaiseStatus($"Authentication failed: {ex.Message}", true);
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
            string startCmd = BuildPipeline(screen, _hostIp);
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
                    Renci.SshNet.SshCommand? cmd = null;
                    bool completed = false;
                    try
                    {
                        cmd = client.CreateCommand(command);
                        cmd.CommandTimeout = TimeSpan.FromSeconds(8);
                        string result = cmd.Execute();
                        completed = true;
                        return result;
                    }
                    catch (Exception ex)
                    {
                        RaiseStatus($"[SSH] Command failed: {ex.Message}", true);
                        return string.Empty;
                    }
                    finally
                    {
                        // Only dispose commands that finished. Disposing one
                        // that is still executing (timeout, dropped link)
                        // races SSH.NET's socket thread against the disposed
                        // object; leaking it to the GC is the safe option.
                        if (completed)
                        {
                            try { cmd?.Dispose(); } catch { }
                        }
                    }
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
