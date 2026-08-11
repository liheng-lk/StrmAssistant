using MediaBrowser.Controller.Entities;
using StrmAssistant.Options;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.MediaEnhance
{
    /// <summary>
    /// Shared MediaInfo / fingerprint / BIF exclusion policy.
    ///
    /// The filter intentionally depends only on stable BaseItem members plus reflection for
    /// optional metadata fields, so the same binary can be compiled against Emby 4.8-4.10.
    /// </summary>
    public static class MediaExtractionFilter
    {
        public static bool ShouldSkip(BaseItem item, MediaInfoExtractOptions options, out string reason)
        {
            reason = null;
            if (item == null || options == null || !options.EnableExtractionBlacklist) return false;

            var blockedTags = ParseTokens(options.ExtractionBlacklistTags);
            if (blockedTags.Count > 0)
            {
                var itemTags = ReadStringValues(item, "Tags");
                var matchedTag = itemTags.FirstOrDefault(tag => blockedTags.Contains(tag));
                if (!string.IsNullOrEmpty(matchedTag))
                {
                    reason = "tag:" + matchedTag;
                    return true;
                }
            }

            var keywords = ParseTokens(options.ExtractionBlacklistKeywords);
            if (keywords.Count == 0) return false;

            var searchable = new[]
                {
                    item.Name,
                    item.Path,
                    ReadStringValue(item, "OriginalTitle")
                }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            foreach (var keyword in keywords)
            {
                if (searchable.Any(value => value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    reason = "keyword:" + keyword;
                    return true;
                }
            }

            return false;
        }

        public static bool ShouldSkip(BaseItem item, out string reason)
        {
            var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
            return ShouldSkip(item, options, out reason);
        }

        public static IEnumerable<T> Apply<T>(IEnumerable<T> items) where T : BaseItem
        {
            if (items == null) yield break;

            foreach (var item in items)
            {
                if (!ShouldSkip(item, out _)) yield return item;
            }
        }

        private static HashSet<string> ParseTokens(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return new HashSet<string>(
                raw.Split(new[] { ',', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Trim())
                    .Where(value => value.Length > 0),
                StringComparer.OrdinalIgnoreCase);
        }

        private static string ReadStringValue(object target, string propertyName)
        {
            try
            {
                return target?.GetType()
                    .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
                    ?.GetValue(target)?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<string> ReadStringValues(object target, string propertyName)
        {
            object value;
            try
            {
                value = target?.GetType()
                    .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
                    ?.GetValue(target);
            }
            catch
            {
                yield break;
            }

            if (value is string text)
            {
                if (!string.IsNullOrWhiteSpace(text)) yield return text.Trim();
                yield break;
            }

            if (!(value is IEnumerable enumerable)) yield break;

            foreach (var entry in enumerable)
            {
                var textValue = entry?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(textValue)) yield return textValue;
            }
        }
    }
}
