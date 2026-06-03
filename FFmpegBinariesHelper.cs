using System;
using System.IO;
using System.Linq;
using FFmpeg.AutoGen;

namespace RGDSCapture
{
    public static class FFmpegBinariesHelper
    {
        private static bool _registered = false;

        public static void RegisterFFmpegBinaries()
        {
            if (_registered) return;
            _registered = true;

            var current = AppContext.BaseDirectory;
            ffmpeg.RootPath = current;

            // Find any avcodec-XX.dll regardless of version number
            var avcodecDll = Directory
                .EnumerateFiles(current, "avcodec-*.dll")
                .FirstOrDefault();

            if (avcodecDll == null)
                throw new FileNotFoundException(
                    "FFmpeg DLLs not found. Copy all .dll files from the FFmpeg " +
                    $"bin folder into: {current}");

            // Log which version was found — visible in VS Code debug console
            System.Diagnostics.Debug.WriteLine(
                $"FFmpeg found: {Path.GetFileName(avcodecDll)}");
        }
    }
}