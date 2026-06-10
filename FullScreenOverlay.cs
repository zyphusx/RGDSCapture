using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Automation;

namespace RGDSCapture
{
    public sealed class FullScreenOverlay : Window
    {
        private readonly Func<WriteableBitmap?> _bitmapSource;
        private readonly Func<bool> _isConnected;
        private readonly Func<Task> _restartAllStreams;
        private readonly Func<Task> _restartTopStream;
        private readonly Func<Task> _restartBottomStream;

        private readonly Image _image;
        private readonly DispatcherTimer _timer;

        public FullScreenOverlay(
            Func<WriteableBitmap?> bitmapSource,
            string screenLabel,
            Func<bool> isConnected,
            Func<Task> restartAllStreams,
            Func<Task> restartTopStream,
            Func<Task> restartBottomStream)
        {
            _bitmapSource        = bitmapSource;
            _isConnected         = isConnected;
            _restartAllStreams    = restartAllStreams;
            _restartTopStream    = restartTopStream;
            _restartBottomStream = restartBottomStream;

            WindowStyle        = WindowStyle.None;
            WindowState        = WindowState.Maximized;
            Background         = Brushes.Black;
            ShowInTaskbar      = false;
            Topmost            = true;
            AllowsTransparency = false;

            KeyDown += OnKeyDown;

            var root = new Grid
            {
                Background = Brushes.Black,
                Cursor     = Cursors.None
            };

            _image = new Image
            {
                Stretch             = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            root.Children.Add(_image);

            var labelBorder = new Border
            {
                Background      = new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0)),
                BorderBrush     = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(12, 8, 12, 8),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Top,
                Margin = new Thickness(16, 16, 0, 0),
                Child  = new TextBlock
                {
                    Text       = screenLabel,
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize   = 14,
                    FontWeight = FontWeights.SemiBold
                }
            };
            AutomationProperties.SetName(labelBorder, "Stream label");
            root.Children.Add(labelBorder);

            var closeBtn = new Button
            {
                Content             = "✕",
                Foreground          = Brushes.White,
                Background          = new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0)),
                BorderBrush         = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)),
                BorderThickness     = new Thickness(1),
                Width               = 40,
                Height              = 40,
                FontSize            = 20,
                FontWeight          = FontWeights.Bold,
                Cursor              = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Top,
                Margin              = new Thickness(0, 16, 16, 0)
            };
            closeBtn.Click += (_, _) => Close();
            AutomationProperties.SetName(closeBtn, "Close fullscreen");
            root.Children.Add(closeBtn);

            var hintText = new TextBlock
            {
                Text       = "ESC to exit  |  Double-click to exit  |  F5 / F6 / F7 to restart",
                Foreground = new SolidColorBrush(Color.FromArgb(0xBB, 200, 200, 200)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 12,
                TextAlignment       = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Margin  = new Thickness(0, 0, 0, 32),
                Opacity = 0.7
            };
            root.Children.Add(hintText);

            var fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            fadeTimer.Tick += (_, _) => { fadeTimer.Stop(); FadeOut(hintText); };
            fadeTimer.Start();

            Content = root;

            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _timer.Tick += OnRenderTick;

            Loaded += (_, _) => _timer.Start();
            Closed += (_, _) => _timer.Stop();

            MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) Close();
            };
        }

        private void OnRenderTick(object? sender, EventArgs e)
        {
            var bitmap = _bitmapSource?.Invoke();
            if (bitmap == null) return;

            if (!ReferenceEquals(_image.Source, bitmap))
                _image.Source = bitmap;

            _image.InvalidateVisual();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;

                case Key.F5:
                case Key.F6:
                case Key.F7:
                    var parentWindow = Application.Current.MainWindow;
                    if (parentWindow != null)
                    {
                        var args = new KeyEventArgs(
                            e.KeyboardDevice, e.InputSource, e.Timestamp, e.Key)
                        {
                            RoutedEvent = KeyDownEvent
                        };
                        parentWindow.RaiseEvent(args);
                    }
                    e.Handled = true;
                    break;
            }
        }

        private static void FadeOut(UIElement element)
        {
            var anim = new DoubleAnimation
            {
                To             = 0,
                Duration       = TimeSpan.FromMilliseconds(600),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            element.BeginAnimation(OpacityProperty, anim);
        }
    }
}