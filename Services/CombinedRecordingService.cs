using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using RGDSCapture.Core;

namespace RGDSCapture.Services
{
    /// <summary>
    /// Records both screens plus Line-In audio into a single MP4 with three
    /// synced tracks (video copied, audio AAC-encoded by ffmpeg).
    ///
    /// Sync strategy: each H.264 elementary stream carries no timestamps, so
    /// track alignment comes from wall-clock measurement. The session arms
    /// taps on both receivers and the audio device, waits until each video
    /// stream produces an SPS (keyframe boundary — at most one GOP, ~333 ms),
    /// then launches ffmpeg with per-input -itsoffset values computed from
    /// the measured start times. Audio captured before the video base time is
    /// trimmed to the sample. Net alignment is within a few tens of ms.
    /// </summary>
    public static class CombinedRecordingService
    {
        public static CombinedRecordingSession? Start(
            RtpStreamReceiver top,
            RtpStreamReceiver bottom,
            int? audioDeviceIndex,
            Action<string, bool> log)
        {
            if (!File.Exists(AppPaths.FfmpegExe))
            {
                log($"[RECORD] ffmpeg.exe not found at {AppPaths.FfmpegExe}", true);
                return null;
            }
            if (!top.IsRunning || !bottom.IsRunning)
            {
                log("[RECORD] Streams are not running.", true);
                return null;
            }

            Directory.CreateDirectory(AppPaths.RecordingsDir);
            string ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string outFile = Path.Combine(AppPaths.RecordingsDir, $"rg_combined_{ts}.mp4");

            try
            {
                return new CombinedRecordingSession(top, bottom, audioDeviceIndex, outFile, log);
            }
            catch (Exception ex)
            {
                log($"[RECORD] Combined start failed: {ex.Message}", true);
                return null;
            }
        }
    }

    public sealed class CombinedRecordingSession : IDisposable
    {
        private const string VideoInputArgs =
            "-thread_queue_size 1024 -fflags +genpts -framerate 30 -f h264";
        private const string AudioInputArgs =
            "-thread_queue_size 1024 -f s16le -ar 48000 -ac 2";
        private const int AudioBytesPerSecond = 48000 * 2 * 2;

        /// <summary>Raised (any thread) when the session dies before StopAsync.</summary>
        public event Action? Failed;

        public string OutputFile { get; }

        private enum Phase { Arming, Streaming, Stopped, Aborted }

        private readonly object _gate = new();
        private readonly RtpStreamReceiver _top;
        private readonly RtpStreamReceiver _bottom;
        private readonly Action<string, bool> _log;
        private readonly Task _armTask;

        private readonly List<byte[]> _preTop = new();
        private readonly List<byte[]> _preBottom = new();
        private readonly List<byte[]> _preAudio = new();

        private Phase _phase = Phase.Arming;
        private bool _spsTopSeen, _spsBottomSeen;
        private DateTime _spsTopUtc, _spsBottomUtc, _audioStartUtc;
        private long _audioSkipBytes;
        private AudioRecordingTap? _audio;
        private FfmpegPipeMuxer? _mux;
        private int _failedRaised;

        internal CombinedRecordingSession(
            RtpStreamReceiver top, RtpStreamReceiver bottom,
            int? audioDeviceIndex, string outFile, Action<string, bool> log)
        {
            _top = top;
            _bottom = bottom;
            OutputFile = outFile;
            _log = log;

            if (audioDeviceIndex.HasValue)
            {
                try
                {
                    _audioStartUtc = DateTime.UtcNow;
                    _audio = new AudioRecordingTap(audioDeviceIndex.Value);
                    _audio.DataAvailable += OnAudio;
                }
                catch (Exception ex)
                {
                    _audio = null;
                    log($"[RECORD] Audio capture unavailable ({ex.Message}) — recording video only.", true);
                }
            }
            else
            {
                log("[RECORD] No audio input selected — recording video only.", false);
            }

            _top.NalUnitReceived += OnTopNal;
            _bottom.NalUnitReceived += OnBottomNal;

            _armTask = Task.Run(ArmAsync);
        }

