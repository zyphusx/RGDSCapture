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
        BottomOnly
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
}
