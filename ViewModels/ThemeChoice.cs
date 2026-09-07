using System.Windows.Media;
using RGDSCapture.Core;
using RGDSCapture.Services;

namespace RGDSCapture.ViewModels
{
    /// <summary>
    /// One entry in the theme picker: the preset plus the few swatch brushes
    /// the tile needs to preview it without applying it.
    /// </summary>
    public sealed class ThemeChoice : ObservableObject
    {
        private readonly Func<ThemePreset, bool> _isActive;

        public ThemeChoice(ThemePreset preset, Func<ThemePreset, bool> isActive, RelayCommand apply)
        {
            Preset = preset;
            _isActive = isActive;
            ApplyCommand = apply;

            StripeSwatch = PaletteBuilder.StripeBrush(preset.Stripes);
            HasStripes = StripeSwatch != null;

            var accent = PaletteBuilder.ParseHex(preset.Accent);
            var (h, s, _) = PaletteBuilder.ToHsl(accent);
            double tintHue = preset.TintHue ?? h;
            double tint = preset.TintStrength;

            // Mirror the ramp PaletteBuilder uses, so a tile actually looks
            // like the theme it applies.
            AccentSwatch = Freeze(accent);
            if (preset.IsDark)
            {
                SurfaceSwatch = Freeze(PaletteBuilder.FromHsl(tintHue, tint * 0.28, 0.082));
                RaisedSwatch = Freeze(PaletteBuilder.FromHsl(tintHue, tint * 0.28, 0.150));
                TextSwatch = Freeze(PaletteBuilder.FromHsl(tintHue, tint * 0.14, 0.905));
            }
            else
            {
                SurfaceSwatch = Freeze(PaletteBuilder.FromHsl(tintHue, tint * 0.24, 0.995));
                RaisedSwatch = Freeze(PaletteBuilder.FromHsl(tintHue, tint * 0.24, 0.900));
                TextSwatch = Freeze(PaletteBuilder.FromHsl(tintHue, tint * 0.14, 0.090));
            }
        }

        public ThemePreset Preset { get; }
        public string Name => Preset.Name;
        public string Id => Preset.Id;
        public bool IsDark => Preset.IsDark;

        public Brush AccentSwatch { get; }
        public Brush SurfaceSwatch { get; }
        public Brush RaisedSwatch { get; }
        public Brush TextSwatch { get; }

        /// <summary>The flag's stripes, or null for a plain theme.</summary>
        public Brush? StripeSwatch { get; }
        public bool HasStripes { get; }

        public RelayCommand ApplyCommand { get; }

        public bool IsActive => _isActive(Preset);
        public void RaiseIsActive() => OnPropertyChanged(nameof(IsActive));

        private static Brush Freeze(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
