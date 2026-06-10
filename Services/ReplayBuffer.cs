using System;
using System.Collections.Generic;

namespace RGDSCapture.Services
{
    /// <summary>
    /// Rolling buffer of timestamped Annex-B NAL units for instant replay.
    /// Always armed while a receiver runs — that is the whole point: the user
    /// never has to pre-start a recording. Memory cost is tiny (~8 MB per
    /// 30 s at the device's 2 Mbps bitrate).
    /// </summary>
    public sealed class ReplayBuffer
    {
        // Keep a little slack past the window so a save at t-N always has a
        // keyframe boundary to start from (GOP is ~333 ms).
        private const int EvictSlackSeconds = 5;

        private readonly object _lock = new();
        private readonly Queue<ReplayEntry> _entries = new();
        private int _capacitySeconds = 30;

        public int CapacitySeconds
        {
            get { lock (_lock) return _capacitySeconds; }
            set { lock (_lock) _capacitySeconds = Math.Clamp(value, 5, 300); }
        }

        public readonly record struct ReplayEntry(DateTime TsUtc, byte[] Nal);

        /// <summary>Called from the receive thread for every reassembled NAL.</summary>
        public void OnNal(byte[] nal)
        {
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                _entries.Enqueue(new ReplayEntry(now, nal));
                var cutoff = now.AddSeconds(-(_capacitySeconds + EvictSlackSeconds));
                while (_entries.Count > 0 && _entries.Peek().TsUtc < cutoff)
                    _entries.Dequeue();
            }
        }

        public ReplayEntry[] Snapshot()
        {
            lock (_lock) return _entries.ToArray();
        }

        public void Clear()
        {
            lock (_lock) _entries.Clear();
        }
    }

    /// <summary>
    /// Rolling buffer of timestamped Line-In PCM (48 kHz 16-bit stereo) so
    /// instant replays include audio. ~5.8 MB per 30 s — negligible.
    /// </summary>
    public sealed class AudioReplayBuffer
    {
        public const int BytesPerSecond = 48000 * 2 * 2;
        private const int EvictSlackSeconds = 5;

        private readonly object _lock = new();
        private readonly Queue<AudioChunk> _chunks = new();
        private int _capacitySeconds = 30;

        public int CapacitySeconds
        {
            get { lock (_lock) return _capacitySeconds; }
            set { lock (_lock) _capacitySeconds = Math.Clamp(value, 5, 300); }
        }

        public readonly record struct AudioChunk(DateTime TsUtc, byte[] Pcm);

        /// <summary>Called from the audio capture thread for each ~30 ms batch.</summary>
        public void OnPcm(byte[] pcm)
        {
            var now = DateTime.UtcNow;
            // The batch arrives when capture of it finishes; back-date the
            // timestamp to the chunk's first sample for alignment with video.
            var ts = now.AddSeconds(-(double)pcm.Length / BytesPerSecond);
            lock (_lock)
            {
                _chunks.Enqueue(new AudioChunk(ts, pcm));
                var cutoff = now.AddSeconds(-(_capacitySeconds + EvictSlackSeconds));
                while (_chunks.Count > 0 && _chunks.Peek().TsUtc < cutoff)
                    _chunks.Dequeue();
            }
        }

        public AudioChunk[] Snapshot()
        {
            lock (_lock) return _chunks.ToArray();
        }

        public void Clear()
        {
            lock (_lock) _chunks.Clear();
        }
    }
}