        // ── Live taps ─────────────────────────────────────────────────
        private void OnTopNal(byte[] nal)
        {
            lock (_gate)
            {
                switch (_phase)
                {
                    case Phase.Arming:
                        if (!_spsTopSeen)
                        {
                            if (!IsSps(nal)) return;
                            _spsTopSeen = true;
                            _spsTopUtc = DateTime.UtcNow;
                        }
                        _preTop.Add(nal);
                        break;
                    case Phase.Streaming:
                        _mux!.TryWrite(0, nal);
                        break;
                }
            }
        }

        private void OnBottomNal(byte[] nal)
        {
            lock (_gate)
            {
                switch (_phase)
                {
                    case Phase.Arming:
                        if (!_spsBottomSeen)
                        {
                            if (!IsSps(nal)) return;
                            _spsBottomSeen = true;
                            _spsBottomUtc = DateTime.UtcNow;
                        }
                        _preBottom.Add(nal);
                        break;
                    case Phase.Streaming:
                        _mux!.TryWrite(1, nal);
                        break;
                }
            }
        }

        private void OnAudio(byte[] pcm)
        {
            lock (_gate)
            {
                switch (_phase)
                {
                    case Phase.Arming:
                        _preAudio.Add(pcm);
                        break;
                    case Phase.Streaming:
                        WriteAudioTrimmed(pcm);
                        break;
                }
            }
        }

        /// <summary>Drops leading samples captured before the video base time.</summary>
        private void WriteAudioTrimmed(byte[] pcm)
        {
            if (_audioSkipBytes >= pcm.Length)
            {
                _audioSkipBytes -= pcm.Length;
                return;
            }
            if (_audioSkipBytes > 0)
            {
                var rest = new byte[pcm.Length - _audioSkipBytes];
                Buffer.BlockCopy(pcm, (int)_audioSkipBytes, rest, 0, rest.Length);
                _audioSkipBytes = 0;
                _mux!.TryWrite(2, rest);
                return;
            }
            _mux!.TryWrite(2, pcm);
        }

