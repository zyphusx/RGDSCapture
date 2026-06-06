using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Automation;

namespace RGDSCapture
{
    /// <summary>
    /// Borderless fullscreen window that displays a live DS screen.
    ///
    /// A Func&lt;WriteableBitmap?&gt; delegate is passed in so the window always
    /// reads the latest bitmap pointer — safe even if the bitmap is replaced
    /// when stream resolution changes.
    ///
    /// Close: Escape key, double-click, or the on-screen close button.
    /// </summary>
    public sealed class FullScreenWindow : Window
    {
        private readonly Func<WriteableBitmap?> _bitmapSource;
        private readonly Image                  _image;
        private readonly DispatcherTimer        _timer;

        public FullScreenWindow(Func<WriteableBitmap?> bitmapSource, string screenLabel)
        {
            _bitmapSource = bitmapSource;

            // ── Window chrome ─────────────────────────────────────────
            WindowStyle       = WindowStyle.None;
            WindowState       = WindowState.Maximized;
            ResizeMode        = ResizeMode.NoResize;
            Background        = Brushes.Black;
            Topmost           = true;
            ShowInTaskbar     = true;
            Title             = $"RGDSCapture — {screenLabel} (Fullscreen)";

            AutomationProperties.SetName(this, $"{screenLabel} fullscreen view");

            // ── Layout ────────────────────────────────────────────────
            var root = new Grid();

            // Video image — fills the entire window, letterboxed
            _image = new Image
{
    Stretch = Stretch.Uniform,
    HorizontalAlignment = HorizontalAlignment.Center,
    VerticalAlignment = VerticalAlignment.Center
};

RenderOptions.SetBitmapScalingMode(
    _image,
    BitmapScalingMode.NearestNeighbor);
    
            AutomationProperties.SetName(_image, $"{screenLabel} video");
            root.Children.Add(_image);

            // Label in top-left (fades after 3 s)
            var labelBorder = new Border
            {
                Background          = new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0)),
                CornerRadius        = new CornerRadius(6),
                Padding             = new Thickness(12, 6, 12, 6),
                Margin              = new Thickness(16, 16, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Top,
                Child               = new TextBlock
                {
                    Text       = screenLabel.ToUpperInvariant(),
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, sans-serif"),
                    FontSize   = 14,
                    FontWeight = FontWeights.SemiBold
                }
            };
            root.Children.Add(labelBorder);

            // Close button — top-right corner
            var closeBtn = new Button
            {
                Content             = "✕",
                Foreground          = Brushes.White,
                Background          = new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0)),
                BorderBrush         = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)),
                BorderThickness     = new Thickness(1),
                Padding             = new Thickness(10, 5, 10, 5),
                Margin              = new Thickness(0, 16, 16, 0),
                FontSize            = 16,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Top,
                Cursor              = Cursors.Hand,
                ToolTip             = "Close fullscreen  (Esc)",
            };
            AutomationProperties.SetName(closeBtn, "Close fullscreen");
            closeBtn.Click += (_, _) => Close();
            closeBtn.Template = BuildCloseTemplate();
            root.Children.Add(closeBtn);

            // Hint text — bottom-centre
            var hint = new TextBlock
            {
                Text                = "Esc or double-click to exit",
                Foreground          = new SolidColorBrush(Color.FromArgb(0x88, 255, 255, 255)),
                FontFamily          = new FontFamily("Segoe UI Variable Text, Segoe UI, sans-serif"),
                FontSize            = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Margin              = new Thickness(0, 0, 0, 16)
            };
            root.Children.Add(hint);

            Content = root;

            // ── Input ─────────────────────────────────────────────────
            KeyDown        += (_, e) => { if (e.Key == Key.Escape) Close(); };
            MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Close(); };

            // ── Live update timer (~30 fps) ───────────────────────────
            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _timer.Tick += OnTick;
            _timer.Start();

            // Fade out the label and hint after 3 seconds
            var fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            fadeTimer.Tick += (_, _) =>
            {
                FadeOut(labelBorder);
                FadeOut(hint);
                fadeTimer.Stop();
            };
            fadeTimer.Start();

            Closed += (_, _) => _timer.Stop();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            var bmp = _bitmapSource();
            if (bmp != null && !ReferenceEquals(_image.Source, bmp))
                _image.Source = bmp;
        }

        private static void FadeOut(UIElement element)
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                To       = 0,
                Duration = TimeSpan.FromMilliseconds(600),
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
            };
            element.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        /// <summary>Minimal button template with hover highlight.</summary>
        private static ControlTemplate BuildCloseTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var border   = new FrameworkElementFactory(typeof(Border));
            border.Name  = "Bd";
            border.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                    { RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetBinding(Border.BorderBrushProperty,
                new System.Windows.Data.Binding("BorderBrush")
                    { RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetBinding(Border.PaddingProperty,
                new System.Windows.Data.Binding("Padding")
                    { RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.TemplatedParent) });

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;

            var hoverTrigger = new Trigger
            {
                Property = Button.IsMouseOverProperty,
                Value    = true
            };
            hoverTrigger.Setters.Add(new Setter(
                Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0xDD, 196, 40, 40)),
                "Bd"));
            template.Triggers.Add(hoverTrigger);

            return template;
        }
    }
}
