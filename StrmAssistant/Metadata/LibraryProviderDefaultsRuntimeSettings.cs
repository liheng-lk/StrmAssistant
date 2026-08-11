using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace StrmAssistant.Metadata
{
    public sealed class LibraryProviderDefaultsOptions
    {
        public bool Enabled { get; set; }
        public string ProviderName { get; set; } = "TheMovieDb";
        public bool ApplyMetadataFetcher { get; set; } = true;
        public bool ApplyImageFetcher { get; set; } = true;
        public bool OnlyWhenFetcherListEmpty { get; set; } = true;
        public string CollectionTypes { get; set; } = "movies,tvshows";
    }

    public static class LibraryProviderDefaultsRuntimeSettings
    {
        private static readonly object Sync = new object();
        private static LibraryProviderDefaultsOptions _options;
        private static string _path;

        public static string SettingsPath
        {
            get { EnsureLoaded(); return _path; }
        }

        public static LibraryProviderDefaultsOptions GetSnapshot()
        {
            EnsureLoaded();
            lock (Sync) return Clone(_options);
        }

        public static LibraryProviderDefaultsOptions Save(LibraryProviderDefaultsOptions value)
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
                    "ProviderName=" + _options.ProviderName,
                    "ApplyMetadataFetcher=" + _options.ApplyMetadataFetcher,
                    "ApplyImageFetcher=" + _options.ApplyImageFetcher,
                    "OnlyWhenFetcherListEmpty=" + _options.OnlyWhenFetcherListEmpty,
                    "CollectionTypes=" + _options.CollectionTypes
                });
                return Clone(_options);
            }
        }

        public static HashSet<string> GetCollectionTypes(LibraryProviderDefaultsOptions options)
        {
            return new HashSet<string>(Split(options?.CollectionTypes), StringComparer.OrdinalIgnoreCase);
        }

        private static void EnsureLoaded()
        {
            if (Volatile.Read(ref _options) != null) return;
            lock (Sync)
            {
                if (_options != null) return;
                var root = Plugin.Instance?.ApplicationPaths?.CachePath;
                if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
                _path = Path.Combine(root, "strmassistant-custom", "library-provider-defaults.conf");
                _options = Load(_path);
            }
        }

        private static LibraryProviderDefaultsOptions Load(string path)
        {
            var result = new LibraryProviderDefaultsOptions();
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
                        case "ProviderName": result.ProviderName = value; break;
                        case "ApplyMetadataFetcher": result.ApplyMetadataFetcher = ParseBool(value, true); break;
                        case "ApplyImageFetcher": result.ApplyImageFetcher = ParseBool(value, true); break;
                        case "OnlyWhenFetcherListEmpty": result.OnlyWhenFetcherListEmpty = ParseBool(value, true); break;
                        case "CollectionTypes": result.CollectionTypes = value; break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Library provider defaults settings load failed: " + ex.Message);
            }
            return Sanitize(result);
        }

        private static LibraryProviderDefaultsOptions Sanitize(LibraryProviderDefaultsOptions value)
        {
            value ??= new LibraryProviderDefaultsOptions();
            var provider = string.IsNullOrWhiteSpace(value.ProviderName) ? "TheMovieDb" : value.ProviderName.Trim();
            return new LibraryProviderDefaultsOptions
            {
                Enabled = value.Enabled,
                ProviderName = provider.Length > 120 ? provider.Substring(0, 120) : provider,
                ApplyMetadataFetcher = value.ApplyMetadataFetcher,
                ApplyImageFetcher = value.ApplyImageFetcher,
                OnlyWhenFetcherListEmpty = value.OnlyWhenFetcherListEmpty,
                CollectionTypes = string.Join(",", Split(value.CollectionTypes).Distinct(StringComparer.OrdinalIgnoreCase))
            };
        }

        private static LibraryProviderDefaultsOptions Clone(LibraryProviderDefaultsOptions value)
        {
            return Sanitize(value ?? new LibraryProviderDefaultsOptions());
        }

        private static IEnumerable<string> Split(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => v.Length > 0);
        }

        private static bool ParseBool(string value, bool fallback)
        {
            return bool.TryParse(value, out var parsed) ? parsed : fallback;
        }
    }
}
