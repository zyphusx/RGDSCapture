using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;

namespace RGDSCapture
{
    /// <summary>
    /// Low-latency Line-In passthrough: WaveInEvent → RingBufferProvider → WaveOutEvent.
    ///
    /// All audio stays as 48 kHz 16-bit stereo PCM throughout — no format
    /// conversion chain, so no NAudio "Must be already floating point" errors.
    /// Volume is applied in-place in the capture callback before the ring buffer.
    /// Drift correction prevents latency creep and starve-clicks.
    /// </summary>
    public sealed class AudioMonitor : IDisposable
    {
        // ── Tuning ────────────────────────────────────────────────────
        private const int CaptureBufMs = 30;   // WMME reliable minimum
        private const int TargetFillMs = 80;   // ideal buffer ahead of playback
        private const int MaxFillMs    = 160;  // above this → drop oldest silently
        private const int MinFillMs    = 20;   // below this → insert silence
        private const int RingBufMs    = 2000; // total ring buffer capacity

        // ── Public state ──────────────────────────────────────────────
        public bool IsRunning { get; private set; }

        private float _volume = 0.85f;
        public float Volume
        {
            get => _volume;
            set => _volume = Math.Clamp(value, 0f, 1f);
        }

        private float _levelLeft, _levelRight;
        public float LevelLeft  => Volatile.Read(ref _levelLeft);
        public float LevelRight => Volatile.Read(ref _levelRight);

        // ── Internals ─────────────────────────────────────────────────
        private WaveInEvent?        _input;
        private WaveOutEvent?       _output;
        private RingBufferProvider? _ring;
        private int _targetFillBytes, _maxFillBytes, _minFillBytes;

        // ── Device enumeration ────────────────────────────────────────
        public static List<AudioDevice> GetInputDevices()
        {
            var list = new List<AudioDevice>();
            for (int i = 0; i < WaveIn.DeviceCount; i++)
                list.Add(new AudioDevice
                {
                    Index = i,
                    Name  = WaveIn.GetCapabilities(i).ProductName
                });
            return list;
        }

        public static List<AudioDevice> GetOutputDevices()
        {
            var list = new List<AudioDevice>
            {
                new AudioDevice { Index = -1, Name = "System Default" }
            };
            for (int i = 0; i < WaveOut.DeviceCount; i++)
                list.Add(new AudioDevice
                {
                    Index = i,
                    Name  = WaveOut.GetCapabilities(i).ProductName
                });
            return list;
        }

        // ─────────────────────────────────────────────────────────────
        // START
        // ─────────────────────────────────────────────────────────────
        public void Start(int inputDevice = 0, int outputDevice = -1)
        {
            if (IsRunning) return;

            var fmt = new WaveFormat(48000, 16, 2);

            int bpm           = fmt.AverageBytesPerSecond / 1000;
            _targetFillBytes  = TargetFillMs * bpm;
            _maxFillBytes     = MaxFillMs    * bpm;
            _minFillBytes     = MinFillMs    * bpm;

            _ring = new RingBufferProvider(fmt, RingBufMs * bpm);
            _ring.PrimeSilence(_targetFillBytes);

            _output = new WaveOutEvent
            {
                DeviceNumber    = outputDevice,
                DesiredLatency  = TargetFillMs,
                NumberOfBuffers = 3
            };
            _output.Init(_ring);
            _output.Play();

            _input = new WaveInEvent
            {
                DeviceNumber       = inputDevice,
                WaveFormat         = fmt,
                BufferMilliseconds = CaptureBufMs,
                NumberOfBuffers    = 3
            };
            _input.DataAvailable    += OnDataAvailable;
            _input.RecordingStopped += OnRecordingStopped;
            _input.StartRecording();

            BoostProcessPriority();
            IsRunning = true;
        }

        // ─────────────────────────────────────────────────────────────
        // STOP
        // ─────────────────────────────────────────────────────────────
        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;

            try { _input?.StopRecording(); } catch { }
            try { _output?.Stop();         } catch { }

            _input?.Dispose();
            _output?.Dispose();
            _input  = null;
            _output = null;
            _ring   = null;

            Volatile.Write(ref _levelLeft,  0f);
            Volatile.Write(ref _levelRight, 0f);
        }