        // ── Arm: wait for keyframes, compute offsets, launch ffmpeg ──
        private async Task ArmAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (_phase != Phase.Arming) return;   // stopped during arm
                    if (_spsTopSeen && _spsBottomSeen) break;
                }
                await Task.Delay(50);
            }

            bool hasAudio;
            lock (_gate)
            {
                if (_phase != Phase.Arming) return;

                if (!_spsTopSeen || !_spsBottomSeen)
                {
                    string which = !_spsTopSeen ? "top" : "bottom";
                    _phase = Phase.Aborted;
                    DetachTaps();
                    _log($"[RECORD] Combined: no keyframe from the {which} stream within 6 s — aborted.", true);
                    RaiseFailedOnce();
                    return;
                }

                var baseUtc = _spsTopUtc < _spsBottomUtc ? _spsTopUtc : _spsBottomUtc;
                double topOffset = (_spsTopUtc - baseUtc).TotalSeconds;
                double bottomOffset = (_spsBottomUtc - baseUtc).TotalSeconds;

                hasAudio = _audio != null;
                // Video offsets are applied via the setts bitstream filter in
                // the output args (-itsoffset would be overridden by setts);
                // only the audio input uses -itsoffset.
                var inputs = new List<MuxInput>
                {
                    new(VideoInputArgs, 0),
                    new(VideoInputArgs, 0)
                };

                double audioOffset = 0;
                if (hasAudio)
                {
                    if (_audioStartUtc <= baseUtc)
                    {
                        _audioSkipBytes = (long)((baseUtc - _audioStartUtc).TotalSeconds * AudioBytesPerSecond);
                        _audioSkipBytes -= _audioSkipBytes % 4;   // whole stereo frames
                    }
                    else
                    {
                        audioOffset = (_audioStartUtc - baseUtc).TotalSeconds;
                    }
                    inputs.Add(new MuxInput(AudioInputArgs, audioOffset));
                }

                string outputArgs =
                    "-map 0:v:0 -map 1:v:0 " +
                    (hasAudio ? "-map 2:a:0 " : "") +
                    "-c:v copy " +
                    (hasAudio ? "-c:a aac -b:a 192k " : "") +
                    FfmpegArgs.CfrSetts(0, topOffset) +
                    FfmpegArgs.CfrSetts(1, bottomOffset) +
                    "-metadata:s:v:0 handler_name=\"Top Screen\" " +
                    "-metadata:s:v:1 handler_name=\"Bottom Screen\" " +
                    "-movflags +faststart";

                try
                {
                    _mux = new FfmpegPipeMuxer(OutputFile, inputs, outputArgs, _log);
                }
                catch (Exception ex)
                {
                    _phase = Phase.Aborted;
                    DetachTaps();
                    _log($"[RECORD] Combined: ffmpeg launch failed: {ex.Message}", true);
                    RaiseFailedOnce();
                    return;
                }
                _mux.Failed += RaiseFailedOnce;

                foreach (var nal in _preTop) _mux.TryWrite(0, nal);
                foreach (var nal in _preBottom) _mux.TryWrite(1, nal);
                foreach (var pcm in _preAudio) WriteAudioTrimmed(pcm);
                _preTop.Clear();
                _preBottom.Clear();
                _preAudio.Clear();

                _phase = Phase.Streaming;
            }

            _log($"[RECORD] Combined → {OutputFile} (top + bottom{(hasAudio ? " + audio" : "")})", false);
        }

        // ── Stop / teardown ───────────────────────────────────────────
        public async Task StopAsync()
        {
            lock (_gate)
            {
                if (_phase is Phase.Stopped or Phase.Aborted) return;
                _phase = _phase == Phase.Streaming ? Phase.Stopped : Phase.Aborted;
            }

            DetachTaps();
            await _armTask;

            if (_mux != null)
            {
                _mux.Failed -= RaiseFailedOnce;
                // Generous timeout: +faststart rewrites the file on finalize.
                bool ok = await _mux.CompleteAsync(timeoutMs: 120_000);
                _log(ok
                    ? "[RECORD] Combined recording saved."
                    : "[RECORD] Combined recording may be incomplete (ffmpeg did not exit cleanly).",
                    !ok);
            }
        }

        private void DetachTaps()
        {
            _top.NalUnitReceived -= OnTopNal;
            _bottom.NalUnitReceived -= OnBottomNal;
            if (_audio != null)
            {
                _audio.DataAvailable -= OnAudio;
                _audio.Dispose();
                _audio = null;
            }
        }

        private void RaiseFailedOnce()
        {
            if (Interlocked.Exchange(ref _failedRaised, 1) == 0)
                Failed?.Invoke();
        }

        private static bool IsSps(byte[] nal)
            => nal.Length > 4 && (nal[4] & 0x1F) == 7;

        public void Dispose()
        {
            lock (_gate)
            {
                if (_phase is Phase.Arming or Phase.Streaming)
                    _phase = Phase.Aborted;
            }
            DetachTaps();
            _mux?.Dispose();
        }
    }

    // ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// Independent Line-In capture for recording. Separate from the
    /// passthrough engine so combined recording works whether or not
    /// monitoring is running (Windows shares the device between captures).
    /// Recorded at unity gain — the monitor volume slider does not color
    /// the recording.
    /// </summary>
    public sealed class AudioRecordingTap : IDisposable
    {
        public event Action<byte[]>? DataAvailable;

        private readonly WaveInEvent _input;

        public AudioRecordingTap(int deviceIndex)
        {
            _input = new WaveInEvent
            {
                DeviceNumber = deviceIndex,
                WaveFormat = new WaveFormat(48000, 16, 2),
                BufferMilliseconds = 30,
                NumberOfBuffers = 3
            };
            _input.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded == 0) return;
                // NAudio reuses e.Buffer — copy before handing off.
                var copy = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
                DataAvailable?.Invoke(copy);
            };
            _input.StartRecording();
        }

        public void Dispose()
        {
            try { _input.StopRecording(); } catch { }
            _input.Dispose();
        }
    }
}
