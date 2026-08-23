namespace RGDSCapture.Core
{
    /// <summary>Identifies one of the two DS screens.</summary>
    public enum ScreenId
    {
        Top,
        Bottom
    }

    /// <summary>How the two screens are arranged in the main window.</summary>
    public enum LayoutMode
    {
        VerticalStack,
        SideBySide,
        TopOnly,
        BottomOnly,
        Hybrid      // one screen large, the other small in a corner (melonDS-style)
    }

    /// <summary>Application color theme.</summary>
    public enum AppTheme
    {
        Dark,
        Light
    }

    /// <summary>Lifecycle of the SSH connection to the device.</summary>
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Lost
    }

    /// <summary>Health of a single video stream.</summary>
    public enum StreamHealth
    {
        Waiting,
        Live,
        Frozen,
        Recovering
    }

    /// <summary>Encoder bitrate preset for the device's GStreamer pipelines.</summary>
    public enum StreamQuality
    {
        Low,      // 1 Mbps per screen — congested WiFi
        Medium,   // 2 Mbps per screen — default
        High      // 4 Mbps per screen — strong network
    }

    /// <summary>
    /// Which Anbernic device the app is talking to. Devices differ in screen
    /// count and in what's available on the remote shell to capture video
    /// with, so this selects both the capture pipeline (see SshService) and
    /// how much of the dual-screen UI applies.
    /// </summary>
    public enum DeviceType
    {
        /// <summary>RG Dual Screen — Anbernic Linux FW 1.0, GStreamer preinstalled, two screens.</summary>
        RgDualScreen,

        /// <summary>RG353V — stock Buildroot firmware, no GStreamer, one 640x480 screen. Captured via ffmpeg's fbdev input + software x264 (no working hardware encoder path on stock firmware).</summary>
        Rg353V
    }
}
