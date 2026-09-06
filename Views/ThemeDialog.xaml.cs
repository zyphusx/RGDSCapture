using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RGDSCapture.Services;
using RGDSCapture.ViewModels;

namespace RGDSCapture.Views
{
    /// <summary>
    /// Theme picker: a grid of presets plus an RGB accent picker.
    ///
    /// Preset tiles apply immediately through their own command (the whole UI
    /// is DynamicResource-bound, so it re-skins live). The accent sliders only
    /// preview into the swatch until Apply is pressed — dragging a slider
    /// would otherwise rebuild the entire palette on every tick.
    /// </summary>
    public partial class ThemeDialog : Window
    {
        private readonly MainViewModel _vm;
        private bool _syncing;

        public ThemeDialog(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;

            LoadAccent(ThemeService.EffectiveAccent);

            // Picking a preset changes the accent out from under the sliders,
            // so follow the view-model rather than only reading it once.
            _vm.PropertyChanged += OnVmPropertyChanged;
            Closed += (_, _) => _vm.PropertyChanged -= OnVmPropertyChanged;

            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) Close();
            };

            DragHandle.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) DragMove();
            };
        }

        private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainViewModel.AccentHex)) return;

            // Don't yank the sliders while the user is mid-edit in the hex box.
            if (TxtHex.IsKeyboardFocusWithin) return;
            LoadAccent(ThemeService.EffectiveAccent);
        }

        private void LoadAccent(Color c)
        {
            _syncing = true;
            SldR.Value = c.R;
            SldG.Value = c.G;
            SldB.Value = c.B;
            _syncing = false;
            UpdatePreview();
        }

        private Color CurrentColor() => Color.FromRgb(
            (byte)SldR.Value, (byte)SldG.Value, (byte)SldB.Value);

        private void UpdatePreview()
        {
            var c = CurrentColor();
            AccentPreview.Background = new SolidColorBrush(c);
            if (!TxtHex.IsKeyboardFocusWithin)
                TxtHex.Text = PaletteBuilder.ToHex(c);
        }

        private void Channel_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncing) return;
            UpdatePreview();
        }

        private void Hex_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            CommitHex();
            e.Handled = true;
        }

        private void Hex_Commit(object sender, RoutedEventArgs e) => CommitHex();

        private void CommitHex()
        {
            string text = TxtHex.Text.Trim();
            if (text.Length == 0) return;
            if (!text.StartsWith('#')) text = "#" + text;

            // ParseHex falls back to grey on nonsense; treat that as "leave
            // the sliders alone" rather than silently jumping to grey.
            if (text.Length is not (7 or 4)) { UpdatePreview(); return; }

            var c = PaletteBuilder.ParseHex(text);
            LoadAccent(c);
        }

        private void BtnApplyAccent_Click(object sender, RoutedEventArgs e)
            => _vm.ApplyTheme(ThemeService.Current, CurrentColor());

        private void BtnClearAccent_Click(object sender, RoutedEventArgs e)
        {
            _vm.ApplyTheme(ThemeService.Current, null);
            LoadAccent(ThemeService.EffectiveAccent);
        }

        /// <summary>
        /// The theme shelves scroll sideways, but the wheel drives vertical
        /// scrolling by default — which does nothing here — so remap it.
        /// </summary>
        private void Shelf_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer shelf) return;

            shelf.ScrollToHorizontalOffset(shelf.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
