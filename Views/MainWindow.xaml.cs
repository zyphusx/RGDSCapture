using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using RGDSCapture.Core;
using RGDSCapture.ViewModels;

namespace RGDSCapture.Views
{
    /// <summary>
    /// Thin shell: wires the MainViewModel to view-only concerns
    /// (dialogs, layout grid spans, fullscreen window, Space shortcut,
    /// shutdown sequencing). All behavior lives in the view-models.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private FullScreenWindow? _fullscreen;
        private bool _shutdownStarted;
        private bool _shutdownComplete;

        public MainWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;

            vm.PromptCredentials = ShowCredentialDialog;
            vm.Confirm = (message, title) =>
                MessageBox.Show(this, message, title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            vm.FullscreenRequested += OpenFullscreen;
            vm.PropertyChanged += OnVmPropertyChanged;

            PreviewKeyDown += OnPreviewKeyDown;
            Closing += OnClosingAsync;
            Loaded += (_, _) => ApplyLayout();
        }

        // ── Credential dialog ─────────────────────────────────────
        private (string User, string Pass, bool Remember)? ShowCredentialDialog(string defaultUsername)
        {
            var dialog = new ConnectDialog(defaultUsername, _vm.Settings.RememberCredentials)
            {
                Owner = this
            };
            return dialog.ShowDialog() == true
                ? (dialog.Username, dialog.Password, dialog.Remember)
                : null;
        }

        // ── Layout switching ──────────────────────────────────────
        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MainViewModel.Layout)
                or nameof(MainViewModel.IsSwapped)
                or nameof(MainViewModel.ScreenGap))
                ApplyLayout();
        }

        private void ApplyLayout()
        {
            static void Place(FrameworkElement el, int row, int col, int rowSpan, int colSpan, bool visible)
            {
                Grid.SetRow(el, row);
                Grid.SetColumn(el, col);
                Grid.SetRowSpan(el, rowSpan);
                Grid.SetColumnSpan(el, colSpan);
                el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }

            // Swap exchanges the screens' positions in the two-screen
            // layouts; Top Only / Bottom Only stay literal.
            var first = _vm.IsSwapped ? BottomView : TopView;
            var second = _vm.IsSwapped ? TopView : BottomView;

            var gap = new Thickness(_vm.ScreenGap);
            TopView.Margin = gap;
            BottomView.Margin = gap;

            VideoCol0.Width = new GridLength(1, GridUnitType.Star);
            VideoCol1.Width = new GridLength(1, GridUnitType.Star);

            switch (_vm.Layout)
            {
                case LayoutMode.VerticalStack:
                    Place(first, 0, 0, 1, 2, true);
                    Place(second, 1, 0, 1, 2, true);
                    break;
                case LayoutMode.SideBySide:
                    Place(first, 0, 0, 2, 1, true);
                    Place(second, 0, 1, 2, 1, true);
                    break;
                case LayoutMode.TopOnly:
                    Place(TopView, 0, 0, 2, 2, true);
                    Place(BottomView, 0, 0, 1, 1, false);
                    break;
                case LayoutMode.BottomOnly:
                    Place(BottomView, 0, 0, 2, 2, true);
                    Place(TopView, 0, 0, 1, 1, false);
                    break;
                case LayoutMode.Hybrid:
                    // Primary screen large on the left (2/3 width), the
                    // other small in the bottom-right corner.
                    VideoCol0.Width = new GridLength(2, GridUnitType.Star);
                    Place(first, 0, 0, 2, 1, true);
                    Place(second, 1, 1, 1, 1, true);
                    break;
            }
        }

        // ── Fullscreen ────────────────────────────────────────────
        private void OpenFullscreen(ScreenViewModel screen)
        {
            if (_fullscreen != null) return;

            _fullscreen = new FullScreenWindow(_vm, screen) { Owner = this };
            _fullscreen.Closed += (_, _) => _fullscreen = null;
            _fullscreen.Show();
        }

        // ── Keyboard: Space toggles the speedrun timer ────────────
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space) return;
            if (Keyboard.FocusedElement is TextBoxBase or PasswordBox) return;
            _vm.Timer.Toggle();
            e.Handled = true;
        }

        // ── Menu ──────────────────────────────────────────────────
        private void MnuExit_Click(object sender, RoutedEventArgs e) => Close();

        // ── Shutdown sequencing ───────────────────────────────────
        // WPF's Closing event is synchronous, but our teardown (stop
        // recordings, SSH cleanup) is async. So: cancel the first close,
        // run teardown, then close for real.
        private async void OnClosingAsync(object? sender, CancelEventArgs e)
        {
            if (_shutdownComplete) return;

            if (_vm.IsConnected && !_shutdownStarted)
            {
                var result = MessageBox.Show(this,
                    "Streams are currently running on the DS.\n\n" +
                    "Closing will stop all GStreamer pipelines and disconnect SSH.\n\nExit anyway?",
                    "Confirm Exit", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            e.Cancel = true;
            if (_shutdownStarted) return;
            _shutdownStarted = true;

            // Get off the Closing call stack first: if teardown completes
            // synchronously, calling Close() from inside the Closing event
            // throws InvalidOperationException.
            await Task.Yield();

            _fullscreen?.Close();
            await _vm.ShutdownAsync();

            _shutdownComplete = true;
            // Shutdown, not Close(): WPF pumps messages during a close, so
            // this continuation can run while the original close is still in
            // flight — Close() would throw "while a Window is closing".
            // Shutdown closes windows ignoring the cancel handshake.
            Application.Current.Shutdown();
        }
    }
}
