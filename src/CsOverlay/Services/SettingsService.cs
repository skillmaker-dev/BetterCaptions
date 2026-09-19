using System;
using System.IO;
using System.Text.Json;
using CsOverlay.Models;

namespace CsOverlay.Services
{
    /// <summary>
    /// Loads and saves <see cref="AppSettings"/> to
    /// %LOCALAPPDATA%\CsOverlay\settings.json.
    /// </summary>
    public sealed class SettingsService
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private readonly string _settingsPath;

        public SettingsService()
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CsOverlay");

            Directory.CreateDirectory(directory);
            _settingsPath = Path.Combine(directory, "settings.json");
            Settings = Load();
        }

        public AppSettings Settings { get; private set; }

        public string SettingsPath => _settingsPath;

        private AppSettings Load()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    string json = File.ReadAllText(_settingsPath);
                    AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
                    if (loaded is not null)
                    {
                        return loaded;
                    }
                }
            }
            catch
            {
                // Corrupt or unreadable settings file: fall back to defaults.
            }

            var defaults = new AppSettings();
            TrySave(defaults);
            return defaults;
        }

        public void Save() => TrySave(Settings);

        private void TrySave(AppSettings settings)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings, SerializerOptions);
                File.WriteAllText(_settingsPath, json);
            }
            catch
            {
                // Settings persistence must never crash the overlay.
            }
        }
    }
}