        // ─────────────────────────────────────────────────────────────
        // CAPTURE CALLBACK — runs on WMME thread, must be fast
        // ─────────────────────────────────────────────────────────────
        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_ring == null || e.BytesRecorded == 0) return;

            float vol = _volume;
            if (Math.Abs(vol - 1.0f) > 0.001f)
                ApplyVolume(e.Buffer, e.BytesRecorded, vol);

            int fill = _ring.BufferedBytes;
            if (fill > _maxFillBytes)
                _ring.DropOldest(fill - _targetFillBytes);
            else if (fill < _minFillBytes)
                _ring.InsertSilence(_targetFillBytes - fill);

            _ring.Write(e.Buffer, 0, e.BytesRecorded);
            ComputeLevels(e.Buffer, e.BytesRecorded);
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
                System.Diagnostics.Debug.WriteLine(
                    $"[AudioMonitor] Capture stopped: {e.Exception.Message}");
        }

        // ─────────────────────────────────────────────────────────────
        // HELPERS
        // ─────────────────────────────────────────────────────────────

        /// <summary>Scale 16-bit PCM samples in-place. Clamps to prevent wrap.</summary>
        private static void ApplyVolume(byte[] buf, int count, float vol)
        {
            for (int i = 0; i < count - 1; i += 2)
            {
                short s = (short)(buf[i] | (buf[i + 1] << 8));
                int   v = Math.Clamp((int)(s * vol), short.MinValue, short.MaxValue);
                buf[i]     = (byte)(v & 0xFF);
                buf[i + 1] = (byte)((v >> 8) & 0xFF);
            }
        }

        /// <summary>RMS per channel, written lock-free for UI thread reads.</summary>
        private void ComputeLevels(byte[] buf, int count)
        {
            if (count < 4) return;
            double sumL = 0, sumR = 0;
            int    pairs = count / 4;
            for (int i = 0; i < count - 3; i += 4)
            {
                short sL = (short)(buf[i]     | (buf[i + 1] << 8));
                short sR = (short)(buf[i + 2] | (buf[i + 3] << 8));
                sumL += (double)sL * sL;
                sumR += (double)sR * sR;
            }
            if (pairs > 0)
            {
                Volatile.Write(ref _levelLeft,  (float)Math.Sqrt(sumL / pairs) / 32768f);
                Volatile.Write(ref _levelRight, (float)Math.Sqrt(sumR / pairs) / 32768f);
            }
        }

        private static void BoostProcessPriority()
        {
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                if (proc.PriorityClass < System.Diagnostics.ProcessPriorityClass.AboveNormal)
                    proc.PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal;
            }
            catch { /* non-fatal — may fail without elevated rights */ }
        }

        public void Dispose() => Stop();
    }

    // ─────────────────────────────────────────────────────────────────
    // RING BUFFER
    // Single-writer (capture thread) / single-reader (playback thread).
    // Implements IWaveProvider so WaveOutEvent.Init() accepts it directly
    // with no format conversion.
    // ─────────────────────────────────────────────────────────────────
    internal sealed class RingBufferProvider : IWaveProvider
    {
        private readonly byte[] _buf;
        private readonly int    _capacity;
        private readonly object _lock = new();
        private int _writePos, _readPos;

        public WaveFormat WaveFormat { get; }

        public int BufferedBytes
        {
            get
            {
                lock (_lock)
                    return (_writePos - _readPos + _capacity) % _capacity;
            }
        }

        public RingBufferProvider(WaveFormat fmt, int capacityBytes)
        {
            WaveFormat = fmt;
            _capacity  = capacityBytes;
            _buf       = new byte[capacityBytes];
        }

        public void PrimeSilence(int bytes)
        {
            lock (_lock)
            {
                bytes     = Math.Min(bytes, _capacity - 1);
                _writePos = (_writePos + bytes) % _capacity;
                // _buf is already zero-initialised — no explicit fill needed
            }
        }

        public void Write(byte[] data, int offset, int count)
        {
            lock (_lock)
            {
                for (int i = 0; i < count; i++)
                    _buf[(_writePos + i) % _capacity] = data[offset + i];
                _writePos = (_writePos + count) % _capacity;
            }
        }

        public void InsertSilence(int bytes)
        {
            lock (_lock)
            {
                // Calculate available space inside the lock to avoid the
                // BufferedBytes property acquiring the lock a second time.
                int buffered = (_writePos - _readPos + _capacity) % _capacity;
                int space    = _capacity - buffered - 1;
                bytes = Math.Min(bytes, space);
                if (bytes <= 0) return;
                for (int i = 0; i < bytes; i++)
                    _buf[(_writePos + i) % _capacity] = 0;
                _writePos = (_writePos + bytes) % _capacity;
            }
        }

        public void DropOldest(int bytes)
        {
            lock (_lock)
            {
                int buffered = (_writePos - _readPos + _capacity) % _capacity;
                bytes    = Math.Min(bytes, buffered);
                _readPos = (_readPos + bytes) % _capacity;
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                int avail  = (_writePos - _readPos + _capacity) % _capacity;
                int toRead = Math.Min(count, avail);

                for (int i = 0; i < toRead; i++)
                    buffer[offset + i] = _buf[(_readPos + i) % _capacity];
                _readPos = (_readPos + toRead) % _capacity;

                if (toRead < count)
                    Array.Clear(buffer, offset + toRead, count - toRead);

                return count;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────
    public sealed class AudioDevice
    {
        public int    Index { get; init; }
        public string Name  { get; init; } = string.Empty;
        public override string ToString() => Name;
    }
}
