using System;
using System.Windows;
using RGDSCapture.Core;

namespace RGDSCapture.Services
{
    /// <summary>
    /// Swaps the active theme color dictionary at runtime. The theme dictionary
    /// is always merged dictionary index 0 (see App.xaml); control styles in
    /// Controls.xaml reference colors via DynamicResource so they restyle live.
    /// </summary>
    public static class ThemeService
    {
        public static AppTheme Current { get; private set; } = AppTheme.Dark;

        public static void Apply(AppTheme theme)
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri($"Themes/{theme}.xaml", UriKind.Relative)
            };

            var merged = Application.Current.Resources.MergedDictionaries;
            if (merged.Count > 0) merged[0] = dict;
            else merged.Add(dict);

            Current = theme;
        }
    }
}
