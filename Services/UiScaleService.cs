using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using RGDSCapture.Core;

namespace RGDSCapture.Services
{
    /// <summary>
    /// App-level UI zoom, layered on top of whatever Windows DPI scaling is
    /// already in force.
    ///
    /// A <see cref="ScaleTransform"/> is published as the application
    /// resource "UiScaleTransform". Anything that opts in with
    /// <c>LayoutTransform="{DynamicResource UiScaleTransform}"</c> resizes
    /// live when the factor changes — no restart, and no per-window
    /// bookkeeping.
    ///
    /// The factor changes by replacing that transform, not by mutating it:
    /// a ResourceDictionary seals the Freezables it stores, so setting
    /// ScaleX on the stored instance throws. DynamicResource carries the
    /// replacement to every element using it, the same way ThemeService
    /// swaps the palette dictionary.
    ///
    /// The video surfaces sit inside the scaled tree, but they are
    /// Stretch=Uniform, so scaling changes how much room the chrome takes and
    /// leaves the picture filling whatever is left. WPF still rasterizes at
    /// the final device resolution, so nothing is resampled twice.
    ///
    /// Window-level chrome that lives *outside* the scaled tree — minimum
    /// sizes, WindowChrome caption height — has to follow by hand; see
    /// <see cref="Changed"/>.
    /// </summary>
    public static class UiScaleService
    {
        public const string ResourceKey = "UiScaleTransform";
        public const double DefaultScale = 1.0;

        /// <summary>
        /// Selectable factors, ascending. The View menu lists these and the
        /// zoom shortcuts step through them, so both stay in step by
        /// construction. The top end is deliberately modest: the main window
        /// has a 900x620 minimum, and 175% of that still fits a 1080p screen.
        /// </summary>
        public static readonly IReadOnlyList<double> Steps =
            new[] { 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75 };

        public static double MinScale => Steps[0];
        public static double MaxScale => Steps[Steps.Count - 1];

        public static double Current { get; private set; } = DefaultScale;

        /// <summary>
        /// Raised after <see cref="Current"/> changes, carrying the new factor.
        /// </summary>
        public static event Action<double>? Changed;

        /// <summary>
        /// Publishes the transform into application resources. Call once at
        /// startup, before any window is constructed.
        /// </summary>
        public static void Install() => Publish();

        /// <summary>Applies a factor, clamping it into range. A no-op if unchanged.</summary>
        public static void Apply(double scale)
        {
            scale = Clamp(scale);
            if (Math.Abs(scale - Current) < 0.0001) return;

            Current = scale;
            Publish();
            Changed?.Invoke(scale);
        }

        private static void Publish()
        {
            var transform = new ScaleTransform(Current, Current);
            transform.Freeze();
            Application.Current.Resources[ResourceKey] = transform;
        }

        /// <summary>Applies the factor recorded in settings, tolerating junk values.</summary>
        public static void ApplyFrom(AppSettings settings) => Apply(settings.UiScale);

        /// <summary>
        /// The step one place up (+1) or down (-1) from the current factor,
        /// for the zoom-in / zoom-out shortcuts. Saturates at either end.
        /// </summary>
        public static double Step(int direction)
        {
            int index = NearestStepIndex(Current) + Math.Sign(direction);
            return Steps[Math.Clamp(index, 0, Steps.Count - 1)];
        }

        public static double Clamp(double scale) =>
            double.IsFinite(scale)
                ? Math.Clamp(scale, MinScale, MaxScale)
                : DefaultScale;

        /// <summary>
        /// Sizes a fixed-width, auto-height dialog for the current factor.
        ///
        /// Width is set explicitly because such a dialog declares it in
        /// unscaled window coordinates: left alone, the zoomed content would
        /// be clipped rather than laid out wider. Later changes are tracked
        /// too — the theme picker is modeless, so the factor can move while
        /// it is open — and the hook is dropped when the dialog closes.
        ///
        /// Height is capped to the desktop. A fixed-layout card grows past
        /// the bottom of the screen at the larger factors, taking its buttons
        /// with it; with a cap, the dialog's own scroller takes over instead.
        /// </summary>
        public static void TrackDialogSize(Window dialog)
        {
            // Deferred: a dialog has no handle — and so no known monitor —
            // until it is sourced, and CenterOwner can land it on a screen
            // quite unlike the primary one. SourceInitialized still runs
            // before the first measure, so SizeToContent sees the cap.
            dialog.SourceInitialized += (_, _) =>
                dialog.MaxHeight = ScreenMetrics.WorkArea(dialog).Height;

            double baseWidth = dialog.Width;
            if (double.IsNaN(baseWidth)) return;

            void Resize(double scale) => dialog.Width = baseWidth * scale;

            Resize(Current);
            Changed += Resize;
            dialog.Closed += (_, _) => Changed -= Resize;
        }

        /// <summary>Menu / status label for a factor, e.g. 125%.</summary>
        public static string Format(double scale) => $"{scale * 100:0}%";

        /// <summary>True when <paramref name="scale"/> is the factor in force.</summary>
        public static bool IsCurrent(double scale) =>
            Math.Abs(scale - Current) < 0.0001;

        /// <summary>
        /// Index of the listed step closest to <paramref name="scale"/>, so a
        /// hand-edited settings value still steps sensibly.
        /// </summary>
        private static int NearestStepIndex(double scale)
        {
            int best = 0;
            for (int i = 1; i < Steps.Count; i++)
            {
                if (Math.Abs(Steps[i] - scale) < Math.Abs(Steps[best] - scale))
                    best = i;
            }
            return best;
        }
    }
}
