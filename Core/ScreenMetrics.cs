using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RGDSCapture.Core
{
    /// <summary>
    /// Which monitor a window is on, and how much of it is usable.
    ///
    /// <see cref="SystemParameters.WorkArea"/> only ever describes the
    /// primary monitor, which is the wrong answer for this app more often
    /// than not — the capture window is typically parked on a second screen
    /// of a different size while the game is played on the first.
    /// </summary>
    public static class ScreenMetrics
    {
        private const int MonitorDefaultToNearest = 0x00000002;

        /// <summary>
        /// Usable area of the monitor <paramref name="window"/> sits on, in
        /// device-independent units. Falls back to the primary monitor before
        /// the window is sourced, since it has no handle — and so no monitor —
        /// until then.
        /// </summary>
        public static Size WorkArea(Window window)
        {
            var fallback = new Size(
                SystemParameters.WorkArea.Width,
                SystemParameters.WorkArea.Height);

            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return fallback;

            if (!TryGetMonitorInfo(handle, out var info)) return fallback;

            // Win32 answers in physical pixels; WPF lays out in DIPs.
            var dpi = VisualTreeHelper.GetDpi(window);
            return new Size(
                (info.rcWork.Right - info.rcWork.Left) / dpi.DpiScaleX,
                (info.rcWork.Bottom - info.rcWork.Top) / dpi.DpiScaleY);
        }

        /// <summary>
        /// Raw monitor geometry in physical pixels, for callers working in
        /// Win32 message coordinates rather than DIPs.
        /// </summary>
        internal static bool TryGetMonitorInfo(IntPtr hwnd, out MONITORINFO info)
        {
            info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

            IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return false;

            return GetMonitorInfo(monitor, ref info);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }
    }
}
