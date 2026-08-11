using System;
using System.IO;
using System.Threading;

namespace StrmAssistant.Metadata
{
    public sealed class DoubanAssistOptions
    {
        public bool Enabled { get; set; }
        public string EndpointTemplate { get; set; } = string.Empty;
        public int TimeoutSeconds { get; set; } = 20;
        public bool OnlyFillMissingFields { get; set; } = true;
        public bool EnableMovies { get; set; } = true;
        public bool EnableSeries { get; set; } = true;
    }

    public static class DoubanAssistRuntimeSettings
    {
        private static readonly object Sync = new object();
        private static DoubanAssistOptions _options;
        private static string _path;

        public static string SettingsPath
        {
            get { EnsureLoaded(); return _path; }
        }

        public static DoubanAssistOptions GetSnapshot()
        {
            EnsureLoaded();
            lock (Sync) return Clone(_options);
        }

        public static DoubanAssistOptions Save(DoubanAssistOptions value)
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
                    "EndpointTemplate=" + _options.EndpointTemplate,
                    "TimeoutSeconds=" + _options.TimeoutSeconds,
                    "OnlyFillMissingFields=" + _options.OnlyFillMissingFields,
                    "EnableMovies=" + _options.EnableMovies,
                    "EnableSeries=" + _options.EnableSeries
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
                _path = Path.Combine(root, "strmassistant-custom", "douban-assist.conf");
                _options = Load(_path);
            }
        }

        private static DoubanAssistOptions Load(string path)
        {
            var result = new DoubanAssistOptions();
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
                        case "EndpointTemplate": result.EndpointTemplate = value; break;
                        case "TimeoutSeconds": if (int.TryParse(value, out var timeout)) result.TimeoutSeconds = timeout; break;
                        case "OnlyFillMissingFields": result.OnlyFillMissingFields = ParseBool(value, true); break;
                        case "EnableMovies": result.EnableMovies = ParseBool(value, true); break;
                        case "EnableSeries": result.EnableSeries = ParseBool(value, true); break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Douban Assist settings load failed: " + ex.Message);
            }
            return Sanitize(result);
        }

        private static DoubanAssistOptions Sanitize(DoubanAssistOptions value)
        {
            value ??= new DoubanAssistOptions();
            return new DoubanAssistOptions
            {
                Enabled = value.Enabled,
                EndpointTemplate = value.EndpointTemplate?.Trim() ?? string.Empty,
                TimeoutSeconds = Math.Max(3, Math.Min(value.TimeoutSeconds, 120)),
                OnlyFillMissingFields = value.OnlyFillMissingFields,
                EnableMovies = value.EnableMovies,
                EnableSeries = value.EnableSeries
            };
        }

        private static DoubanAssistOptions Clone(DoubanAssistOptions value)
        {
            return Sanitize(value ?? new DoubanAssistOptions());
        }

        private static bool ParseBool(string value, bool fallback)
        {
            return bool.TryParse(value, out var parsed) ? parsed : fallback;
        }
    }
}
