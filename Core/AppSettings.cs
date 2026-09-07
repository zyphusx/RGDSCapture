using System.Text.Json.Serialization;

namespace RGDSCapture.Core
{
    /// <summary>
    /// User preferences persisted between sessions as JSON in %APPDATA%\RGDSCapture.
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>
        /// Theme preset id (see <see cref="ThemeCatalog"/>). Older settings
        /// files hold "Dark"/"Light" here; ThemeCatalog.Resolve maps those.
        /// </summary>
        public string Theme { get; set; } = ThemeCatalog.DefaultId;

        /// <summary>
        /// Optional accent override as #RRGGBB, set by the custom color
        /// picker. Null means the preset supplies its own accent.
        /// </summary>
        public string? CustomAccent { get; set; }

        public string Layout { get; set; } = nameof(LayoutMode.SideBySide);

        /// <summary>Display rotation in degrees (0, 90, 180, 270) for sideways-held games.</summary>
        public int Rotation { get; set; }

        /// <summary>Swap which screen takes the top/left/large position.</summary>
        public bool SwapScreens { get; set; }

        /// <summary>Margin around each screen panel in pixels.</summary>
        public int ScreenGap { get; set; } = 8;

        /// <summary>Smooth (Fant) video scaling instead of pixel-perfect nearest-neighbor.</summary>
        public bool SmoothScaling { get; set; }

        /// <summary>
        /// App-level UI zoom (1.0 = 100%), layered on top of the Windows DPI
        /// setting. See <see cref="Services.UiScaleService"/> for the
        /// selectable steps; anything out of range is clamped when applied.
        /// </summary>
        public double UiScale { get; set; } = 1.0;

        public string DeviceIp { get; set; } = "192.168.1.100";
        public int SshPort { get; set; } = 22;
        public string SshUsername { get; set; } = "root";

        /// <summary>Which Anbernic device to connect to (DeviceType enum name).</summary>
        public string DeviceType { get; set; } = nameof(Core.DeviceType.RgDualScreen);

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

        /// <summary>Left sidebar column (device / streams / display) expanded.</summary>
        public bool LeftPanelOpen { get; set; } = true;

        /// <summary>Right sidebar column (record / audio / timer) expanded.</summary>
        public bool RightPanelOpen { get; set; } = true;

        /// <summary>Persisted theme id, exposed under the name ThemeService expects.</summary>
        [JsonIgnore]
        public string ThemeId => Theme;

        [JsonIgnore]
        public LayoutMode LayoutValue =>
            System.Enum.TryParse(Layout, out LayoutMode l) ? l : LayoutMode.SideBySide;

        [JsonIgnore]
        public Core.DeviceType DeviceTypeValue =>
            System.Enum.TryParse(DeviceType, out Core.DeviceType d) ? d : Core.DeviceType.RgDualScreen;
    }
}
