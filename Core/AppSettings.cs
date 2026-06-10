using System.Text.Json.Serialization;

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

        /// <summary>Opt-in: SSH password stored DPAPI-encrypted (see CredentialStore).</summary>
        public bool RememberCredentials { get; set; }
        public string? ProtectedPassword { get; set; }

        /// <summary>Stream quality preset name (StreamQuality enum).</summary>
        public string Quality { get; set; } = nameof(StreamQuality.Medium);

        /// <summary>Show the per-screen network stats overlay.</summary>
        public bool ShowStats { get; set; }

        public double Volume { get; set; } = 0.85;
        public string? AudioInputName { get; set; }
        public string? AudioOutputName { get; set; }

        /// <summary>Instant-replay window length in seconds.</summary>
        public int ReplaySeconds { get; set; } = 30;

        [JsonIgnore]
        public AppTheme ThemeValue =>
            System.Enum.TryParse(Theme, out AppTheme t) ? t : AppTheme.Dark;

        [JsonIgnore]
        public LayoutMode LayoutValue =>
            System.Enum.TryParse(Layout, out LayoutMode l) ? l : LayoutMode.SideBySide;
    }
}
