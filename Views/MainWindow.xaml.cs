using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using RGDSCapture.Core;
using RGDSCapture.Services;
using RGDSCapture.ViewModels;

namespace RGDSCapture.Views
{
    /// <summary>
    /// Thin shell: wires the MainViewModel to view-only concerns
    /// (window chrome, dialogs, layout grid spans, fullscreen window,
    /// Space shortcut, shutdown sequencing). All behavior lives in the
    /// view-models.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private FullScreenWindow? _fullscreen;
        private bool _shutdownStarted;
        private bool _shutdownComplete;

        // Caption glyphs: a single square when restorable, two offset
        // squares when already maximized.
        private static readonly Geometry MaximizeGeometry =
            Geometry.Parse("M 0.5 0.5 H 9.5 V 9.5 H 0.5 Z");
        private static readonly Geometry RestoreGeometry =
            Geometry.Parse("M 2.5 0.5 H 9.5 V 7.5 M 0.5 2.5 H 7.5 V 9.5 H 0.5 Z");

        // The UI-scale transform stops at RootBorder. The window's own
        // minimum size and the WindowChrome caption band are in unscaled
        // window coordinates, so they are recomputed from these baselines
        // whenever the factor changes — otherwise the draggable caption
        // strip drifts away from the title bar it is meant to cover.
        private readonly double _baseMinWidth;
        private readonly double _baseMinHeight;
        private readonly double _baseCaptionHeight;

        public MainWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;

            _baseMinWidth = MinWidth;
            _baseMinHeight = MinHeight;
            _baseCaptionHeight = WindowChrome.GetWindowChrome(this)?.CaptionHeight ?? 0;

            // Raising the minimum also grows a too-small window, so switching
            // to a larger factor makes room for itself.
            OnUiScaleChanged(UiScaleService.Current);
            UiScaleService.Changed += OnUiScaleChanged;
            Closed += (_, _) => UiScaleService.Changed -= OnUiScaleChanged;

            vm.PromptCredentials = ShowCredentialDialog;
            vm.Confirm = (message, title) =>
                MessageBox.Show(this, message, title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            vm.FullscreenRequested += OpenFullscreen;
            vm.ThemePickerRequested += OpenThemePicker;
            vm.PropertyChanged += OnVmPropertyChanged;

            PreviewKeyDown += OnPreviewKeyDown;
            Closing += OnClosingAsync;
            SourceInitialized += OnSourceInitialized;
            StateChanged += (_, _) => UpdateMaximizeGlyph();
            Loaded += (_, _) =>
            {
                ApplyLayout();
                ApplyPanelState();
                UpdateMaximizeGlyph();
            };
        }

        // ── UI scale ──────────────────────────────────────────────
        private void OnUiScaleChanged(double scale)
        {
            var workArea = ScreenMetrics.WorkArea(this);
            MinWidth = FitOnScreen(_baseMinWidth * scale, workArea.Width);
            MinHeight = FitOnScreen(_baseMinHeight * scale, workArea.Height);

            // The caption strip is the title bar's drag / snap region, so it
            // has to grow with the title bar row inside the scaled tree.
            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null) chrome.CaptionHeight = _baseCaptionHeight * scale;
        }

        /// <summary>
        /// Caps a scaled minimum to the monitor: 175% of the 620px floor is
        /// taller than a 1080p work area, and a window whose minimum exceeds
        /// the screen cannot be positioned sensibly at all. Content gets
        /// tighter past that point rather than the window becoming unusable.
        /// </summary>
        private static double FitOnScreen(double desired, double available)
            => Math.Min(desired, available * 0.95);

        // ── Window chrome ─────────────────────────────────────────
        // A WindowStyle=None window maximizes to the full monitor
        // rectangle by default, covering the taskbar. Clamping
        // WM_GETMINMAXINFO to the monitor's work area fixes that, and
        // does it per-monitor so it stays correct on mixed-DPI setups.
        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
        }

        private static IntPtr WindowProc(
            IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            if (msg == WM_GETMINMAXINFO)
            {
                ClampToWorkArea(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private static void ClampToWorkArea(IntPtr hwnd, IntPtr lParam)
        {
            if (!ScreenMetrics.TryGetMonitorInfo(hwnd, out var info)) return;

            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

            // Work area, expressed relative to the monitor's own origin.
            mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
            mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
            mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
            mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;

            Marshal.StructureToPtr(mmi, lParam, true);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        private void UpdateMaximizeGlyph()
        {
            bool maximized = WindowState == WindowState.Maximized;
            MaximizeGlyph.Data = maximized ? RestoreGeometry : MaximizeGeometry;
            BtnMaximize.ToolTip = maximized ? "Restore Down" : "Maximize";
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // ── Sidebar columns ───────────────────────────────────────
        // The open state is animated by XAML triggers, which only fire on
        // a *change*. A panel that starts collapsed therefore needs its
        // width zeroed once up front.
        private void ApplyPanelState()
        {
            if (!_vm.IsLeftPanelOpen) LeftPanelHost.Width = 0;
            if (!_vm.IsRightPanelOpen) RightPanelHost.Width = 0;
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

        // ── Theme picker ──────────────────────────────────────────
        private ThemeDialog? _themeDialog;

        private void OpenThemePicker()
        {
            // Modeless: presets apply live, so the user wants to see the app
            // behind the picker change as they click through them.
            if (_themeDialog != null)
            {
                _themeDialog.Activate();
                return;
            }

            _themeDialog = new ThemeDialog(_vm) { Owner = this };
            _themeDialog.Closed += (_, _) => _themeDialog = null;
            _themeDialog.Show();
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
