using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class RemovedEpisodeNotificationCapabilityStatus
    {
        public bool ItemRemovedSubscribed { get; set; }
        public bool ActivityHandlerTargetFound { get; set; }
        public bool Patched { get; set; }
        public string Target { get; set; }
        public string LastRemovedItemId { get; set; }
        public string LastActivityItemId { get; set; }
        public string LastActivityType { get; set; }
        public string LastAppliedLabel { get; set; }
        public int CachedIdentityCount { get; set; }
        public string Error { get; set; }
    }

    public static class RemovedEpisodeNotificationModState
    {
        public static RemovedEpisodeNotificationCapabilityStatus Status { get; internal set; } =
            new RemovedEpisodeNotificationCapabilityStatus();
    }

    /// <summary>
    /// Bridges Emby's native remove activity notification without title matching. ItemRemoved keeps
    /// a short-lived Episode identity cache; the Notifications ActivityLog handler is patched before
    /// it constructs NotificationRequest, and ActivityLogEntry.ItemId is matched against that cache.
    /// Only delete/remove activity types are modified.
    /// </summary>
    public sealed class RemovedEpisodeNotificationRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.removed-episode-notification";
        private readonly ILibraryManager _libraryManager;
        private Harmony _harmony;

        public RemovedEpisodeNotificationRuntimeModEntryPoint(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public void Run()
        {
            var status = new RemovedEpisodeNotificationCapabilityStatus();
            RemovedEpisodeNotificationModState.Status = status;
            try
            {
                _libraryManager.ItemRemoved += LibraryManagerOnItemRemoved;
                status.ItemRemovedSubscribed = true;

                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "Emby.Notifications", StringComparison.OrdinalIgnoreCase));
                var notificationsType = assembly?.GetType("Emby.Notifications.Notifications", false);
                var target = notificationsType?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(method =>
                        method.Name.IndexOf("activity", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        method.Name.IndexOf("EntryCreated", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        method.GetParameters().Length == 2);

                status.ActivityHandlerTargetFound = target != null;
                status.Target = target?.ToString();
                if (target == null) return;

                _harmony = new Harmony(HarmonyId);
                var prefix = typeof(RemovedEpisodeNotificationPatches).GetMethod(
                    nameof(RemovedEpisodeNotificationPatches.ActivityEntryCreatedPrefix),
                    BindingFlags.Public | BindingFlags.Static);
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                status.Patched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Removed-episode notification enhancement unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _libraryManager.ItemRemoved -= LibraryManagerOnItemRemoved; }
            catch { }
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
            RemovedEpisodeNotificationPatches.Clear();
        }

        private static void LibraryManagerOnItemRemoved(object sender, ItemChangeEventArgs e)
        {
            try
            {
                var options = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
                if (options?.EnableNotificationEnhance != true || !(e?.Item is Episode episode)) return;
                var label = BuildEpisodeLabel(episode.ParentIndexNumber, episode.IndexNumber);
                if (string.IsNullOrWhiteSpace(label)) return;

                var identities = GetItemIdentityStrings(episode).ToList();
                if (identities.Count == 0) return;
                RemovedEpisodeNotificationPatches.Remember(identities, label);
                var status = RemovedEpisodeNotificationModState.Status;
                status.LastRemovedItemId = identities[0];
                status.CachedIdentityCount = RemovedEpisodeNotificationPatches.Count;
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Unable to cache removed Episode notification identity: " + ex.Message);
            }
        }

        private static IEnumerable<string> GetItemIdentityStrings(BaseItem item)
        {
            var values = new List<string>();
            if (item == null) return values;
            values.Add(item.InternalId.ToString(System.Globalization.CultureInfo.InvariantCulture));

            try
            {
                var property = item.GetType().GetProperty("Id",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var value = property?.CanRead == true ? property.GetValue(item) : null;
                if (value != null)
                {
                    values.Add(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                    if (value is Guid guid)
                    {
                        values.Add(guid.ToString("N"));
                        values.Add(guid.ToString("D"));
                    }
                }
            }
            catch { }

            return values.Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(NormalizeIdentity)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        internal static string NormalizeIdentity(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return value.Trim().Trim('{', '}').Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string BuildEpisodeLabel(int? season, int? episode)
        {
            if (season.HasValue && episode.HasValue) return $"S{season.Value:00}E{episode.Value:00}";
            if (season.HasValue) return $"S{season.Value:00}";
            if (episode.HasValue) return $"E{episode.Value:00}";
            return null;
        }
    }

    public static class RemovedEpisodeNotificationPatches
    {
        private sealed class CachedEpisodeIdentity
        {
            public string Token { get; set; }
            public string Label { get; set; }
            public DateTime ExpiresUtc { get; set; }
        }

        private static readonly ConcurrentDictionary<string, CachedEpisodeIdentity> Cache =
            new ConcurrentDictionary<string, CachedEpisodeIdentity>(StringComparer.OrdinalIgnoreCase);

        public static int Count => Cache.Count;

        public static void Remember(IEnumerable<string> identities, string label)
        {
            Prune();
            var token = Guid.NewGuid().ToString("N");
            var entry = new CachedEpisodeIdentity
            {
                Token = token,
                Label = label,
                ExpiresUtc = DateTime.UtcNow.AddMinutes(2)
            };
            foreach (var identity in identities ?? Array.Empty<string>())
            {
                var normalized = RemovedEpisodeNotificationRuntimeModEntryPoint.NormalizeIdentity(identity);
                if (!string.IsNullOrWhiteSpace(normalized)) Cache[normalized] = entry;
            }
        }

        public static void Clear() => Cache.Clear();

        public static void ActivityEntryCreatedPrefix(object[] __args)
        {
            try
            {
                var options = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
                if (options?.EnableNotificationEnhance != true || __args == null) return;

                var activity = FindActivityEntry(__args);
                if (activity == null) return;
                var activityType = ReadString(activity, "Type") ?? ReadString(activity, "EventId");
                var itemId = ReadString(activity, "ItemId");

                var status = RemovedEpisodeNotificationModState.Status;
                status.LastActivityType = activityType;
                status.LastActivityItemId = itemId;
                status.CachedIdentityCount = Cache.Count;

                if (!LooksLikeRemovalActivity(activityType) || string.IsNullOrWhiteSpace(itemId)) return;
                if (!TryConsume(itemId, out var label)) return;

                var overviewProperty = activity.GetType().GetProperty("Overview",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (overviewProperty?.CanWrite != true) return;
                var overview = Convert.ToString(overviewProperty.GetValue(activity)) ?? string.Empty;
                var firstLine = overview.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                    .FirstOrDefault() ?? string.Empty;
                if (firstLine.IndexOf(label, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    overviewProperty.SetValue(activity,
                        string.IsNullOrWhiteSpace(overview) ? label : label + Environment.NewLine + overview);
                }
                status.LastAppliedLabel = label;
                status.CachedIdentityCount = Cache.Count;
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Removed Episode notification season/episode enhancement skipped: " + ex.Message);
            }
        }

        private static object FindActivityEntry(object[] args)
        {
            foreach (var arg in args)
            {
                if (arg == null) continue;
                if (HasActivityShape(arg)) return arg;
                try
                {
                    var property = arg.GetType().GetProperty("Argument",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var nested = property?.CanRead == true ? property.GetValue(arg) : null;
                    if (nested != null && HasActivityShape(nested)) return nested;
                }
                catch { }
            }
            return null;
        }

        private static bool HasActivityShape(object value)
        {
            if (value == null) return false;
            var type = value.GetType();
            return type.GetProperty("ItemId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null &&
                   type.GetProperty("Overview", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;
        }

        private static string ReadString(object value, string propertyName)
        {
            try
            {
                var property = value?.GetType().GetProperty(propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return property?.CanRead == true
                    ? Convert.ToString(property.GetValue(value), System.Globalization.CultureInfo.InvariantCulture)
                    : null;
            }
            catch { return null; }
        }

        private static bool LooksLikeRemovalActivity(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return false;
            return type.IndexOf("delete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("remove", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryConsume(string itemId, out string label)
        {
            label = null;
            Prune();
            var normalized = RemovedEpisodeNotificationRuntimeModEntryPoint.NormalizeIdentity(itemId);
            if (string.IsNullOrWhiteSpace(normalized) || !Cache.TryGetValue(normalized, out var entry) || entry == null)
                return false;
            label = entry.Label;
            foreach (var pair in Cache.Where(v => string.Equals(v.Value?.Token, entry.Token, StringComparison.Ordinal)).ToList())
                Cache.TryRemove(pair.Key, out _);
            return !string.IsNullOrWhiteSpace(label);
        }

        private static void Prune()
        {
            var now = DateTime.UtcNow;
            foreach (var pair in Cache.Where(v => v.Value == null || v.Value.ExpiresUtc <= now).ToList())
                Cache.TryRemove(pair.Key, out _);
        }
    }
}
