using System;
using System.IO;

namespace RGDSCapture.Core
{
    /// <summary>Well-known folders and file locations used by the app.</summary>
    public static class AppPaths
    {
        public static string SettingsDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RGDSCapture");

        public static string SettingsFile { get; } = Path.Combine(SettingsDir, "settings.json");

        public static string CrashLogFile { get; } = Path.Combine(SettingsDir, "crash.log");

        public static string RecordingsDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "RGDSCapture");

        public static string ScreenshotsDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "RGDSCapture");

        public static string FfmpegExe { get; } = Path.Combine(
            AppContext.BaseDirectory, "ffmpeg.exe");

        public static void EnsureCreated()
        {
            Directory.CreateDirectory(SettingsDir);
            Directory.CreateDirectory(RecordingsDir);
            Directory.CreateDirectory(ScreenshotsDir);
        }
    }
}
