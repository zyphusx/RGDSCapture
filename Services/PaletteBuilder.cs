using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RGDSCapture.Core;

namespace RGDSCapture.Services
{
    /// <summary>
    /// Derives a complete theme dictionary from a <see cref="ThemePreset"/>.
    ///
    /// Every brush the UI asks for is generated from three seeds — the
    /// light/dark ramp, the accent color, and how much of the accent's hue
    /// bleeds into the greys. That keeps a new theme to one line in
    /// <see cref="ThemeCatalog"/> and lets a user-picked accent re-tint the
    /// whole app with no extra work.
    ///
    /// The key set here must stay in step with Themes/Dark.xaml, which is the
    /// design-time and pre-startup fallback.
    /// </summary>
    public static class PaletteBuilder
    {
        public static ResourceDictionary Build(ThemePreset preset, Color? accentOverride = null)
        {
            Color accent = accentOverride ?? ParseHex(preset.Accent);
            var (accentH, accentS, accentL) = ToHsl(accent);

            // A custom accent brings its own hue to the surfaces; a preset may
            // pin them somewhere else on purpose.
            double tintHue = accentOverride.HasValue
                ? accentH
                : preset.TintHue ?? accentH;
            double tint = Clamp01(preset.TintStrength);

            return preset.IsDark
                ? BuildDark(accent, accentH, accentS, accentL, tintHue, tint)
                : BuildLight(accent, accentH, accentS, accentL, tintHue, tint);
        }

