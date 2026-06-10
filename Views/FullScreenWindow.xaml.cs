using System.Windows;
using System.Windows.Input;
using RGDSCapture.ViewModels;

namespace RGDSCapture.Views
{
    /// <summary>
    /// Borderless fullscreen view of one screen. Shares the same
    /// WriteableBitmap as the main window, so frames appear in both
    /// with no extra copies. Esc or double-click closes.
    /// </summary>
    public partial class FullScreenWindow : Window
    {
        public sealed record FullscreenContext(MainViewModel Main, ScreenViewModel Screen);

        public FullScreenWindow(MainViewModel main, ScreenViewModel screen)
        {
            InitializeComponent();
            DataContext = new FullscreenContext(main, screen);

            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Close();
                    e.Handled = true;
                }
            };

            MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) Close();
            };
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
