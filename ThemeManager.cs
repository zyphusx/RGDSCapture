using System;
using System.IO;
using System.Windows;

namespace RGDSCapture
{
    public static class ThemeManager
    {
        public enum Theme
        {
            Dark,
            Light,
            HighContrastDark,
            HighContrastLight
        }

        private static Theme _current = Theme.Dark;
        public  static Theme Current  => _current;

        private static readonly string SettingsFile =
            Path.Combine(AppContext.BaseDirectory, "theme.cfg");

        private static readonly string[] ThemeUris =
        {
            "Themes/Dark.xaml",
            "Themes/Light.xaml",
            "Themes/HighContrastDark.xaml",
            "Themes/HighContrastLight.xaml"
        };

        public static readonly string[] ThemeDisplayNames =
        {
            "Dark",
            "Light",
            "High Contrast Dark",
            "High Contrast Light"
        };

        public static Theme Load()
        {
            try
            {
                if (File.Exists(SettingsFile) &&
                    Enum.TryParse(File.ReadAllText(SettingsFile).Trim(), out Theme t))
                    _current = t;
            }
            catch { }

            Apply(_current);
            return _current;
        }

        public static void Apply(Theme theme)
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri(ThemeUris[(int)theme], UriKind.Relative)
            };

            var merged = Application.Current.Resources.MergedDictionaries;
            if (merged.Count > 0) merged[0] = dict;
            else                  merged.Add(dict);

            _current = theme;
            Save();
        }

        private static void Save()
        {
            try { File.WriteAllText(SettingsFile, _current.ToString()); }
            catch { }
        }
    }
}