        // ── Dark ramp ─────────────────────────────────────────────────
        private static ResourceDictionary BuildDark(
            Color accent, double aH, double aS, double aL, double tintHue, double tint)
        {
            double surfSat = tint * 0.28;
            double textSat = tint * 0.14;

            Color Surface(double l) => FromHsl(tintHue, surfSat, l);
            Color Text(double l) => FromHsl(tintHue, textSat, l);

            var d = new ResourceDictionary();

            // Surface ramp
            Color backdrop = Surface(0.052);
            Color surface = Surface(0.082);
            Color raised = Surface(0.112);
            Color hover = Surface(0.150);
            Color videoArea = Surface(0.028);
            Color borderSubtle = Surface(0.170);
            Color borderStrong = Surface(0.235);
            Color inputBg = Surface(0.040);

            Set(d, "BackdropBg", backdrop);
            Set(d, "WindowBg", backdrop);
            Set(d, "SurfaceBg", surface);
            Set(d, "SurfaceRaisedBg", raised);
            Set(d, "SurfaceHoverBg", hover);
            Set(d, "VideoAreaBg", videoArea);

            // Aliases kept for the dialogs and drawer
            Set(d, "ToolbarBg", surface);
            Set(d, "ToolbarBorder", borderSubtle);
            Set(d, "PanelBg", surface);
            Set(d, "PanelBorder", borderSubtle);
            Set(d, "HeaderBg", raised);
            Set(d, "SeparatorBg", borderSubtle);

            Set(d, "BorderSubtle", borderSubtle);
            Set(d, "BorderStrong", borderStrong);

            // Typography
            Color textPrimary = Text(0.905);
            Color textSecondary = Text(0.660);
            Color textTertiary = Text(0.485);
            Color textDisabled = Text(0.375);
            Set(d, "TextPrimary", textPrimary);
            Set(d, "TextSecondary", textSecondary);
            Set(d, "TextTertiary", textTertiary);
            Set(d, "TextDisabled", textDisabled);

            // Accent
            Color accentHover = FromHsl(aH, aS, Math.Min(0.96, aL + 0.09));
            Color accentPress = FromHsl(aH, aS, Math.Max(0.10, aL - 0.09));
            Color accentMuted = FromHsl(aH, Math.Min(0.60, aS * 0.75), 0.185);
            Color accentSubtle = FromHsl(aH, Math.Min(0.85, aS * 0.85), 0.780);
            Set(d, "AccentBg", accent);
            Set(d, "AccentHover", accentHover);
            Set(d, "AccentPress", accentPress);
            Set(d, "AccentFg", ReadableOn(accent));
            Set(d, "AccentMuted", accentMuted);
            Set(d, "AccentSubtleFg", accentSubtle);

            // Semantic status — deliberately NOT re-tinted; "live" must read
            // as green and "record" as red whatever the accent is.
            Set(d, "LiveFg", Hex("#34D399"));
            Set(d, "LiveMuted", Hex("#17342A"));
            Set(d, "WarnFg", Hex("#FBBF24"));
            Set(d, "WarnMuted", Hex("#3A2E12"));
            Set(d, "DangerFgSolid", Hex("#FF4D4D"));
            Set(d, "IdleFg", textTertiary);

            // Buttons
            Set(d, "BtnBg", raised);
            Set(d, "BtnHover", Surface(0.175));
            Set(d, "BtnPress", Surface(0.215));
            Set(d, "BtnBorder", borderStrong);
            Set(d, "BtnFg", textPrimary);

            Set(d, "DangerBg", FromHsl(2, 0.30, 0.125));
            Set(d, "DangerHover", FromHsl(2, 0.33, 0.170));
            Set(d, "DangerPress", FromHsl(2, 0.35, 0.210));
            Set(d, "DangerBorder", FromHsl(2, 0.36, 0.260));
            Set(d, "DangerFg", Hex("#FF8A8A"));

            Set(d, "RecordingBg", Hex("#C22B2B"));
            Set(d, "RecordingHover", Hex("#D63838"));
            Set(d, "RecordingBorder", Hex("#E05252"));
            Set(d, "RecordingFg", Colors.White);

            // Inputs
            Set(d, "InputBg", inputBg);
            Set(d, "InputFg", textPrimary);
            Set(d, "InputBorder", borderStrong);
            Set(d, "InputBorderHover", Surface(0.300));
            Set(d, "InputBorderFocus", accent);
            Set(d, "InputCaret", accent);

            // Segmented controls
            Set(d, "SegmentTrackBg", inputBg);
            Set(d, "SegmentBorder", borderSubtle);
            Set(d, "SegmentHoverBg", raised);
            Set(d, "SegmentSelBg", FromHsl(aH, Math.Min(0.45, aS * 0.55), 0.245));
            Set(d, "SegmentSelBorder", accent);
            Set(d, "SegmentSelFg", accentSubtle);
            Set(d, "SegmentFg", textSecondary);

            // Title bar
            Set(d, "TitleBarBg", backdrop);
            Set(d, "TitleBarBorder", Surface(0.140));
            Set(d, "TitleBarFg", textPrimary);
            Set(d, "CaptionBtnHover", hover);
            Set(d, "CaptionBtnPress", Surface(0.195));
            Set(d, "CaptionCloseHover", Hex("#C42B1C"));
            Set(d, "CaptionClosePress", Hex("#A02318"));

            // Menus
            Set(d, "MenuBg", backdrop);
            Set(d, "MenuFg", Text(0.790));
            Set(d, "MenuBorder", Surface(0.140));
            Set(d, "MenuPopupBg", Surface(0.098));
            Set(d, "MenuHoverBg", Surface(0.175));
            Set(d, "MenuSeparator", Surface(0.190));

            // Status strip
            Set(d, "StatusBarBg", Surface(0.038));
            Set(d, "StatusBarBorder", Surface(0.140));
            Set(d, "StatusFg", textSecondary);
            Set(d, "StatusErrorFg", Hex("#FF8A8A"));

            // Video panels
            Set(d, "ScreenBg", Colors.Black);
            Set(d, "ScreenBorder", Surface(0.155));
            Set(d, "ScreenBorderLive", FromHsl(aH, Math.Min(0.40, aS * 0.5), 0.235));
            Set(d, "ScreenLabelFg", Surface(0.290));
            Set(d, "ScreenGlassBg", Color.FromArgb(0xB3, 0, 0, 0));
            Set(d, "ScreenGlassBorder", Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

            // Log drawer
            Set(d, "LogBg", Surface(0.038));
            Set(d, "LogFg", Text(0.720));
            Set(d, "LogErrorFg", Hex("#FF8A8A"));
            Set(d, "LogTimestampFg", textDisabled);

            // Run timer
            Set(d, "TimerBg", inputBg);
            Set(d, "TimerBorder", borderSubtle);
            Set(d, "TimerFg", textPrimary);
            Set(d, "TimerRunningFg", Hex("#34D399"));

            // VU meter
            Set(d, "VuBg", inputBg);
            Set(d, "VuBorder", borderSubtle);

            // Misc
            Set(d, "SliderTrackBg", Surface(0.190));
            Set(d, "ScrollThumb", Surface(0.235));
            Set(d, "ScrollThumbHover", Surface(0.320));
            Set(d, "FocusRing", accent);
            Set(d, "TooltipBg", Surface(0.098));
            Set(d, "TooltipFg", textPrimary);
            Set(d, "TooltipBorder", borderStrong);

            // Overlay buttons on video
            Set(d, "OverlayBtnBg", Color.FromArgb(0x99, 0, 0, 0));
            Set(d, "OverlayBtnHover", Color.FromArgb(0xCC, 0, 0, 0));
            Set(d, "OverlayBtnBorder", Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            Set(d, "OverlayBtnFg", Colors.White);

            return d;
        }

        // ── Light ramp ────────────────────────────────────────────────
        private static ResourceDictionary BuildLight(
            Color accent, double aH, double aS, double aL, double tintHue, double tint)
        {
            double surfSat = tint * 0.24;
            double textSat = tint * 0.14;

            Color Surface(double l) => FromHsl(tintHue, surfSat, l);
            Color Text(double l) => FromHsl(tintHue, textSat, l);

            var d = new ResourceDictionary();

            Color backdrop = Surface(0.930);
            Color surface = Surface(0.995);
            Color raised = Surface(0.968);
            Color hover = Surface(0.922);
            Color videoArea = Surface(0.870);
            Color borderSubtle = Surface(0.895);
            Color borderStrong = Surface(0.800);

            Set(d, "BackdropBg", backdrop);
            Set(d, "WindowBg", backdrop);
            Set(d, "SurfaceBg", surface);
            Set(d, "SurfaceRaisedBg", raised);
            Set(d, "SurfaceHoverBg", hover);
            Set(d, "VideoAreaBg", videoArea);

            Set(d, "ToolbarBg", surface);
            Set(d, "ToolbarBorder", borderSubtle);
            Set(d, "PanelBg", surface);
            Set(d, "PanelBorder", borderSubtle);
            Set(d, "HeaderBg", raised);
            Set(d, "SeparatorBg", borderSubtle);

            Set(d, "BorderSubtle", borderSubtle);
            Set(d, "BorderStrong", borderStrong);

            Color textPrimary = Text(0.090);
            Color textSecondary = Text(0.360);
            Color textTertiary = Text(0.500);
            Color textDisabled = Text(0.660);
            Set(d, "TextPrimary", textPrimary);
            Set(d, "TextSecondary", textSecondary);
            Set(d, "TextTertiary", textTertiary);
            Set(d, "TextDisabled", textDisabled);

            Color accentHover = FromHsl(aH, aS, Math.Max(0.08, aL - 0.06));
            Color accentPress = FromHsl(aH, aS, Math.Max(0.06, aL - 0.13));
            Color accentMuted = FromHsl(aH, Math.Min(0.80, aS * 0.85), 0.930);
            Color accentSubtle = FromHsl(aH, aS, Math.Max(0.10, aL - 0.10));
            Set(d, "AccentBg", accent);
            Set(d, "AccentHover", accentHover);
            Set(d, "AccentPress", accentPress);
            Set(d, "AccentFg", ReadableOn(accent));
            Set(d, "AccentMuted", accentMuted);
            Set(d, "AccentSubtleFg", accentSubtle);

            Set(d, "LiveFg", Hex("#0E9F6E"));
            Set(d, "LiveMuted", Hex("#E3F7EF"));
            Set(d, "WarnFg", Hex("#B4690E"));
            Set(d, "WarnMuted", Hex("#FDF3E2"));
            Set(d, "DangerFgSolid", Hex("#DC2626"));
            Set(d, "IdleFg", textTertiary);

            Set(d, "BtnBg", surface);
            Set(d, "BtnHover", Surface(0.945));
            Set(d, "BtnPress", Surface(0.900));
            Set(d, "BtnBorder", borderStrong);
            Set(d, "BtnFg", textPrimary);

            Set(d, "DangerBg", FromHsl(2, 0.75, 0.970));
            Set(d, "DangerHover", FromHsl(2, 0.72, 0.935));
            Set(d, "DangerPress", FromHsl(2, 0.70, 0.895));
            Set(d, "DangerBorder", FromHsl(2, 0.60, 0.820));
            Set(d, "DangerFg", Hex("#B42318"));

            Set(d, "RecordingBg", Hex("#DC2626"));
            Set(d, "RecordingHover", Hex("#C61F1F"));
            Set(d, "RecordingBorder", Hex("#B01B1B"));
            Set(d, "RecordingFg", Colors.White);

            Set(d, "InputBg", Surface(1.000));
            Set(d, "InputFg", textPrimary);
            Set(d, "InputBorder", borderStrong);
            Set(d, "InputBorderHover", Surface(0.700));
            Set(d, "InputBorderFocus", accent);
            Set(d, "InputCaret", accent);

            Set(d, "SegmentTrackBg", Surface(0.945));
            Set(d, "SegmentBorder", Surface(0.875));
            Set(d, "SegmentHoverBg", Surface(0.900));
            Set(d, "SegmentSelBg", Surface(1.000));
            Set(d, "SegmentSelBorder", accent);
            Set(d, "SegmentSelFg", accentSubtle);
            Set(d, "SegmentFg", textSecondary);

            Set(d, "TitleBarBg", Surface(0.900));
            Set(d, "TitleBarBorder", Surface(0.845));
            Set(d, "TitleBarFg", textPrimary);
            Set(d, "CaptionBtnHover", Surface(0.845));
            Set(d, "CaptionBtnPress", Surface(0.790));
            Set(d, "CaptionCloseHover", Hex("#C42B1C"));
            Set(d, "CaptionClosePress", Hex("#A02318"));

            Set(d, "MenuBg", Surface(0.900));
            Set(d, "MenuFg", Text(0.200));
            Set(d, "MenuBorder", Surface(0.845));
            Set(d, "MenuPopupBg", Surface(1.000));
            Set(d, "MenuHoverBg", Surface(0.925));
            Set(d, "MenuSeparator", borderSubtle);

            Set(d, "StatusBarBg", Surface(0.900));
            Set(d, "StatusBarBorder", Surface(0.845));
            Set(d, "StatusFg", textSecondary);
            Set(d, "StatusErrorFg", Hex("#B42318"));

            Set(d, "ScreenBg", Hex("#101216"));
            Set(d, "ScreenBorder", Surface(0.790));
            Set(d, "ScreenBorderLive", FromHsl(aH, Math.Min(0.55, aS * 0.6), 0.740));
            Set(d, "ScreenLabelFg", Surface(0.460));
            Set(d, "ScreenGlassBg", Color.FromArgb(0xB3, 0, 0, 0));
            Set(d, "ScreenGlassBorder", Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

            Set(d, "LogBg", Surface(0.985));
            Set(d, "LogFg", Text(0.250));
            Set(d, "LogErrorFg", Hex("#B42318"));
            Set(d, "LogTimestampFg", textDisabled);

            Set(d, "TimerBg", raised);
            Set(d, "TimerBorder", Surface(0.875));
            Set(d, "TimerFg", textPrimary);
            Set(d, "TimerRunningFg", Hex("#0E9F6E"));

            Set(d, "VuBg", Surface(0.945));
            Set(d, "VuBorder", Surface(0.875));

            Set(d, "SliderTrackBg", Surface(0.845));
            Set(d, "ScrollThumb", Surface(0.790));
            Set(d, "ScrollThumbHover", Surface(0.700));
            Set(d, "FocusRing", accent);
            Set(d, "TooltipBg", Surface(1.000));
            Set(d, "TooltipFg", textPrimary);
            Set(d, "TooltipBorder", borderStrong);

            Set(d, "OverlayBtnBg", Color.FromArgb(0x99, 0, 0, 0));
            Set(d, "OverlayBtnHover", Color.FromArgb(0xCC, 0, 0, 0));
            Set(d, "OverlayBtnBorder", Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            Set(d, "OverlayBtnFg", Colors.White);

            return d;
        }

        // ── Helpers ───────────────────────────────────────────────────
        private static void Set(ResourceDictionary d, string key, Color c)
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            d[key] = brush;
        }

        private static Color Hex(string hex) => ParseHex(hex);

        public static Color ParseHex(string hex)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex)!;
                return c;
            }
            catch
            {
                return Colors.Gray;
            }
        }

