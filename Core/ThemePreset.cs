
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
    /// <param name="Group">Which section of the theme picker this appears under.</param>
    /// <param name="Stripes">
    /// Optional flag colours, outermost first. Purely decorative: they drive
    /// the picker tile and the accent dot, never anything that has text on it —
    /// a six-stripe gradient behind a label cannot hold a contrast ratio.
    /// </param>
    public sealed record ThemePreset(
        string Id,
        string Name,
        bool IsDark,
        string Accent,
        double TintStrength,
        double? TintHue = null,
        string Group = ThemeGroups.Dark,
        string[]? Stripes = null);

    /// <summary>Section headings in the theme picker.</summary>
    public static class ThemeGroups
    {
        public const string Dark = "Dark";
        public const string Light = "Light";
        public const string Pride = "Pride";
    }

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

            // Accents here sit around 60-75% lightness so they still read as
            // an accent against a dark surface. Where two themes share a
            // neighbourhood on the colour wheel they differ sharply in tint
            // strength instead — Rust is a near-neutral warm grey next to
            // Ember's fully orange surfaces.
            new ThemePreset("scarlet",   "Scarlet",   true,  "#EF4444", 0.42, 2),
            new ThemePreset("rust",      "Rust",      true,  "#F97316", 0.20, 22),
            new ThemePreset("saffron",   "Saffron",   true,  "#FDE047", 0.30, 51),
            new ThemePreset("citrus",    "Citrus",    true,  "#A3E635", 0.34, 82),
            new ThemePreset("moss",      "Moss",      true,  "#86EFAC", 0.26, 108),
            new ThemePreset("lagoon",    "Lagoon",    true,  "#2DD4BF", 0.36, 174),
            new ThemePreset("skyline",   "Skyline",   true,  "#38BDF8", 0.34, 199),
            new ThemePreset("cobalt",    "Cobalt",    true,  "#60A5FA", 0.44, 213),
            new ThemePreset("violet",    "Violet",    true,  "#A78BFA", 0.46, 258),
            new ThemePreset("orchid",    "Orchid",    true,  "#E879F9", 0.50, 300),
            new ThemePreset("wine",      "Wine",      true,  "#FDA4AF", 0.54, 345),
            new ThemePreset("slate",     "Slate",     true,  "#94A3B8", 0.12, 215),
            new ThemePreset("sepia",     "Sepia",     true,  "#D6BFA0", 0.16, 38),

            // ── Light ─────────────────────────────────────────────────
            // The mirror of the dark ramp: accents drop to roughly 35-45%
            // lightness so white label text on an accent-filled button keeps
            // its contrast, and tint strengths stay lower because a colour
            // cast reads much more strongly on near-white surfaces.
            new ThemePreset("daylight",  "Daylight",  false, "#2563EB", 0.18, 222, ThemeGroups.Light),
            new ThemePreset("parchment", "Parchment", false, "#B45309", 0.34, 36,  ThemeGroups.Light),
            new ThemePreset("mint",      "Mint",      false, "#0D9488", 0.26, 172, ThemeGroups.Light),
            new ThemePreset("poppy",     "Poppy",     false, "#DC2626", 0.16, 0,   ThemeGroups.Light),
            new ThemePreset("rose",      "Rose",      false, "#E11D48", 0.20, 346, ThemeGroups.Light),
            new ThemePreset("clay",      "Clay",      false, "#C2410C", 0.26, 20,  ThemeGroups.Light),
            new ThemePreset("honey",     "Honey",     false, "#CA8A04", 0.24, 45,  ThemeGroups.Light),
            new ThemePreset("olive",     "Olive",     false, "#4D7C0F", 0.22, 84,  ThemeGroups.Light),
            new ThemePreset("fern",      "Fern",      false, "#15803D", 0.20, 142, ThemeGroups.Light),
            new ThemePreset("seafoam",   "Seafoam",   false, "#0F766E", 0.24, 175, ThemeGroups.Light),
            new ThemePreset("sky",       "Sky",       false, "#0284C7", 0.22, 200, ThemeGroups.Light),
            new ThemePreset("indigo",    "Indigo",    false, "#4338CA", 0.24, 245, ThemeGroups.Light),
            new ThemePreset("lavender",  "Lavender",  false, "#7C3AED", 0.28, 262, ThemeGroups.Light),
            new ThemePreset("mauve",     "Mauve",     false, "#A21CAF", 0.30, 295, ThemeGroups.Light),
            new ThemePreset("blossom",   "Blossom",   false, "#DB2777", 0.26, 330, ThemeGroups.Light),
            new ThemePreset("porcelain", "Porcelain", false, "#475569", 0.06, 215, ThemeGroups.Light),
            new ThemePreset("linen",     "Linen",     false, "#78716C", 0.10, 35,  ThemeGroups.Light),

            // ── Pride ─────────────────────────────────────────────────
            // Each accent is taken from its flag, lifted in lightness where
            // the flag colour would be too dark to read as UI accent on a
            // dark surface. Stripes are the flags' real colours.
            new ThemePreset("pride", "Rainbow", true, "#A24BD8", 0.40, 285, ThemeGroups.Pride,
                new[] { "#E40303", "#FF8C00", "#FFED00", "#008026", "#24408E", "#732982" }),

            new ThemePreset("progress", "Progress", true, "#74D7EE", 0.34, 194, ThemeGroups.Pride,
                new[] { "#E40303", "#FF8C00", "#FFED00", "#008026", "#24408E", "#732982",
                        "#FFFFFF", "#FFAFC8", "#74D7EE", "#613915", "#000000" }),

            new ThemePreset("trans", "Trans", true, "#5BCEFA", 0.36, 197, ThemeGroups.Pride,
                new[] { "#5BCEFA", "#F5A9B8", "#FFFFFF", "#F5A9B8", "#5BCEFA" }),

            new ThemePreset("bisexual", "Bisexual", true, "#E8408F", 0.44, 322, ThemeGroups.Pride,
                new[] { "#D60270", "#D60270", "#9B4F96", "#0038A8", "#0038A8" }),

            new ThemePreset("pansexual", "Pansexual", true, "#21B1FF", 0.40, 203, ThemeGroups.Pride,
                new[] { "#FF218C", "#FFD800", "#21B1FF" }),

            new ThemePreset("lesbian", "Lesbian", true, "#E4649B", 0.42, 335, ThemeGroups.Pride,
                new[] { "#D52D00", "#FF9A56", "#FFFFFF", "#D362A4", "#A30262" }),

            new ThemePreset("nonbinary", "Non-binary", true, "#9C59D1", 0.46, 272, ThemeGroups.Pride,
                new[] { "#FCF434", "#FFFFFF", "#9C59D1", "#2C2C2C" }),

            new ThemePreset("asexual", "Asexual", true, "#A855C7", 0.30, 288, ThemeGroups.Pride,
                new[] { "#000000", "#A3A3A3", "#FFFFFF", "#800080" }),
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
