using System.Windows;

namespace RGDSCapture.Views
{
    public partial class ConnectDialog : Window
    {
        public string Username { get; private set; } = string.Empty;
        public string Password { get; private set; } = string.Empty;

        public ConnectDialog(string defaultUsername)
        {
            InitializeComponent();
            TxtUsername.Text = defaultUsername;
            Loaded += (_, _) => TxtPassword.Focus();
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
            DialogResult = true;
        }
    }
}
