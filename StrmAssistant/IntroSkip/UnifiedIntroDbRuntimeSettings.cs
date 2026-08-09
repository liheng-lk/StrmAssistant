using System;
using System.IO;
using System.Threading;

namespace StrmAssistant.IntroSkip
{
    public sealed class UnifiedIntroDbOptions
    {
        public bool Enabled { get; set; }
        public string EndpointTemplate { get; set; } = string.Empty;
        public int TimeoutSeconds { get; set; } = 15;
        public double MinimumConfidence { get; set; } = 0.75;
        public bool AllowCreditsMarker { get; set; } = true;
        public bool OverwriteExistingMarkers { get; set; }
    }

    public static class UnifiedIntroDbRuntimeSettings
    {
        private static readonly object Sync = new object();
        private static UnifiedIntroDbOptions _options;
        private static string _path;

        public static string SettingsPath
        {
            get { EnsureLoaded(); return _path; }
        }

        public static UnifiedIntroDbOptions GetSnapshot()
        {
            EnsureLoaded();
            lock (Sync) return Clone(_options);
        }

        public static UnifiedIntroDbOptions Save(UnifiedIntroDbOptions value)
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
                    "MinimumConfidence=" + _options.MinimumConfidence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "AllowCreditsMarker=" + _options.AllowCreditsMarker,
                    "OverwriteExistingMarkers=" + _options.OverwriteExistingMarkers
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
                _path = Path.Combine(root, "strmassistant-custom", "unified-introdb.conf");
                _options = Load(_path);
            }
        }

        private static UnifiedIntroDbOptions Load(string path)
        {
            var result = new UnifiedIntroDbOptions();
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
                        case "MinimumConfidence": if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var confidence)) result.MinimumConfidence = confidence; break;
                        case "AllowCreditsMarker": result.AllowCreditsMarker = ParseBool(value, true); break;
                        case "OverwriteExistingMarkers": result.OverwriteExistingMarkers = ParseBool(value, false); break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Unified IntroDb settings load failed: " + ex.Message);
            }
            return Sanitize(result);
        }

        private static UnifiedIntroDbOptions Sanitize(UnifiedIntroDbOptions value)
        {
            value ??= new UnifiedIntroDbOptions();
            return new UnifiedIntroDbOptions
            {
                Enabled = value.Enabled,
                EndpointTemplate = value.EndpointTemplate?.Trim() ?? string.Empty,
                TimeoutSeconds = Math.Max(3, Math.Min(value.TimeoutSeconds, 120)),
                MinimumConfidence = Math.Max(0, Math.Min(value.MinimumConfidence, 1)),
                AllowCreditsMarker = value.AllowCreditsMarker,
                OverwriteExistingMarkers = value.OverwriteExistingMarkers
            };
        }

        private static UnifiedIntroDbOptions Clone(UnifiedIntroDbOptions value)
        {
            return Sanitize(value ?? new UnifiedIntroDbOptions());
        }

        private static bool ParseBool(string value, bool fallback)
        {
            return bool.TryParse(value, out var parsed) ? parsed : fallback;
        }
    }
}
