using System;
using System.IO;
using System.Threading;

namespace StrmAssistant.Experience
{
    public sealed class UiSortRuntimeOptions
    {
        public bool Enabled { get; set; }
        public bool NaturalTitleSort { get; set; }
        public bool ReverseSeasons { get; set; }
        public bool ReverseEpisodes { get; set; }
        public bool CollectionDateDescending { get; set; }
    }

    public static class UiSortRuntimeSettings
    {
        private static readonly object Sync = new object();
        private static UiSortRuntimeOptions _options;
        private static string _path;

        public static string SettingsPath
        {
            get { EnsureLoaded(); return _path; }
        }

        public static UiSortRuntimeOptions GetSnapshot()
        {
            EnsureLoaded();
            lock (Sync) return Clone(_options);
        }

        public static UiSortRuntimeOptions Save(UiSortRuntimeOptions options)
        {
            EnsureLoaded();
            lock (Sync)
            {
                _options = Clone(options ?? new UiSortRuntimeOptions());
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.WriteAllLines(_path, new[]
                {
                    "Enabled=" + _options.Enabled,
                    "NaturalTitleSort=" + _options.NaturalTitleSort,
                    "ReverseSeasons=" + _options.ReverseSeasons,
                    "ReverseEpisodes=" + _options.ReverseEpisodes,
                    "CollectionDateDescending=" + _options.CollectionDateDescending
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
                _path = Path.Combine(root, "strmassistant-custom", "ui-sort-runtime.conf");
                _options = Load(_path);
            }
        }

        private static UiSortRuntimeOptions Load(string path)
        {
            var result = new UiSortRuntimeOptions();
            try
            {
                if (!File.Exists(path)) return result;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var index = raw.IndexOf('=');
                    if (index <= 0) continue;
                    var key = raw.Substring(0, index).Trim();
                    var value = raw.Substring(index + 1).Trim();
                    bool parsed;
                    if (!bool.TryParse(value, out parsed)) continue;
                    switch (key)
                    {
                        case "Enabled": result.Enabled = parsed; break;
                        case "NaturalTitleSort": result.NaturalTitleSort = parsed; break;
                        case "ReverseSeasons": result.ReverseSeasons = parsed; break;
                        case "ReverseEpisodes": result.ReverseEpisodes = parsed; break;
                        case "CollectionDateDescending": result.CollectionDateDescending = parsed; break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("UI sort settings load failed: " + ex.Message);
            }
            return result;
        }

        private static UiSortRuntimeOptions Clone(UiSortRuntimeOptions value)
        {
            value = value ?? new UiSortRuntimeOptions();
            return new UiSortRuntimeOptions
            {
                Enabled = value.Enabled,
                NaturalTitleSort = value.NaturalTitleSort,
                ReverseSeasons = value.ReverseSeasons,
                ReverseEpisodes = value.ReverseEpisodes,
                CollectionDateDescending = value.CollectionDateDescending
            };
        }
    }
}
