using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using RGDSCapture.ViewModels;

namespace RGDSCapture.Views.Controls
{
    /// <summary>
    /// The event log panel. Its one job beyond the XAML is to keep the view
    /// pinned to the newest entry, which data binding alone cannot do — a
    /// ScrollViewer has no notion of "follow the tail".
    /// </summary>
    public partial class LogDrawer : UserControl
    {
        // Tracked so the previous log can be unsubscribed. The DataContext is
        // set after construction and can be replaced later, so subscribing
        // once in the constructor would attach to nothing, and subscribing on
        // every change without detaching would leak handlers onto a log that
        // is no longer displayed.
        private LogViewModel? _attachedLog;

        public LogDrawer()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_attachedLog != null)
                ((INotifyCollectionChanged)_attachedLog.Entries).CollectionChanged -= OnEntriesChanged;

            _attachedLog = (DataContext as MainViewModel)?.Log;

            if (_attachedLog != null)
                ((INotifyCollectionChanged)_attachedLog.Entries).CollectionChanged += OnEntriesChanged;
        }

        private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Only follow appends. The log trims its own backlog, and
            // scrolling to the end on a Remove would fight the user every
            // time they scrolled back to read something.
            if (e.Action == NotifyCollectionChangedAction.Add)
                LogScroll.ScrollToEnd();
        }
    }
}