        public static string ToHex(Color c) =>
            string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);

        /// <summary>Black or white, whichever stays readable on <paramref name="bg"/>.</summary>
        private static Color ReadableOn(Color bg)
        {
            static double Channel(byte v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            double luminance =
                0.2126 * Channel(bg.R) + 0.7152 * Channel(bg.G) + 0.0722 * Channel(bg.B);

            // Contrast against white vs. against near-black.
            double vsWhite = 1.05 / (luminance + 0.05);
            double vsBlack = (luminance + 0.05) / 0.05;
            return vsBlack >= vsWhite ? Color.FromRgb(0x0B, 0x10, 0x18) : Colors.White;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        /// <summary>HSL to RGB. Hue in degrees, saturation and lightness 0-1.</summary>
        public static Color FromHsl(double h, double s, double l)
        {
            h = ((h % 360) + 360) % 360;
            s = Clamp01(s);
            l = Clamp01(l);

            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = l - c / 2;

            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            static byte To255(double v) => (byte)Math.Round(Clamp01(v) * 255.0);
            return Color.FromRgb(To255(r + m), To255(g + m), To255(b + m));
        }

        /// <summary>RGB to HSL. Hue in degrees, saturation and lightness 0-1.</summary>
        public static (double H, double S, double L) ToHsl(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double l = (max + min) / 2.0;

            if (Math.Abs(max - min) < 1e-9) return (0, 0, l);

            double delta = max - min;
            double s = l > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);

            double h;
            if (Math.Abs(max - r) < 1e-9) h = (g - b) / delta + (g < b ? 6 : 0);
            else if (Math.Abs(max - g) < 1e-9) h = (b - r) / delta + 2;
            else h = (r - g) / delta + 4;

            return (h * 60.0, s, l);
        }
    }
}
