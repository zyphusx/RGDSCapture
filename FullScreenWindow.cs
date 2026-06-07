using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Automation;
using System.Diagnostics;

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
    /// 
    /// Features:
    /// - Stream freeze detection and recovery (restarts frame timer on stall)
    /// - FPS counter in top-right corner
    /// - Graceful recovery when stream data stops arriving
    /// </summary>
    public sealed class FullScreenWindow : Window
    {
        private readonly Func<WriteableBitmap?> _bitmapSource;
        private readonly Image                  _image;
        private readonly DispatcherTimer        _timer;
        private readonly DispatcherTimer        _freezeDetectionTimer;
        private readonly TextBlock              _fpsCounter;
        private int                             _frameCount;
        private Stopwatch                       _fpsStopwatch;
        private WriteableBitmap?                _lastBitmap;
        private int                             _freezeFrameCount;
        private const int                       FreezeThresholdFrames = 30; // ~1 second at 30fps

        public FullScreenWindow(Func<WriteableBitmap?> bitmapSource, string screenLabel)
        {
            _bitmapSource = bitmapSource;
            _frameCount = 0;
            _freezeFrameCount = 0;
            _fpsStopwatch = Stopwatch.StartNew();

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

            // FPS Counter in top-right corner
            _fpsCounter = new TextBlock
            {
                Text                = "60.0 FPS",
                Foreground          = new SolidColorBrush(Color.FromArgb(0xCC, 76, 175, 80)), // Green
                FontFamily          = new FontFamily("Consolas, monospace"),
                FontSize            = 12,
                FontWeight          = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Top,
                Margin              = new Thickness(0, 16, 16, 0),
                Padding             = new Thickness(8, 4, 8, 4),
                Background          = new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0))
            };
            AutomationProperties.SetName(_fpsCounter, "FPS counter");
            root.Children.Add(_fpsCounter);

            // Close button — top-right corner (below FPS counter)
            var closeBtn = new Button
            {
                Content             = "✕",
                Foreground          = Brushes.White,
                Background          = new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0)),
                BorderBrush         = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)),
                BorderThickness     = new Thickness(1),
                Padding             = new Thickness(10, 5, 10, 5),
                Margin              = new Thickness(0, 70, 16, 0),
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

            // ── Freeze detection timer (runs every 100ms) ──────────────
            _freezeDetectionTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _freezeDetectionTimer.Tick += OnFreezeDetectionTick;
            _freezeDetectionTimer.Start();

            // Fade out the label and hint after 3 seconds
            var fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            fadeTimer.Tick += (_, _) =>
            {
                FadeOut(labelBorder);
                FadeOut(hint);
                fadeTimer.Stop();
            };
            fadeTimer.Start();

            Closed += (_, _) => 
            {
                _timer.Stop();
                _freezeDetectionTimer.Stop();
            };
        }

        private void OnTick(object? sender, EventArgs e)
        {
            var bmp = _bitmapSource();
            if (bmp != null)
            {
                // Only update if bitmap changed (new frame arrived)
                if (!ReferenceEquals(_image.Source, bmp))
                {
                    _image.Source = bmp;
                    _lastBitmap = bmp;
                    _frameCount++;
                    _freezeFrameCount = 0; // Reset freeze counter on new frame
                }
                else
                {
                    _freezeFrameCount++;
                }

                // Update FPS counter every 500ms
                if (_fpsStopwatch.ElapsedMilliseconds >= 500)
                {
                    double fps = (_frameCount * 1000.0) / _fpsStopwatch.ElapsedMilliseconds;
                    _fpsCounter.Text = $"{fps:F1} FPS";
                    
                    // Change color based on FPS health
                    if (fps >= 58)
                        _fpsCounter.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 76, 175, 80)); // Green
                    else if (fps >= 30)
                        _fpsCounter.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 255, 193, 7)); // Amber
                    else
                        _fpsCounter.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 244, 67, 54)); // Red

                    _frameCount = 0;
                    _fpsStopwatch.Restart();
                }
            }
        }

        private void OnFreezeDetectionTick(object? sender, EventArgs e)
        {
            // If no new frame for ~1 second (30 frames @ 30fps), attempt recovery
            if (_freezeFrameCount > FreezeThresholdFrames && _lastBitmap != null)
            {
                // Attempt recovery: Restart timer with aggressive retry
                try
                {
                    _timer.Stop();
                    _timer.Start();
                    _freezeFrameCount = 0;
                    
                    // Optional: Log or notify user of recovery attempt
                    System.Diagnostics.Debug.WriteLine($"Stream freeze detected and recovery attempted");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Freeze recovery error: {ex.Message}");
                }
            }
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
