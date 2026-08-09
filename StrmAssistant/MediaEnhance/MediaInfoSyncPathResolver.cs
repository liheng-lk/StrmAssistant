using MediaBrowser.Controller.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StrmAssistant.MediaEnhance
{
    public sealed class MediaInfoSyncPathResolution
    {
        public bool Success { get; set; }
        public bool MappingMatched { get; set; }
        public string LocalRoot { get; set; }
        public string LogicalRoot { get; set; }
        public string RelativeDirectory { get; set; }
        public string SyncKey { get; set; }
        public string JsonPath { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Builds a host-independent key for shared MediaInfo JSON storage.
    /// Mapping syntax is one rule per line: /local/media/root => logical-root
    /// The longest matching local root wins.
    /// </summary>
    public static class MediaInfoSyncPathResolver
    {
        private const string MediaInfoSuffix = "-mediainfo.json";

        public static MediaInfoSyncPathResolution Resolve(BaseItem item, string sharedRoot, string mappings)
        {
            var result = new MediaInfoSyncPathResolution();
            if (item == null)
            {
                result.Error = "Media item is null.";
                return result;
            }

            if (string.IsNullOrWhiteSpace(sharedRoot))
            {
                result.Error = "Shared MediaInfo root is empty.";
                return result;
            }

            if (string.IsNullOrWhiteSpace(item.ContainingFolderPath) ||
                string.IsNullOrWhiteSpace(item.FileNameWithoutExtension))
            {
                result.Error = "The item does not have a usable containing-folder path or filename.";
                return result;
            }

            try
            {
                var containingFolder = NormalizeFullPath(item.ContainingFolderPath);
                var rules = ParseMappings(mappings)
                    .OrderByDescending(r => r.LocalRoot.Length)
                    .ToList();

                var matched = rules.FirstOrDefault(rule => IsWithin(containingFolder, rule.LocalRoot));
                string relativeDirectory;
                string logicalRoot;

                if (matched != null)
                {
                    relativeDirectory = Path.GetRelativePath(matched.LocalRoot, containingFolder);
                    if (string.Equals(relativeDirectory, ".", StringComparison.Ordinal))
                        relativeDirectory = string.Empty;

                    if (!IsSafeRelativePath(relativeDirectory))
                    {
                        result.Error = "Mapped relative path escaped the configured local root.";
                        return result;
                    }

                    result.MappingMatched = true;
                    result.LocalRoot = matched.LocalRoot;
                    result.LogicalRoot = matched.LogicalRoot;
                    logicalRoot = matched.LogicalRoot;
                }
                else
                {
                    var pathRoot = Path.GetPathRoot(containingFolder);
                    if (string.IsNullOrWhiteSpace(pathRoot))
                    {
                        result.Error = "Unable to determine a filesystem root for the item path.";
                        return result;
                    }

                    relativeDirectory = Path.GetRelativePath(pathRoot, containingFolder);
                    if (!IsSafeRelativePath(relativeDirectory))
                    {
                        result.Error = "Fallback relative path is unsafe.";
                        return result;
                    }

                    logicalRoot = string.Empty;
                }

                var fileName = item.FileNameWithoutExtension + MediaInfoSuffix;
                var keyParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(logicalRoot)) keyParts.Add(ToKeyPath(logicalRoot));
                if (!string.IsNullOrWhiteSpace(relativeDirectory)) keyParts.Add(ToKeyPath(relativeDirectory));
                keyParts.Add(fileName);

                var syncKey = string.Join("/", keyParts.Where(p => !string.IsNullOrWhiteSpace(p)));
                if (!IsSafeKey(syncKey))
                {
                    result.Error = "Resolved sync key is unsafe.";
                    return result;
                }

                var jsonPath = Path.Combine(new[] { sharedRoot }
                    .Concat(syncKey.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                    .ToArray());

                result.RelativeDirectory = ToKeyPath(relativeDirectory);
                result.SyncKey = syncKey;
                result.JsonPath = jsonPath;
                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "Unable to resolve MediaInfo sync path: " + ex.Message;
                return result;
            }
        }

        private static IEnumerable<PathMappingRule> ParseMappings(string mappings)
        {
            if (string.IsNullOrWhiteSpace(mappings)) yield break;

            var lines = mappings.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                var separatorIndex = line.IndexOf("=>", StringComparison.Ordinal);
                if (separatorIndex <= 0) continue;

                var local = TrimQuotes(line.Substring(0, separatorIndex).Trim());
                var logical = TrimQuotes(line.Substring(separatorIndex + 2).Trim());
                if (string.IsNullOrWhiteSpace(local) || string.IsNullOrWhiteSpace(logical)) continue;
                if (!IsSafeLogicalRoot(logical)) continue;

                string normalizedLocal;
                try
                {
                    normalizedLocal = NormalizeFullPath(local);
                }
                catch
                {
                    continue;
                }

                yield return new PathMappingRule
                {
                    LocalRoot = normalizedLocal,
                    LogicalRoot = ToKeyPath(logical)
                };
            }
        }

        private static string NormalizeFullPath(string path)
        {
            return Path.GetFullPath(TrimQuotes(path))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool IsWithin(string path, string root)
        {
            if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return true;
            var prefix = root + Path.DirectorySeparatorChar;
            var altPrefix = root + Path.AltDirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(altPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSafeRelativePath(string value)
        {
            if (string.IsNullOrEmpty(value) || string.Equals(value, ".", StringComparison.Ordinal)) return true;
            if (Path.IsPathRooted(value)) return false;
            return value.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .All(segment => segment != "." && segment != "..");
        }

        private static bool IsSafeLogicalRoot(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) return false;
            return value.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .All(segment => segment != "." && segment != "..");
        }

        private static bool IsSafeKey(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   !value.StartsWith("/", StringComparison.Ordinal) &&
                   value.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                       .All(segment => segment != "." && segment != "..");
        }

        private static string ToKeyPath(string value)
        {
            return (value ?? string.Empty).Replace('\\', '/').Trim('/');
        }

        private static string TrimQuotes(string value)
        {
            return (value ?? string.Empty).Trim().Trim('"');
        }

        private sealed class PathMappingRule
        {
            public string LocalRoot { get; set; }
            public string LogicalRoot { get; set; }
        }
    }
}
