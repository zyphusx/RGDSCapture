using System.Windows;

namespace RGDSCapture
{
    public partial class ConnectDialog : Window
    {
        public string SshUsername { get; private set; } = string.Empty;
        public string SshPassword { get; private set; } = string.Empty;

        public ConnectDialog()
        {
            InitializeComponent();
            Loaded += (_, _) => TxtPassword.Focus();
        }

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtUsername.Text))
            {
                MessageBox.Show("Enter a username.", "Missing field",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SshUsername = TxtUsername.Text.Trim();
            SshPassword = TxtPassword.Password;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}