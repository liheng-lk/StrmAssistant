using System;
using System.IO;
using System.Threading;

namespace StrmAssistant.Metadata
{
    public sealed class LocalTmdbMetadataOptions
    {
        public bool Enabled { get; set; }
        public string RootPath { get; set; } = string.Empty;
        public bool OnlyFillMissingFields { get; set; } = true;
        public bool EnableMovies { get; set; } = true;
        public bool EnableSeries { get; set; } = true;
        public bool EnableSeasons { get; set; } = true;
        public bool EnableEpisodes { get; set; } = true;
        public bool EnablePeople { get; set; }
    }

    public static class LocalTmdbMetadataRuntimeSettings
    {
        private static readonly object Sync = new object();
        private static LocalTmdbMetadataOptions _options;
        private static string _path;

        public static string SettingsPath
        {
            get { EnsureLoaded(); return _path; }
        }

        public static LocalTmdbMetadataOptions GetSnapshot()
        {
            EnsureLoaded();
            lock (Sync) return Clone(_options);
        }

        public static LocalTmdbMetadataOptions Save(LocalTmdbMetadataOptions value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            EnsureLoaded();
            lock (Sync)
            {
                _options = Sanitize(value);
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.WriteAllLines(_path, new[]
                {
                    "Enabled=" + _options.Enabled,
                    "RootPath=" + _options.RootPath,
                    "OnlyFillMissingFields=" + _options.OnlyFillMissingFields,
                    "EnableMovies=" + _options.EnableMovies,
                    "EnableSeries=" + _options.EnableSeries,
                    "EnableSeasons=" + _options.EnableSeasons,
                    "EnableEpisodes=" + _options.EnableEpisodes,
                    "EnablePeople=" + _options.EnablePeople
                });
                return Clone(_options);
            }
        }

        private static void EnsureLoaded()
        {
            if (Volatile.Read(ref _options) != null) return;
            lock (Sync)
            {
                if (_options != null) return;
                var root = Plugin.Instance?.ApplicationPaths?.CachePath;
                if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
                _path = Path.Combine(root, "strmassistant-custom", "local-tmdb-metadata.conf");
                _options = Load(_path);
            }
        }

        private static LocalTmdbMetadataOptions Load(string path)
        {
            var result = new LocalTmdbMetadataOptions();
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
                        case "RootPath": result.RootPath = value; break;
                        case "OnlyFillMissingFields": result.OnlyFillMissingFields = ParseBool(value, true); break;
                        case "EnableMovies": result.EnableMovies = ParseBool(value, true); break;
                        case "EnableSeries": result.EnableSeries = ParseBool(value, true); break;
                        case "EnableSeasons": result.EnableSeasons = ParseBool(value, true); break;
                        case "EnableEpisodes": result.EnableEpisodes = ParseBool(value, true); break;
                        case "EnablePeople": result.EnablePeople = ParseBool(value, false); break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Local TMDB settings load failed: " + ex.Message);
            }
            return Sanitize(result);
        }

        private static LocalTmdbMetadataOptions Sanitize(LocalTmdbMetadataOptions value)
        {
            value ??= new LocalTmdbMetadataOptions();
            return new LocalTmdbMetadataOptions
            {
                Enabled = value.Enabled,
                RootPath = value.RootPath?.Trim() ?? string.Empty,
                OnlyFillMissingFields = value.OnlyFillMissingFields,
                EnableMovies = value.EnableMovies,
                EnableSeries = value.EnableSeries,
                EnableSeasons = value.EnableSeasons,
                EnableEpisodes = value.EnableEpisodes,
                EnablePeople = value.EnablePeople
            };
        }

        private static LocalTmdbMetadataOptions Clone(LocalTmdbMetadataOptions value)
        {
            return Sanitize(value ?? new LocalTmdbMetadataOptions());
        }

        private static bool ParseBool(string value, bool fallback)
        {
            return bool.TryParse(value, out var parsed) ? parsed : fallback;
        }
    }
}
