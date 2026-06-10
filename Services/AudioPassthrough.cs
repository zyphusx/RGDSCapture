using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;

namespace RGDSCapture.Services
{
    /// <summary>
    /// Low-latency Line-In passthrough: WaveInEvent → ring buffer → WaveOutEvent.
    ///
    /// Audio stays 48 kHz 16-bit stereo PCM end to end (no format conversion).
    /// Volume is applied in-place in the capture callback. Drift correction
    /// keeps the buffer near the 80 ms target: too full → drop oldest,
    /// too empty → insert silence. Prevents both latency creep and
    /// starve-clicks.
    /// </summary>
    public sealed class AudioPassthrough : IDisposable
    {
        private const int CaptureBufMs = 30;   // WMME reliable minimum
        private const int TargetFillMs = 80;   // ideal buffer ahead of playback
        private const int MaxFillMs = 160;     // above this → drop oldest
        private const int MinFillMs = 20;      // below this → insert silence
        private const int RingBufMs = 2000;    // total ring capacity

        public bool IsRunning { get; private set; }

        private float _volume = 0.85f;
        public float Volume
        {
            get => _volume;
            set => _volume = Math.Clamp(value, 0f, 1f);
        }

        private float _levelLeft, _levelRight;
        public float LevelLeft => Volatile.Read(ref _levelLeft);
        public float LevelRight => Volatile.Read(ref _levelRight);

        private WaveInEvent? _input;
        private WaveOutEvent? _output;
        private RingBufferProvider? _ring;
        private int _targetFillBytes, _maxFillBytes, _minFillBytes;

        // ── Device enumeration ────────────────────────────────────────
        public static List<AudioDeviceInfo> GetInputDevices()
        {
            var list = new List<AudioDeviceInfo>();
            for (int i = 0; i < WaveIn.DeviceCount; i++)
                list.Add(new AudioDeviceInfo(i, WaveIn.GetCapabilities(i).ProductName));
            return list;
        }

        public static List<AudioDeviceInfo> GetOutputDevices()
        {
            var list = new List<AudioDeviceInfo>
            {
                new(-1, "System Default")
            };
            for (int i = 0; i < WaveOut.DeviceCount; i++)
                list.Add(new AudioDeviceInfo(i, WaveOut.GetCapabilities(i).ProductName));
            return list;
        }

        // ─────────────────────────────────────────────────────────────
        public void Start(int inputDevice, int outputDevice)
        {
            if (IsRunning) return;

            var fmt = new WaveFormat(48000, 16, 2);
            int bytesPerMs = fmt.AverageBytesPerSecond / 1000;
            _targetFillBytes = TargetFillMs * bytesPerMs;
            _maxFillBytes = MaxFillMs * bytesPerMs;
            _minFillBytes = MinFillMs * bytesPerMs;

            _ring = new RingBufferProvider(fmt, RingBufMs * bytesPerMs);
            _ring.PrimeSilence(_targetFillBytes);

            try
            {
                _output = new WaveOutEvent
                {
                    DeviceNumber = outputDevice,
                    DesiredLatency = TargetFillMs,
                    NumberOfBuffers = 3
                };
                _output.Init(_ring);
                _output.Play();

                _input = new WaveInEvent
                {
                    DeviceNumber = inputDevice,
                    WaveFormat = fmt,
                    BufferMilliseconds = CaptureBufMs,
                    NumberOfBuffers = 3
                };
                _input.DataAvailable += OnDataAvailable;
                _input.StartRecording();
            }
            catch
            {
                // Roll back partial initialization so a failed Start
                // doesn't leak a playing output device.
                TearDown();
                throw;
            }

            BoostProcessPriority();
            IsRunning = true;
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            TearDown();
        }

        public void Dispose() => Stop();

        private void TearDown()
        {
            try { _input?.StopRecording(); } catch { }
            try { _output?.Stop(); } catch { }
            _input?.Dispose();
            _output?.Dispose();
            _input = null;
            _output = null;
            _ring = null;
            Volatile.Write(ref _levelLeft, 0f);
            Volatile.Write(ref _levelRight, 0f);
        }

