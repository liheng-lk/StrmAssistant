using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace StrmAssistant.Experience
{
    public sealed class ForcedUserPreferencesOptions
    {
        public bool Enabled { get; set; }
        public bool ForceLibraryOrder { get; set; }
        public string LibraryOrderIds { get; set; } = string.Empty;
        public bool ForceDisplayMissingEpisodes { get; set; }
        public bool DisplayMissingEpisodes { get; set; } = true;
    }

    public static class ForcedUserPreferencesRuntimeSettings
    {
        private static readonly object Sync = new object();
        private static ForcedUserPreferencesOptions _options;
        private static string _path;

        public static string SettingsPath
        {
            get { EnsureLoaded(); return _path; }
        }

        public static ForcedUserPreferencesOptions GetSnapshot()
        {
            EnsureLoaded();
            lock (Sync) return Clone(_options);
        }

        public static ForcedUserPreferencesOptions Save(ForcedUserPreferencesOptions options)
        {
            EnsureLoaded();
            lock (Sync)
            {
                _options = Sanitize(options ?? new ForcedUserPreferencesOptions());
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.WriteAllLines(_path, new[]
                {
                    "Enabled=" + _options.Enabled,
                    "ForceLibraryOrder=" + _options.ForceLibraryOrder,
                    "LibraryOrderIds=" + _options.LibraryOrderIds,
                    "ForceDisplayMissingEpisodes=" + _options.ForceDisplayMissingEpisodes,
                    "DisplayMissingEpisodes=" + _options.DisplayMissingEpisodes
                });
                return Clone(_options);
            }
        }

        public static string[] GetLibraryOrderIds()
        {
            var value = GetSnapshot().LibraryOrderIds;
            return (value ?? string.Empty)
                .Split(new[] { ',', ';', '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void EnsureLoaded()
        {
            if (Volatile.Read(ref _options) != null) return;
            lock (Sync)
            {
                if (_options != null) return;
                var root = Plugin.Instance?.ApplicationPaths?.CachePath;
                if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
                _path = Path.Combine(root, "strmassistant-custom", "forced-user-preferences.conf");
                _options = Load(_path);
            }
        }

        private static ForcedUserPreferencesOptions Load(string path)
        {
            var result = new ForcedUserPreferencesOptions();
            try
            {
                if (!File.Exists(path)) return result;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var index = raw.IndexOf('=');
                    if (index <= 0) continue;
                    var key = raw.Substring(0, index).Trim();
                    var value = raw.Substring(index + 1).Trim();
                    switch (key)
                    {
                        case "Enabled": result.Enabled = ParseBool(value, false); break;
                        case "ForceLibraryOrder": result.ForceLibraryOrder = ParseBool(value, false); break;
                        case "LibraryOrderIds": result.LibraryOrderIds = value; break;
                        case "ForceDisplayMissingEpisodes": result.ForceDisplayMissingEpisodes = ParseBool(value, false); break;
                        case "DisplayMissingEpisodes": result.DisplayMissingEpisodes = ParseBool(value, true); break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Forced user preferences settings load failed: " + ex.Message);
            }
            return Sanitize(result);
        }

        private static ForcedUserPreferencesOptions Sanitize(ForcedUserPreferencesOptions value)
        {
            return new ForcedUserPreferencesOptions
            {
                Enabled = value.Enabled,
                ForceLibraryOrder = value.ForceLibraryOrder,
                LibraryOrderIds = string.Join(",", (value.LibraryOrderIds ?? string.Empty)
                    .Split(new[] { ',', ';', '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(NormalizeId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)),
                ForceDisplayMissingEpisodes = value.ForceDisplayMissingEpisodes,
                DisplayMissingEpisodes = value.DisplayMissingEpisodes
            };
        }

        private static ForcedUserPreferencesOptions Clone(ForcedUserPreferencesOptions value)
        {
            return Sanitize(value ?? new ForcedUserPreferencesOptions());
        }

        private static string NormalizeId(string value)
        {
            var text = value?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (Guid.TryParse(text, out var guid)) return guid.ToString("N");
            return text.Replace("-", string.Empty);
        }

        private static bool ParseBool(string value, bool fallback)
        {
            return bool.TryParse(value, out var parsed) ? parsed : fallback;
        }
    }
}
