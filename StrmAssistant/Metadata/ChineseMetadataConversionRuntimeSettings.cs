using System;
using System.IO;
using System.Threading;

namespace StrmAssistant.Metadata
{
    public sealed class ChineseMetadataConversionOptions
    {
        public bool Enabled { get; set; }
        public bool ConvertName { get; set; } = true;
        public bool ConvertOverview { get; set; } = true;
        public bool ConvertTagline { get; set; } = true;
        public bool ConvertPersonName { get; set; } = true;
        public bool OnlyForSimplifiedChineseRequests { get; set; } = true;
    }

    public static class ChineseMetadataConversionRuntimeSettings
    {
        private static readonly object Sync = new object();
        private static ChineseMetadataConversionOptions _options;
        private static string _path;

        public static string SettingsPath
        {
            get { EnsureLoaded(); return _path; }
        }

        public static ChineseMetadataConversionOptions GetSnapshot()
        {
            EnsureLoaded();
            lock (Sync) return Clone(_options);
        }

        public static ChineseMetadataConversionOptions Save(ChineseMetadataConversionOptions value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            EnsureLoaded();
            lock (Sync)
            {
                _options = Clone(value);
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.WriteAllLines(_path, new[]
                {
                    "Enabled=" + _options.Enabled,
                    "ConvertName=" + _options.ConvertName,
                    "ConvertOverview=" + _options.ConvertOverview,
                    "ConvertTagline=" + _options.ConvertTagline,
                    "ConvertPersonName=" + _options.ConvertPersonName,
                    "OnlyForSimplifiedChineseRequests=" + _options.OnlyForSimplifiedChineseRequests
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
                _path = Path.Combine(root, "strmassistant-custom", "chinese-metadata-conversion.conf");
                _options = Load(_path);
            }
        }

        private static ChineseMetadataConversionOptions Load(string path)
        {
            var result = new ChineseMetadataConversionOptions();
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
                        case "ConvertName": result.ConvertName = ParseBool(value, true); break;
                        case "ConvertOverview": result.ConvertOverview = ParseBool(value, true); break;
                        case "ConvertTagline": result.ConvertTagline = ParseBool(value, true); break;
                        case "ConvertPersonName": result.ConvertPersonName = ParseBool(value, true); break;
                        case "OnlyForSimplifiedChineseRequests": result.OnlyForSimplifiedChineseRequests = ParseBool(value, true); break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Chinese metadata conversion settings load failed: " + ex.Message);
            }
            return result;
        }

        private static ChineseMetadataConversionOptions Clone(ChineseMetadataConversionOptions value)
        {
            value ??= new ChineseMetadataConversionOptions();
            return new ChineseMetadataConversionOptions
            {
                Enabled = value.Enabled,
                ConvertName = value.ConvertName,
                ConvertOverview = value.ConvertOverview,
                ConvertTagline = value.ConvertTagline,
                ConvertPersonName = value.ConvertPersonName,
                OnlyForSimplifiedChineseRequests = value.OnlyForSimplifiedChineseRequests
            };
        }

        private static bool ParseBool(string value, bool fallback)
        {
            return bool.TryParse(value, out var parsed) ? parsed : fallback;
        }
    }
}
