using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RGDSCapture.Core;

namespace RGDSCapture.Services
{
    /// <summary>
    /// Records a stream to MP4 with zero re-encoding.
    ///
    /// The receiver already owns the UDP port and reassembles complete
    /// Annex-B NAL units, so recording taps <see cref="RtpStreamReceiver.NalUnitReceived"/>
    /// and pipes the elementary stream into ffmpeg's stdin, which remuxes it
    /// into MP4 with <c>-c:v copy</c>. No second socket, no transcode, no
    /// quality loss, near-zero CPU.
    /// </summary>
    public static class RecordingService
    {
        public static RecordingSession? Start(
            ScreenId screen,
            RtpStreamReceiver receiver,
            Action<string, bool> log)
        {
            if (!File.Exists(AppPaths.FfmpegExe))
            {
                log($"[RECORD] ffmpeg.exe not found at {AppPaths.FfmpegExe}", true);
                return null;
            }

            Directory.CreateDirectory(AppPaths.RecordingsDir);
            string label = screen == ScreenId.Top ? "top" : "bottom";
            string ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string outFile = Path.Combine(AppPaths.RecordingsDir, $"rg_{label}_{ts}.mp4");

            try
            {
                var session = new RecordingSession(receiver, outFile, label, log);
                log($"[RECORD] {label} → {outFile}", false);
                return session;
            }
            catch (Exception ex)
            {
                log($"[RECORD] Start failed: {ex.Message}", true);
                return null;
            }
        }
    }

    /// <summary>
    /// One active recording. Created via <see cref="RecordingService.Start"/>;
    /// call <see cref="StopAsync"/> to finalize the MP4.
    /// </summary>
    public sealed class RecordingSession : IDisposable
    {
        /// <summary>Raised (on a background thread) if ffmpeg dies mid-recording.</summary>
        public event Action? Failed;

        public string OutputFile { get; }

        private readonly RtpStreamReceiver _receiver;
        private readonly Process _proc;
        private readonly BlockingCollection<byte[]> _queue = new(boundedCapacity: 1024);
        private readonly Task _writerTask;
        private readonly Action<string, bool> _log;
        private readonly string _label;
        private bool _seenSps;
        private bool _stopped;

        internal RecordingSession(
            RtpStreamReceiver receiver, string outFile, string label,
            Action<string, bool> log)
        {
            _receiver = receiver;
            OutputFile = outFile;
            _label = label;
            _log = log;

            // -f h264: raw Annex-B elementary stream on stdin.
            // -framerate 30 matches the device pipeline's videorate cap.
            // -c:v copy: remux only — the device already encoded H.264.
            // setts forces exact 30 fps CFR timestamps even if the encoder
            // wrote misleading SPS VUI timing (which overrides -framerate).
            var psi = new ProcessStartInfo
            {
                FileName = AppPaths.FfmpegExe,
                Arguments =
                    "-hide_banner -loglevel error " +
                    "-fflags +genpts -framerate 30 -f h264 -i pipe:0 " +
                    "-c:v copy " + FfmpegArgs.CfrSetts(0, 0) +
                    $"-movflags +faststart -y \"{outFile}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true
            };

            _proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ffmpeg.");

            // Drain stderr so ffmpeg can't deadlock on a full pipe; surface errors.
            _ = Task.Run(() =>
            {
                try
                {
                    string? line;
                    while ((line = _proc.StandardError.ReadLine()) != null)
                        _log($"[RECORD] {_label} ffmpeg: {line}", true);
                }
                catch { }
            });

            _proc.EnableRaisingEvents = true;
            _proc.Exited += (_, _) =>
            {
                if (!_stopped) Failed?.Invoke();
            };

            _writerTask = Task.Run(WriterLoop);
            _receiver.NalUnitReceived += OnNal;
        }

        private void OnNal(byte[] annexBNal)
        {
            // MP4 needs SPS/PPS before the first slice. The device re-sends
            // SPS with every keyframe (config-interval=-1), so just wait for
            // one before letting data through.
            if (!_seenSps)
            {
                if (annexBNal.Length < 5 || (annexBNal[4] & 0x1F) != 7) return;
                _seenSps = true;
            }

            // Bounded queue: under backpressure drop NALs rather than stall
            // the receive/decode thread. ffmpeg's error concealment copes.
            _queue.TryAdd(annexBNal);
        }

        private void WriterLoop()
        {
            try
            {
                var stdin = _proc.StandardInput.BaseStream;
                foreach (var nal in _queue.GetConsumingEnumerable())
                    stdin.Write(nal, 0, nal.Length);
                stdin.Flush();
            }
            catch
            {
                // Broken pipe — ffmpeg exited; the Exited handler reports it.
            }
        }

        /// <summary>Stops the tap, closes ffmpeg's stdin and waits for the MP4 to finalize.</summary>
        public async Task StopAsync()
        {
            if (_stopped) return;
            _stopped = true;

            _receiver.NalUnitReceived -= OnNal;
            _queue.CompleteAdding();

            await Task.Run(() =>
            {
                try
                {
                    _writerTask.Wait(3000);
                    _proc.StandardInput.Close();
                    if (!_proc.WaitForExit(8000))
                    {
                        _proc.Kill(entireProcessTree: true);
                        _proc.WaitForExit(2000);
                    }
                }
                catch (Exception ex)
                {
                    _log($"[RECORD] Stop error: {ex.Message}", true);
                    try { _proc.Kill(entireProcessTree: true); } catch { }
                }
            });

            _log($"[RECORD] {_label} recording saved.", false);
        }

        public void Dispose()
        {
            if (!_stopped)
            {
                _stopped = true;
                _receiver.NalUnitReceived -= OnNal;
                _queue.CompleteAdding();
                try { _proc.Kill(entireProcessTree: true); } catch { }
            }
            _proc.Dispose();
            _queue.Dispose();
        }
    }
}
