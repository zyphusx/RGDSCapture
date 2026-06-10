using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using RGDSCapture.Core;
using RGDSCapture.Services;

namespace RGDSCapture.ViewModels
{
    /// <summary>
    /// Line-In passthrough controls: device selection, start/stop,
    /// volume and VU meters. Independent of the SSH connection — audio
    /// arrives over a physical 3.5mm cable, not the network.
    /// </summary>
    public sealed class AudioViewModel : ObservableObject, IDisposable
    {
        private static readonly Brush VuGreen =
            new SolidColorBrush(Color.FromRgb(0x13, 0xA1, 0x0E));
        private static readonly Brush VuOrange =
            new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));
        private static readonly Brush VuRed =
            new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));

        private readonly AudioPassthrough _engine = new();
        private readonly DispatcherTimer _vuTimer;
        private readonly Action<string, bool> _log;
        private readonly AppSettings _settings;

        public ObservableCollection<AudioDeviceInfo> InputDevices { get; } = new();
        public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new();

        private AudioDeviceInfo? _selectedInput;
        public AudioDeviceInfo? SelectedInput
        {
            get => _selectedInput;
            set => SetProperty(ref _selectedInput, value);
        }

        private AudioDeviceInfo? _selectedOutput;
        public AudioDeviceInfo? SelectedOutput
        {
            get => _selectedOutput;
            set => SetProperty(ref _selectedOutput, value);
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (SetProperty(ref _isRunning, value))
                    OnPropertyChanged(nameof(ToggleText));
            }
        }

        public string ToggleText => IsRunning ? "⏹  Audio" : "▶  Audio";

        private double _volume;
        public double Volume
        {
            get => _volume;
            set
            {
                if (SetProperty(ref _volume, value))
                {
                    _engine.Volume = (float)value;
                    _settings.Volume = value;
                    OnPropertyChanged(nameof(VolumeLabel));
                }
            }
        }

        public string VolumeLabel => $"{(int)(Volume * 100)}%";

        private double _leftLevel, _rightLevel;
        public double LeftLevel
        {
            get => _leftLevel;
            private set => SetProperty(ref _leftLevel, value);
        }
        public double RightLevel
        {
            get => _rightLevel;
            private set => SetProperty(ref _rightLevel, value);
        }

        private Brush _vuBrush = VuGreen;
        public Brush VuBrush
        {
            get => _vuBrush;
            private set => SetProperty(ref _vuBrush, value);
        }

        public RelayCommand ToggleCommand { get; }

        public AudioViewModel(AppSettings settings, Action<string, bool> log)
        {
            _settings = settings;
            _log = log;
            _volume = Math.Clamp(settings.Volume, 0.0, 1.0);

            _vuTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(40)
            };
            _vuTimer.Tick += (_, _) => UpdateVu();

            ToggleCommand = new RelayCommand(Toggle);
            PopulateDevices();
        }

        private void PopulateDevices()
        {
            foreach (var d in AudioPassthrough.GetInputDevices()) InputDevices.Add(d);
            foreach (var d in AudioPassthrough.GetOutputDevices()) OutputDevices.Add(d);

            // Restore by saved name first; otherwise guess the Line-In jack.
            SelectedInput =
                InputDevices.FirstOrDefault(d => d.Name == _settings.AudioInputName)
                ?? InputDevices.FirstOrDefault(d =>
                {
                    string n = d.Name.ToLowerInvariant();
                    return n.Contains("line") || n.Contains("stereo mix") || n.Contains("aux");
                })
                ?? InputDevices.FirstOrDefault();

            SelectedOutput =
                OutputDevices.FirstOrDefault(d => d.Name == _settings.AudioOutputName)
                ?? OutputDevices.FirstOrDefault();

            if (InputDevices.Count == 0)
                _log("[AUDIO] No input devices found — check sound settings.", true);
        }

        private void Toggle()
        {
            if (IsRunning) Stop();
            else Start();
        }

        private void Start()
        {
            if (SelectedInput == null)
            {
                _log("[AUDIO] No input device selected.", true);
                return;
            }

            try
            {
                _engine.Start(SelectedInput.Index, SelectedOutput?.Index ?? -1);
                _engine.Volume = (float)Volume;
                IsRunning = true;
                _vuTimer.Start();

                _settings.AudioInputName = SelectedInput.Name;
                _settings.AudioOutputName = SelectedOutput?.Name;

                _log($"[AUDIO] Line-In started — {SelectedInput.Name} → " +
                     $"{SelectedOutput?.Name ?? "System Default"}", false);
            }
            catch (Exception ex)
            {
                _log($"[AUDIO] Failed to start: {ex.Message}", true);
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            _vuTimer.Stop();
            _engine.Stop();
            IsRunning = false;
            LeftLevel = 0;
            RightLevel = 0;
            _log("[AUDIO] Line-In monitoring stopped.", false);
        }

        private void UpdateVu()
        {
            if (!IsRunning) return;

            // RMS levels are perceptually small; ×3 gives a useful meter range.
            LeftLevel = Math.Clamp(_engine.LevelLeft * 3.0, 0.0, 1.0);
            RightLevel = Math.Clamp(_engine.LevelRight * 3.0, 0.0, 1.0);

            float peak = Math.Max(_engine.LevelLeft, _engine.LevelRight);
            VuBrush = peak < 0.6f ? VuGreen : peak < 0.85f ? VuOrange : VuRed;
        }

        public void Dispose()
        {
            _vuTimer.Stop();
            _engine.Dispose();
        }
    }
}
