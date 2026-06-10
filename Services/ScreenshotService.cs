using System;
using System.IO;
using System.Windows.Media.Imaging;
using RGDSCapture.Core;

namespace RGDSCapture.Services
{
    /// <summary>Saves the current frame of each screen as a timestamped PNG.</summary>
    public static class ScreenshotService
    {
        /// <summary>
        /// Saves any non-null bitmaps. Returns the number of files written.
        /// Must be called on the UI thread (reads WriteableBitmaps).
        /// </summary>
        public static int SaveAll(BitmapSource? top, BitmapSource? bottom)
        {
            Directory.CreateDirectory(AppPaths.ScreenshotsDir);
            string ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            int saved = 0;

            if (top != null)
            {
                SavePng(top, Path.Combine(AppPaths.ScreenshotsDir, $"top_{ts}.png"));
                saved++;
            }
            if (bottom != null)
            {
                SavePng(bottom, Path.Combine(AppPaths.ScreenshotsDir, $"bottom_{ts}.png"));
                saved++;
            }
            return saved;
        }

        private static void SavePng(BitmapSource bmp, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            encoder.Save(fs);
        }
    }
}
