using MediaBrowser.Controller.Entities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Experience
{
    public sealed class MetadataFieldChange
    {
        public string Field { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
    }

    public sealed class MetadataSnapshot
    {
        public long ItemId { get; set; }
        public Dictionary<string, string> Values { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Captures and compares the 19 public metadata fields documented by the notification
    /// feature. Reflection is deliberate here: it prevents compile-time coupling to fields
    /// whose concrete property types have changed between Emby releases.
    /// </summary>
    public static class MetadataChangeTracker
    {
        public static readonly string[] SupportedFields =
        {
            "Name", "Overview", "OriginalTitle", "Tagline", "OfficialRating", "CustomRating",
            "CriticRating", "CommunityRating", "IndexNumber", "ParentIndexNumber", "PremiereDate",
            "ProductionYear", "EndDate", "RunTimeTicks", "Tags", "Genres", "Studios",
            "ProductionLocations", "ProviderIds"
        };

        private static readonly HashSet<string> SupportedFieldSet =
            new HashSet<string>(SupportedFields, StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<string> ParseTrackedFields(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

            return raw
                .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(SupportedFieldSet.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static MetadataSnapshot Capture(BaseItem item, IEnumerable<string> trackedFields)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            var snapshot = new MetadataSnapshot { ItemId = item.InternalId };
            var type = item.GetType();

            foreach (var field in trackedFields ?? Array.Empty<string>())
            {
                if (!SupportedFieldSet.Contains(field)) continue;

                var property = type.GetProperty(field,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);

                snapshot.Values[field] = property == null
                    ? "<unsupported>"
                    : NormalizeValue(SafeGetValue(property, item));
            }

            return snapshot;
        }

        public static IReadOnlyList<MetadataFieldChange> Compare(MetadataSnapshot before, MetadataSnapshot after)
        {
            if (before == null) throw new ArgumentNullException(nameof(before));
            if (after == null) throw new ArgumentNullException(nameof(after));
            if (before.ItemId != after.ItemId) throw new InvalidOperationException("Metadata snapshots belong to different items.");

            var fields = before.Values.Keys
                .Concat(after.Values.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);

            var changes = new List<MetadataFieldChange>();
            foreach (var field in fields)
            {
                before.Values.TryGetValue(field, out var oldValue);
                after.Values.TryGetValue(field, out var newValue);

                oldValue = oldValue ?? string.Empty;
                newValue = newValue ?? string.Empty;
                if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) continue;

                changes.Add(new MetadataFieldChange
                {
                    Field = field,
                    Before = oldValue,
                    After = newValue
                });
            }

            return changes;
        }

        public static string FormatDescription(IEnumerable<MetadataFieldChange> changes)
        {
            return string.Join(Environment.NewLine,
                (changes ?? Array.Empty<MetadataFieldChange>())
                .Select(change => $"{change.Field}: {change.Before} -> {change.After}"));
        }

        private static object SafeGetValue(PropertyInfo property, object target)
        {
            try
            {
                return property.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeValue(object value)
        {
            if (value == null) return string.Empty;

            if (value is string text) return text.Trim();
            if (value is DateTime dateTime) return dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            if (value is DateTimeOffset dateTimeOffset) return dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            if (value is IFormattable formattable && !(value is IEnumerable))
                return formattable.ToString(null, CultureInfo.InvariantCulture);

            if (value is IDictionary dictionary)
            {
                var pairs = new List<string>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    pairs.Add($"{NormalizeValue(entry.Key)}={NormalizeValue(entry.Value)}");
                }

                return string.Join(";", pairs.OrderBy(pair => pair, StringComparer.Ordinal));
            }

            if (value is IEnumerable enumerable)
            {
                var values = new List<string>();
                foreach (var item in enumerable) values.Add(NormalizeValue(item));
                return string.Join(";", values.OrderBy(item => item, StringComparer.Ordinal));
            }

            return value.ToString();
        }
    }
}
