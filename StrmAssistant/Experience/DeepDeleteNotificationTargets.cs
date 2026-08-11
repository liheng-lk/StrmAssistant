using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StrmAssistant.Experience
{
    /// <summary>
    /// Captures the external-operation targets that are placed in the historical
    /// deep.delete notification "Mount Paths" field. The notification contract is
    /// provider-agnostic: a STRM may point at OpenList, another HTTP service, a signed
    /// CDN URL, WebDAV, a local path, or any other target understood by the external
    /// webhook consumer.
    ///
    /// Capture MUST happen before Emby removes the .strm source file.
    /// </summary>
    internal static class DeepDeleteNotificationTargets
    {
        internal const int MaxTargetCount = 16;
        internal const int MaxTargetLength = 32768;

        internal static HashSet<string> Capture(string sourcePath, IEnumerable<string> additionalTargets = null)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddRange(result, additionalTargets);

            if (string.IsNullOrWhiteSpace(sourcePath) ||
                !string.Equals(Path.GetExtension(sourcePath), ".strm", StringComparison.OrdinalIgnoreCase))
                return result;

            try
            {
                // The established STRM contract uses the first non-empty line as the media target.
                // Keep it verbatim except for surrounding whitespace: external automation may need
                // query parameters/tokens in order to map the target to the real cloud object.
                var target = File.ReadLines(sourcePath)
                    .Select(line => line?.Trim())
                    .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

                Add(result, target);
            }
            catch
            {
                // Notification capture is best-effort here. The caller reports an empty target set
                // instead of inventing a path when the STRM cannot be read.
            }

            return result;
        }

        internal static bool ContainsHttpTarget(IEnumerable<string> targets)
        {
            return (targets ?? Enumerable.Empty<string>()).Any(target =>
                Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
        }

        private static void AddRange(HashSet<string> result, IEnumerable<string> values)
        {
            if (values == null) return;
            foreach (var value in values)
            {
                if (result.Count >= MaxTargetCount) break;
                Add(result, value);
            }
        }

        private static void Add(HashSet<string> result, string value)
        {
            if (result == null || result.Count >= MaxTargetCount || string.IsNullOrWhiteSpace(value)) return;
            var trimmed = value.Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxTargetLength) return;
            result.Add(trimmed);
        }
    }
}
