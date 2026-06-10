using System;
using System.IO;
using System.Linq;
using FFmpeg.AutoGen;

namespace RGDSCapture.Services
{
    /// <summary>
    /// Points FFmpeg.AutoGen at the native DLLs shipped next to the executable.
    /// Thread-safe and idempotent.
    /// </summary>
    public static class FFmpegLoader
    {
        private static readonly object Gate = new();
        private static bool _registered;

        public static void EnsureRegistered()
        {
            lock (Gate)
            {
                if (_registered) return;

                string baseDir = AppContext.BaseDirectory;

                bool dllsPresent = Directory
                    .EnumerateFiles(baseDir, "avcodec-*.dll")
                    .Any();

                if (!dllsPresent)
                    throw new FileNotFoundException(
                        $"FFmpeg DLLs not found in {baseDir}. " +
                        "Reinstall the application or copy the FFmpeg shared DLLs next to RGDSCapture.exe.");

                ffmpeg.RootPath = baseDir;
                _registered = true;
            }
        }
    }
}
