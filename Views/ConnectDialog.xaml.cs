using System.Windows;
using System.Windows.Input;
using RGDSCapture.Services;

namespace RGDSCapture.Views
{
    public partial class ConnectDialog : Window
    {
        public string Username { get; private set; } = string.Empty;
        public string Password { get; private set; } = string.Empty;
        public bool Remember { get; private set; }

        public ConnectDialog(string defaultUsername, bool defaultRemember = false)
        {
            InitializeComponent();
            UiScaleService.TrackDialogSize(this);
            TxtUsername.Text = defaultUsername;
            ChkRemember.IsChecked = defaultRemember;
            Loaded += (_, _) => TxtPassword.Focus();

            // The window is borderless, so the header stands in for a title bar.
            DragHandle.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) DragMove();
            };
        }

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtUsername.Text))
            {
                MessageBox.Show(this, "Enter a username.", "Missing field",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Username = TxtUsername.Text.Trim();
            Password = TxtPassword.Password;
            Remember = ChkRemember.IsChecked == true;
            DialogResult = true;
        }
    }
}
