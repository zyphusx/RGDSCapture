using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RGDSCapture.Core;
using RGDSCapture.Services;

namespace RGDSCapture.ViewModels
{
    /// <summary>
    /// Everything about one DS screen: the live bitmap, stream health badge,
    /// FPS readout and recording state. The receive/decode thread hands frames
    /// to <see cref="OnFrameReady"/>; the UI render tick pulls the latest one
    /// via <see cref="RenderPendingFrame"/>. One reused pending buffer means
    /// zero steady-state allocation.
    /// </summary>
    public sealed class ScreenViewModel : ObservableObject, IDisposable
    {
        private static readonly Brush BadgeLiveBrush =
            Frozen(new SolidColorBrush(Color.FromRgb(0x13, 0xA1, 0x0E)));
        private static readonly Brush BadgeFrozenBrush =
            Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)));
        private static readonly Brush BadgeRecoveringBrush =
            Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)));
        private static readonly Brush BadgeWaitingBrush =
            Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));

        public ScreenId Id { get; }
        public string DisplayName { get; }
        public RtpStreamReceiver Receiver { get; }

        private readonly Action<string, bool> _log;

        // Latest decoded frame, handed off from the decode thread.
        private readonly object _frameLock = new();
        private byte[]? _pending;
        private int _pendingW, _pendingH;
        private bool _hasPending;

        private WriteableBitmap? _bitmap;
        public WriteableBitmap? Bitmap
        {
            get => _bitmap;
            private set => SetProperty(ref _bitmap, value);
        }

        private StreamHealth _health = StreamHealth.Waiting;
        public StreamHealth Health
        {
            get => _health;
            set
            {
                if (SetProperty(ref _health, value))
                {
                    OnPropertyChanged(nameof(BadgeText));
                    OnPropertyChanged(nameof(BadgeBrush));
                }
            }
        }

        public string BadgeText => Health switch
        {
            StreamHealth.Live => "● LIVE",
            StreamHealth.Frozen => "● FROZEN",
            StreamHealth.Recovering => "● RECOVERING",
            _ => "○ WAITING"
        };

        public Brush BadgeBrush => Health switch
        {
            StreamHealth.Live => BadgeLiveBrush,
            StreamHealth.Frozen => BadgeFrozenBrush,
            StreamHealth.Recovering => BadgeRecoveringBrush,
            _ => BadgeWaitingBrush
        };

        private string _fpsText = string.Empty;
        public string FpsText
        {
            get => _fpsText;
            set => SetProperty(ref _fpsText, value);
        }

        /// <summary>Network stats overlay line; empty hides the overlay.</summary>
        private string _statsText = string.Empty;
        public string StatsText
        {
            get => _statsText;
            set => SetProperty(ref _statsText, value);
        }

        /// <summary>"● REC mm:ss" indicator while recording; empty hides it.</summary>
        private string _recText = string.Empty;
        public string RecText
        {
            get => _recText;
            set => SetProperty(ref _recText, value);
        }

        public DateTime RecordingStartUtc { get; private set; }

        // ── Recording ─────────────────────────────────────────────────
        private RecordingSession? _session;

        private bool _isRecording;
        public bool IsRecording
        {
            get => _isRecording;
            private set
            {
                if (SetProperty(ref _isRecording, value))
                    OnPropertyChanged(nameof(RecordButtonText));
            }
        }

        public string RecordButtonText =>
            IsRecording ? "⏹  Stop" : "⏺  " + (Id == ScreenId.Top ? "Top" : "Bottom");

        public AsyncRelayCommand ToggleRecordingCommand { get; }

        public ScreenViewModel(ScreenId id, int port, Action<string, bool> log)
        {
            Id = id;
            DisplayName = id == ScreenId.Top ? "Top Screen" : "Bottom Screen";
            _log = log;

            Receiver = new RtpStreamReceiver(port);
            Receiver.FrameReady += OnFrameReady;

            ToggleRecordingCommand = new AsyncRelayCommand(ToggleRecordingAsync);
        }

        // ── Frame flow ────────────────────────────────────────────────
        private void OnFrameReady(byte[] buffer, int w, int h)
        {
            // Decode thread: copy into the reused pending buffer.
            lock (_frameLock)
            {
                int size = w * 4 * h;
                if (_pending == null || _pending.Length != size)
                    _pending = new byte[size];
                Buffer.BlockCopy(buffer, 0, _pending, 0, size);
                _pendingW = w;
                _pendingH = h;
                _hasPending = true;
            }
        }

        /// <summary>UI thread: pushes the newest pending frame into the bitmap.</summary>
        public void RenderPendingFrame()
        {
            byte[] data;
            int w, h;

            lock (_frameLock)
            {
                if (!_hasPending || _pending == null) return;
                data = _pending;
                w = _pendingW;
                h = _pendingH;
                _hasPending = false;

                if (Bitmap == null || Bitmap.PixelWidth != w || Bitmap.PixelHeight != h)
                    Bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);

                // WritePixels inside the lock: the decode thread writes _pending
                // on its next frame, so the copy must finish first.
                Bitmap.WritePixels(new Int32Rect(0, 0, w, h), data, w * 4, 0);
            }
        }

        public void ClearFrame()
        {
            lock (_frameLock)
            {
                _hasPending = false;
                _pending = null;
            }
            Bitmap = null;
        }

        // ── Recording ─────────────────────────────────────────────────
        private async Task ToggleRecordingAsync()
        {
            if (IsRecording) await StopRecordingAsync();
            else StartRecording();
        }

        private void StartRecording()
        {
            if (!Receiver.IsRunning)
            {
                _log("[RECORD] Stream is not running.", true);
                return;
            }

            var session = RecordingService.Start(Id, Receiver, _log);
            if (session == null) return;

            session.Failed += OnRecordingFailed;
            _session = session;
            RecordingStartUtc = DateTime.UtcNow;
            RecText = "● REC 00:00";
            IsRecording = true;
        }

        public async Task StopRecordingAsync()
        {
            var session = _session;
            _session = null;
            if (session != null)
            {
                session.Failed -= OnRecordingFailed;
                await session.StopAsync();
                session.Dispose();
            }
            IsRecording = false;
            RecText = string.Empty;
        }

        private void OnRecordingFailed()
        {
            _log($"[RECORD] {DisplayName} recording stopped unexpectedly (ffmpeg exited).", true);
            Application.Current?.Dispatcher.BeginInvoke(async () =>
            {
                var session = _session;
                _session = null;
                IsRecording = false;
                RecText = string.Empty;
                if (session != null)
                {
                    session.Failed -= OnRecordingFailed;
                    await Task.Run(session.Dispose);
                }
            });
        }

        public void Dispose()
        {
            Receiver.FrameReady -= OnFrameReady;
            _session?.Dispose();
            _session = null;
            Receiver.Dispose();
        }

        private static Brush Frozen(Brush b)
        {
            b.Freeze();
            return b;
        }
    }
}
