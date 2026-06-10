using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using RGDSCapture.ViewModels;

namespace RGDSCapture.Views.Controls
{
    public partial class LogDrawer : UserControl
    {
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
            if (e.Action == NotifyCollectionChangedAction.Add)
                LogScroll.ScrollToEnd();
        }
    }
}
