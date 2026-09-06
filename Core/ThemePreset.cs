using System.Collections.Generic;
using System.Linq;

namespace RGDSCapture.Core
{
    /// <summary>
    /// A theme described by its seeds rather than by ~100 hand-picked colors.
    /// <see cref="Services.PaletteBuilder"/> derives the full brush set from
    /// these few values, so adding a preset costs one line instead of a new
    /// 200-line resource dictionary.
    /// </summary>
    /// <param name="Id">Stable key persisted in settings. Never rename.</param>
    /// <param name="Name">Display name in the theme picker.</param>
    /// <param name="IsDark">Selects the dark or light lightness ramp.</param>
    /// <param name="Accent">Accent color as #RRGGBB — buttons, selection, focus.</param>
    /// <param name="TintStrength">
    /// How strongly the accent hue bleeds into the greys, 0 (pure neutral) to
    /// 1 (heavily colored surfaces).
    /// </param>
    /// <param name="TintHue">
    /// Optional surface hue in degrees, when the greys should lean somewhere
    /// other than the accent — e.g. a magenta accent over cool blue surfaces.
    /// </param>
    public sealed record ThemePreset(
        string Id,
        string Name,
        bool IsDark,
        string Accent,
        double TintStrength,
        double? TintHue = null);

    /// <summary>The built-in theme catalog.</summary>
    public static class ThemeCatalog
    {
        public const string DefaultId = "midnight";

        public static IReadOnlyList<ThemePreset> All { get; } = new[]
        {
            // ── Dark ──────────────────────────────────────────────────
            new ThemePreset("midnight",  "Midnight",  true,  "#4C8DFF", 0.22, 222),
            new ThemePreset("amethyst",  "Amethyst",  true,  "#A855F7", 0.62, 272),
            new ThemePreset("nocturne",  "Nocturne",  true,  "#818CF8", 0.42, 245),
            new ThemePreset("phosphor",  "Phosphor",  true,  "#34D399", 0.34, 155),
            new ThemePreset("ember",     "Ember",     true,  "#FB923C", 0.38, 24),
            new ThemePreset("crimson",   "Crimson",   true,  "#F43F5E", 0.40, 348),
            new ThemePreset("neon",      "Neon",      true,  "#F0ABFC", 0.50, 292),
            new ThemePreset("ocean",     "Ocean",     true,  "#22D3EE", 0.38, 195),
            new ThemePreset("forest",    "Forest",    true,  "#4ADE80", 0.30, 140),
            new ThemePreset("amber",     "Amber",     true,  "#FBBF24", 0.32, 38),
            new ThemePreset("sakura",    "Sakura",    true,  "#F472B6", 0.44, 330),
            new ThemePreset("mono",      "Mono",      true,  "#D4D4D8", 0.00),

            // ── Light ─────────────────────────────────────────────────
            new ThemePreset("daylight",  "Daylight",  false, "#2563EB", 0.18, 222),
            new ThemePreset("parchment", "Parchment", false, "#B45309", 0.34, 36),
            new ThemePreset("mint",      "Mint",      false, "#0D9488", 0.26, 172),
        };

        public static ThemePreset Default =>
            All.First(t => t.Id == DefaultId);

        /// <summary>
        /// Ids that older settings files may still hold, mapped to their
        /// current equivalents. Without these an upgrading user silently
        /// loses the theme they chose.
        /// </summary>
        private static readonly Dictionary<string, string> LegacyIds =
            new(System.StringComparer.OrdinalIgnoreCase)
            {
                // Through 2.2.0 the theme was a two-value enum, not a preset id.
                ["Dark"] = DefaultId,
                ["Light"] = "daylight",
            };

        /// <summary>
        /// Resolves a persisted id, mapping any legacy value forward and
        /// falling back to the default rather than throwing.
        /// </summary>
        public static ThemePreset Resolve(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return Default;

            if (LegacyIds.TryGetValue(id, out var mapped)) id = mapped;

            return All.FirstOrDefault(
                t => string.Equals(t.Id, id, System.StringComparison.OrdinalIgnoreCase))
                ?? Default;
        }
    }
}
