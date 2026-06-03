using System;
using System.IO;
using System.Windows;

namespace RGDSCapture
{
    /// <summary>
    /// Swaps the application theme at runtime by replacing the first merged
    /// ResourceDictionary in Application.Resources with the chosen theme file.
    /// Theme choice is persisted to a simple text file next to the executable.
    /// </summary>
    public static class ThemeManager
    {
        public enum Theme { Dark, Light }

        private static Theme _current = Theme.Dark;
        public  static Theme Current  => _current;

        private static readonly string SettingsFile = Path.Combine(
            AppContext.BaseDirectory, "theme.cfg");

        // ── Load persisted choice (call once at startup) ──────────────
        public static Theme Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string val = File.ReadAllText(SettingsFile).Trim();
                    if (Enum.TryParse(val, out Theme t))
                        _current = t;
                }
            }
            catch { /* ignore — default to Dark */ }

            Apply(_current);
            return _current;
        }

        // ── Toggle between Dark and Light ────────────────────────────
        public static Theme Toggle()
        {
            _current = _current == Theme.Dark ? Theme.Light : Theme.Dark;
            Apply(_current);
            Save();
            return _current;
        }

        // ── Apply a specific theme ────────────────────────────────────
        public static void Apply(Theme theme)
        {
            string uri = theme == Theme.Dark
                ? "Themes/Dark.xaml"
                : "Themes/Light.xaml";

            var dict = new ResourceDictionary
            {
                Source = new Uri(uri, UriKind.Relative)
            };

            var appDicts = Application.Current.Resources.MergedDictionaries;
            if (appDicts.Count > 0)
                appDicts[0] = dict;   // replace in-place — DynamicResource picks it up instantly
            else
                appDicts.Add(dict);

            _current = theme;
        }

        private static void Save()
        {
            try { File.WriteAllText(SettingsFile, _current.ToString()); }
            catch { /* best-effort */ }
        }
    }
}
