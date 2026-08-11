using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace StrmAssistant.Metadata
{
    public sealed class ChineseImageLanguageOptions
    {
        public bool Enabled { get; set; }
        public string PreferredLanguage { get; set; } = "zh-CN";
        public string FallbackLanguages { get; set; } = "zh,zh-HK,zh-TW";
        public bool ApplyPrimary { get; set; } = true;
        public bool ApplyLogo { get; set; } = true;
    }

    public static class ChineseImageLanguageRuntimeSettings
    {
        private static readonly object Sync = new object();
        private static ChineseImageLanguageOptions _options;
        private static string _path;

        public static string SettingsPath
        {
            get { EnsureLoaded(); return _path; }
        }

        public static ChineseImageLanguageOptions GetSnapshot()
        {
            EnsureLoaded();
            lock (Sync) return Clone(_options);
        }

        public static ChineseImageLanguageOptions Save(ChineseImageLanguageOptions value)
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
                    "PreferredLanguage=" + _options.PreferredLanguage,
                    "FallbackLanguages=" + _options.FallbackLanguages,
                    "ApplyPrimary=" + _options.ApplyPrimary,
                    "ApplyLogo=" + _options.ApplyLogo
                });
                return Clone(_options);
            }
        }

        public static IReadOnlyList<string> GetPriorityLanguages(ChineseImageLanguageOptions options)
        {
            options = Sanitize(options ?? new ChineseImageLanguageOptions());
            var result = new List<string>();
            Add(result, options.PreferredLanguage);
            foreach (var value in Split(options.FallbackLanguages)) Add(result, value);
            return result;
        }

        private static void EnsureLoaded()
        {
            if (Volatile.Read(ref _options) != null) return;
            lock (Sync)
            {
                if (_options != null) return;
                var root = Plugin.Instance?.ApplicationPaths?.CachePath;
                if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
                _path = Path.Combine(root, "strmassistant-custom", "chinese-image-language.conf");
                _options = Load(_path);
            }
        }

        private static ChineseImageLanguageOptions Load(string path)
        {
            var result = new ChineseImageLanguageOptions();
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
                        case "PreferredLanguage": result.PreferredLanguage = value; break;
                        case "FallbackLanguages": result.FallbackLanguages = value; break;
                        case "ApplyPrimary": result.ApplyPrimary = ParseBool(value, true); break;
                        case "ApplyLogo": result.ApplyLogo = ParseBool(value, true); break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Chinese image-language settings load failed: " + ex.Message);
            }
            return Sanitize(result);
        }

        private static ChineseImageLanguageOptions Sanitize(ChineseImageLanguageOptions value)
        {
            var preferred = Normalize(value.PreferredLanguage);
            if (string.IsNullOrWhiteSpace(preferred)) preferred = "zh-CN";
            return new ChineseImageLanguageOptions
            {
                Enabled = value.Enabled,
                PreferredLanguage = preferred,
                FallbackLanguages = string.Join(",", Split(value.FallbackLanguages).Select(Normalize)
                    .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase)),
                ApplyPrimary = value.ApplyPrimary,
                ApplyLogo = value.ApplyLogo
            };
        }

        private static ChineseImageLanguageOptions Clone(ChineseImageLanguageOptions value)
        {
            return Sanitize(value ?? new ChineseImageLanguageOptions());
        }

        private static IEnumerable<string> Split(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim());
        }

        private static void Add(ICollection<string> result, string value)
        {
            value = Normalize(value);
            if (string.IsNullOrWhiteSpace(value) || result.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
            result.Add(value);
        }

        public static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var parts = value.Trim().Replace('_', '-').Split('-');
            if (parts.Length == 1) return parts[0].ToLowerInvariant();
            return parts[0].ToLowerInvariant() + "-" + parts[1].ToUpperInvariant();
        }

        private static bool ParseBool(string value, bool fallback)
        {
            return bool.TryParse(value, out var parsed) ? parsed : fallback;
        }
    }
}
