using System;
using System.Diagnostics;
using System.Windows.Threading;

namespace RGDSCapture.ViewModels
{
    /// <summary>
    /// Millisecond-precision speedrun timer. The display timer only runs
    /// while the stopwatch runs — no idle ticking.
    /// </summary>
    public sealed class TimerViewModel : ObservableObject
    {
        private readonly Stopwatch _watch = new();
        private readonly DispatcherTimer _displayTimer;
        private readonly Action<string, bool> _log;
        private TimeSpan _offset = TimeSpan.Zero;

        private string _display = "00:00.000";
        public string Display
        {
            get => _display;
            private set => SetProperty(ref _display, value);
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (SetProperty(ref _isRunning, value))
                    OnPropertyChanged(nameof(StartPauseText));
            }
        }

        public string StartPauseText => IsRunning ? "⏸  Pause" : "▶  Start";

        public RelayCommand StartPauseCommand { get; }
        public RelayCommand LapCommand { get; }
        public RelayCommand ResetCommand { get; }

        public TimerViewModel(Action<string, bool> log)
        {
            _log = log;
            _displayTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _displayTimer.Tick += (_, _) => Display = Format(Elapsed);

            StartPauseCommand = new RelayCommand(Toggle);
            LapCommand = new RelayCommand(Lap);
            ResetCommand = new RelayCommand(Reset);
        }

        private TimeSpan Elapsed => _offset + _watch.Elapsed;

        public void Toggle()
        {
            if (!IsRunning)
            {
                _watch.Start();
                _displayTimer.Start();
                IsRunning = true;
                _log("[TIMER] Started.", false);
            }
            else
            {
                _offset += _watch.Elapsed;
                _watch.Reset();
                _displayTimer.Stop();
                IsRunning = false;
                Display = Format(_offset);
                _log($"[TIMER] Paused at {Format(_offset)}", false);
            }
        }

        private void Lap()
            => _log($"[LAP]  {Format(Elapsed)}", false);

        private void Reset()
        {
            _watch.Reset();
            _displayTimer.Stop();
            _offset = TimeSpan.Zero;
            IsRunning = false;
            Display = "00:00.000";
            _log("[TIMER] Reset.", false);
        }

        public void Shutdown() => _displayTimer.Stop();

        private static string Format(TimeSpan t) =>
            t.TotalHours >= 1
                ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}"
                : $"{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
    }
}
