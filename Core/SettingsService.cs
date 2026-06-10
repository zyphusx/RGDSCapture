using System;
using System.IO;
using System.Text.Json;

namespace RGDSCapture.Core
{
    /// <summary>
    /// Loads and saves <see cref="AppSettings"/>. All failures are non-fatal:
    /// a corrupt or missing file simply yields defaults.
    /// </summary>
    public sealed class SettingsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public AppSettings Current { get; private set; } = new();

        public AppSettings Load()
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(
                        File.ReadAllText(AppPaths.SettingsFile));
                    if (loaded != null) Current = loaded;
                }
            }
            catch
            {
                Current = new AppSettings();
            }
            return Current;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.SettingsDir);
                // Write to a temp file first so a crash mid-write can't corrupt settings.
                string tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
                File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
            }
            catch
            {
                // Persistence is best-effort; never crash over settings.
            }
        }
    }
}