        // ── Capture callback — runs on WMME thread, must stay fast ────
        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            var ring = _ring;
            if (ring == null || e.BytesRecorded == 0) return;

            float vol = _volume;
            if (Math.Abs(vol - 1.0f) > 0.001f)
                ApplyVolume(e.Buffer, e.BytesRecorded, vol);

            int fill = ring.BufferedBytes;
            if (fill > _maxFillBytes)
                ring.DropOldest(fill - _targetFillBytes);
            else if (fill < _minFillBytes)
                ring.InsertSilence(_targetFillBytes - fill);

            ring.Write(e.Buffer, 0, e.BytesRecorded);
            ComputeLevels(e.Buffer, e.BytesRecorded);
        }

        /// <summary>Scale 16-bit PCM samples in place, clamped to prevent wrap.</summary>
        private static void ApplyVolume(byte[] buf, int count, float vol)
        {
            for (int i = 0; i < count - 1; i += 2)
            {
                short s = (short)(buf[i] | (buf[i + 1] << 8));
                int v = Math.Clamp((int)(s * vol), short.MinValue, short.MaxValue);
                buf[i] = (byte)(v & 0xFF);
                buf[i + 1] = (byte)((v >> 8) & 0xFF);
            }
        }

        /// <summary>Per-channel RMS, written lock-free for UI reads.</summary>
        private void ComputeLevels(byte[] buf, int count)
        {
            if (count < 4) return;
            double sumL = 0, sumR = 0;
            int pairs = count / 4;
            for (int i = 0; i < count - 3; i += 4)
            {
                short sL = (short)(buf[i] | (buf[i + 1] << 8));
                short sR = (short)(buf[i + 2] | (buf[i + 3] << 8));
                sumL += (double)sL * sL;
                sumR += (double)sR * sR;
            }
            if (pairs > 0)
            {
                Volatile.Write(ref _levelLeft, (float)Math.Sqrt(sumL / pairs) / 32768f);
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
            catch
            {
                // Non-fatal — may fail without elevated rights.
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// Single-writer / single-reader ring buffer implementing IWaveProvider,
    /// so WaveOutEvent consumes it directly with no conversion chain.
    /// </summary>
    internal sealed class RingBufferProvider : IWaveProvider
    {
        private readonly byte[] _buf;
        private readonly int _capacity;
        private readonly object _lock = new();
        private int _writePos, _readPos;

        public WaveFormat WaveFormat { get; }

        public RingBufferProvider(WaveFormat fmt, int capacityBytes)
        {
            WaveFormat = fmt;
            _capacity = capacityBytes;
            _buf = new byte[capacityBytes];
        }

        public int BufferedBytes
        {
            get
            {
                lock (_lock)
                    return (_writePos - _readPos + _capacity) % _capacity;
            }
        }

        public void PrimeSilence(int bytes)
        {
            lock (_lock)
            {
                bytes = Math.Min(bytes, _capacity - 1);
                _writePos = (_writePos + bytes) % _capacity;
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
                int buffered = (_writePos - _readPos + _capacity) % _capacity;
                int space = _capacity - buffered - 1;
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
                bytes = Math.Min(bytes, buffered);
                _readPos = (_readPos + bytes) % _capacity;
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                int avail = (_writePos - _readPos + _capacity) % _capacity;
                int toRead = Math.Min(count, avail);

                for (int i = 0; i < toRead; i++)
                    buffer[offset + i] = _buf[(_readPos + i) % _capacity];
                _readPos = (_readPos + toRead) % _capacity;

                // Pad shortfall with silence rather than starving the device.
                if (toRead < count)
                    Array.Clear(buffer, offset + toRead, count - toRead);

                return count;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────
    public sealed record AudioDeviceInfo(int Index, string Name)
    {
        public override string ToString() => Name;
    }
}
