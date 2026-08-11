using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace StrmAssistant.Experience
{
    public sealed class MultiVersionRuntimeOptions
    {
        public bool Enabled { get; set; }
        public bool RenameSources { get; set; } = true;
        public bool SortHighestQualityFirst { get; set; }
        public bool IncludeFileName { get; set; } = true;
        public bool IncludeContainer { get; set; }
        public string Separator { get; set; } = " · ";
        public bool IsolateUserDataPerVersion { get; set; }
    }

    /// <summary>
    /// Small standalone option store for the multi-version DTO/display layer. It deliberately
    /// does not reuse Emby's media database and is default-off. The file contains only simple
    /// key/value pairs and can be deleted to restore defaults.
    /// </summary>
    public static class MultiVersionRuntimeSettings
    {
        private static readonly object Sync = new object();
        private static MultiVersionRuntimeOptions _options;
        private static string _path;

        public static string SettingsPath
        {
            get
            {
                EnsureLoaded();
                return _path;
            }
        }

        public static MultiVersionRuntimeOptions GetSnapshot()
        {
            EnsureLoaded();
            lock (Sync)
            {
                return Clone(_options);
            }
        }

        public static MultiVersionRuntimeOptions Save(MultiVersionRuntimeOptions value)
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
                    "RenameSources=" + _options.RenameSources,
                    "SortHighestQualityFirst=" + _options.SortHighestQualityFirst,
                    "IncludeFileName=" + _options.IncludeFileName,
                    "IncludeContainer=" + _options.IncludeContainer,
                    "Separator=" + Escape(_options.Separator),
                    "IsolateUserDataPerVersion=" + _options.IsolateUserDataPerVersion
                });

                return Clone(_options);
            }
        }

        public static List<MediaSourceInfo> Enhance(IList<MediaSourceInfo> sources)
        {
            var options = GetSnapshot();
            if (!options.Enabled || sources == null || sources.Count <= 1)
                return sources?.ToList() ?? new List<MediaSourceInfo>();

            var working = sources.Where(source => source != null).ToList();
            if (options.RenameSources)
            {
                var usedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var source in working)
                {
                    var name = BuildName(source, options);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    if (!usedNames.TryGetValue(name, out var count))
                    {
                        usedNames[name] = 1;
                        source.Name = name;
                    }
                    else
                    {
                        count++;
                        usedNames[name] = count;
                        source.Name = name + " #" + count.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }

            if (options.SortHighestQualityFirst)
            {
                working = working
                    .Select((source, index) => new { source, index })
                    .OrderByDescending(entry => QualityScore(entry.source))
                    .ThenBy(entry => entry.index)
                    .Select(entry => entry.source)
                    .ToList();
            }

            return working;
        }

        private static string BuildName(MediaSourceInfo source, MultiVersionRuntimeOptions options)
        {
            var parts = new List<string>();
            var quality = GetQualityLabel(source);
            if (!string.IsNullOrWhiteSpace(quality)) parts.Add(quality);

            if (options.IncludeContainer && !string.IsNullOrWhiteSpace(source.Container))
                parts.Add(source.Container.ToUpperInvariant());

            if (options.IncludeFileName && !string.IsNullOrWhiteSpace(source.Path))
            {
                try
                {
                    var file = Path.GetFileNameWithoutExtension(source.Path);
                    if (!string.IsNullOrWhiteSpace(file)) parts.Add(file);
                }
                catch
                {
                    // Preserve the existing source name if the path cannot be parsed.
                }
            }

            if (parts.Count == 0) return source.Name;
            return string.Join(options.Separator, parts.Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static string GetQualityLabel(MediaSourceInfo source)
        {
            var video = source.MediaStreams?
                .Where(stream => stream.Type == MediaStreamType.Video)
                .OrderByDescending(stream => (long)stream.Width.GetValueOrDefault() * stream.Height.GetValueOrDefault())
                .FirstOrDefault();

            var height = video?.Height.GetValueOrDefault() ?? 0;
            var width = video?.Width.GetValueOrDefault() ?? 0;
            if (height >= 4000 || width >= 7000) return "8K";
            if (height >= 2000 || width >= 3800) return "2160p";
            if (height >= 1400 || width >= 2500) return "1440p";
            if (height >= 1000 || width >= 1800) return "1080p";
            if (height >= 700 || width >= 1200) return "720p";
            if (height > 0) return height.ToString(CultureInfo.InvariantCulture) + "p";
            return null;
        }

        private static long QualityScore(MediaSourceInfo source)
        {
            var video = source.MediaStreams?
                .Where(stream => stream.Type == MediaStreamType.Video)
                .OrderByDescending(stream => (long)stream.Width.GetValueOrDefault() * stream.Height.GetValueOrDefault())
                .FirstOrDefault();
            var pixels = video == null
                ? 0L
                : (long)video.Width.GetValueOrDefault() * video.Height.GetValueOrDefault();
            var bitrate = Math.Max(0, source.Bitrate.GetValueOrDefault());
            return pixels * 100000L + bitrate;
        }

        private static void EnsureLoaded()
        {
            if (Volatile.Read(ref _options) != null) return;
            lock (Sync)
            {
                if (_options != null) return;

                var root = Plugin.Instance?.ApplicationPaths?.CachePath;
                if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
                _path = Path.Combine(root, "strmassistant-custom", "multiversion-runtime.conf");
                _options = Load(_path);
            }
        }

        private static MultiVersionRuntimeOptions Load(string path)
        {
            var result = new MultiVersionRuntimeOptions();
            try
            {
                if (!File.Exists(path)) return result;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var index = raw.IndexOf('=');
                    if (index <= 0) continue;
                    var key = raw.Substring(0, index).Trim();
                    var value = raw.Substring(index + 1);
                    switch (key)
                    {
                        case "Enabled": result.Enabled = ParseBool(value, false); break;
                        case "RenameSources": result.RenameSources = ParseBool(value, true); break;
                        case "SortHighestQualityFirst": result.SortHighestQualityFirst = ParseBool(value, false); break;
                        case "IncludeFileName": result.IncludeFileName = ParseBool(value, true); break;
                        case "IncludeContainer": result.IncludeContainer = ParseBool(value, false); break;
                        case "Separator": result.Separator = Unescape(value); break;
                        case "IsolateUserDataPerVersion": result.IsolateUserDataPerVersion = ParseBool(value, false); break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("MultiVersion settings load failed: " + ex.Message);
            }
            return Sanitize(result);
        }

        private static MultiVersionRuntimeOptions Sanitize(MultiVersionRuntimeOptions value)
        {
            return new MultiVersionRuntimeOptions
            {
                Enabled = value.Enabled,
                RenameSources = value.RenameSources,
                SortHighestQualityFirst = value.SortHighestQualityFirst,
                IncludeFileName = value.IncludeFileName,
                IncludeContainer = value.IncludeContainer,
                Separator = string.IsNullOrEmpty(value.Separator) ? " · " : value.Separator.Substring(0, Math.Min(value.Separator.Length, 12)),
                IsolateUserDataPerVersion = value.IsolateUserDataPerVersion
            };
        }

        private static MultiVersionRuntimeOptions Clone(MultiVersionRuntimeOptions value)
        {
            return Sanitize(value ?? new MultiVersionRuntimeOptions());
        }

        private static bool ParseBool(string value, bool fallback)
        {
            return bool.TryParse(value?.Trim(), out var parsed) ? parsed : fallback;
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static string Unescape(string value)
        {
            return (value ?? string.Empty).Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\\\", "\\");
        }
    }
}
