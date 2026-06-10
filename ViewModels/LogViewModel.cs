using System;
using System.Collections.ObjectModel;
using System.Windows;

namespace RGDSCapture.ViewModels
{
    public sealed class LogEntry
    {
        public string Timestamp { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public bool IsError { get; init; }
    }

    /// <summary>
    /// In-memory event log shown in the slide-out drawer. Thread-safe:
    /// appends from any thread are marshaled to the dispatcher.
    /// </summary>
    public sealed class LogViewModel : ObservableObject
    {
        private const int MaxEntries = 500;

        public ObservableCollection<LogEntry> Entries { get; } = new();

        private bool _isOpen;
        public bool IsOpen
        {
            get => _isOpen;
            set => SetProperty(ref _isOpen, value);
        }

        public RelayCommand ToggleCommand { get; }
        public RelayCommand ClearCommand { get; }

        public LogViewModel()
        {
            ToggleCommand = new RelayCommand(() => IsOpen = !IsOpen);
            ClearCommand = new RelayCommand(() =>
            {
                Entries.Clear();
                Append("Log cleared.");
            });
        }

        public void Append(string message, bool isError = false)
        {
            var app = Application.Current;
            if (app == null) return;

            if (!app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(() => Append(message, isError));
                return;
            }

            Entries.Add(new LogEntry
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                Message = message,
                IsError = isError
            });

            while (Entries.Count > MaxEntries)
                Entries.RemoveAt(0);
        }
    }
}
