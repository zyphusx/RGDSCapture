namespace RGDSCapture.Core
{
    /// <summary>
    /// User preferences persisted between sessions as JSON in %APPDATA%\RGDSCapture.
    /// </summary>
    public sealed class AppSettings
    {
        public string Theme { get; set; } = nameof(AppTheme.Dark);
        public string Layout { get; set; } = nameof(LayoutMode.SideBySide);

        public string DeviceIp { get; set; } = "192.168.1.100";
        public int SshPort { get; set; } = 22;
        public string SshUsername { get; set; } = "root";

        public double Volume { get; set; } = 0.85;
        public string? AudioInputName { get; set; }
        public string? AudioOutputName { get; set; }

        public AppTheme ThemeValue =>
            System.Enum.TryParse(Theme, out AppTheme t) ? t : AppTheme.Dark;

        public LayoutMode LayoutValue =>
            System.Enum.TryParse(Layout, out LayoutMode l) ? l : LayoutMode.SideBySide;
    }
}
