using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Plugins;
using System;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class LibraryNotificationDescriptionCapabilityStatus
    {
        public bool TargetFound { get; set; }
        public bool Patched { get; set; }
        public string Target { get; set; }
        public string Error { get; set; }
    }

    public static class LibraryNotificationDescriptionModState
    {
        public static LibraryNotificationDescriptionCapabilityStatus Status { get; internal set; } =
            new LibraryNotificationDescriptionCapabilityStatus();
    }

    /// <summary>
    /// Adds SxxExx information to the first line of Emby's native NewLibraryContent notification.
    /// The patch is deliberately DTO/text-only: it does not alter recipients, notification
    /// configuration, item visibility, library events or the notification delivery pipeline.
    /// </summary>
    public sealed class LibraryNotificationDescriptionRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.library-notification-description";
        private Harmony _harmony;

        public void Run()
        {
            var status = new LibraryNotificationDescriptionCapabilityStatus();
            LibraryNotificationDescriptionModState.Status = status;
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "Emby.Notifications", StringComparison.OrdinalIgnoreCase));
                var managerType = assembly?.GetType("Emby.Notifications.NotificationManager", false);
                var target = managerType?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(method =>
                    {
                        if (!string.Equals(method.Name, "SendNotification", StringComparison.Ordinal)) return false;
                        var parameters = method.GetParameters();
                        return parameters.Length == 3 &&
                               typeof(NotificationRequest).IsAssignableFrom(parameters[0].ParameterType) &&
                               typeof(BaseItem).IsAssignableFrom(parameters[1].ParameterType);
                    });

                status.TargetFound = target != null;
                status.Target = target?.ToString();
                if (target == null) return;

                _harmony = new Harmony(HarmonyId);
                var prefix = typeof(LibraryNotificationDescriptionPatches).GetMethod(
                    nameof(LibraryNotificationDescriptionPatches.SendNotificationPrefix),
                    BindingFlags.Public | BindingFlags.Static);
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                status.Patched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Library notification description patch failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
        }
    }

    public static class LibraryNotificationDescriptionPatches
    {
        public static void SendNotificationPrefix(object[] __args)
        {
            try
            {
                var plugin = Plugin.Instance;
                var options = plugin?.GetPluginOptions()?.ExperienceEnhanceOptions;
                if (options?.EnableNotificationEnhance != true || __args == null) return;

                var request = __args.OfType<NotificationRequest>().FirstOrDefault();
                var relatedItem = __args.OfType<BaseItem>().FirstOrDefault();
                if (request == null || !(relatedItem is Episode episode)) return;
                if (!string.Equals(request.NotificationType, "NewLibraryContent", StringComparison.OrdinalIgnoreCase)) return;

                var season = episode.ParentIndexNumber;
                var number = episode.IndexNumber;
                if (!season.HasValue && !number.HasValue) return;

                var prefix = BuildEpisodeLabel(season, number);
                if (string.IsNullOrWhiteSpace(prefix)) return;

                var description = request.Description ?? string.Empty;
                var firstLine = description.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).FirstOrDefault() ?? string.Empty;
                if (firstLine.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0) return;

                request.Description = string.IsNullOrWhiteSpace(description)
                    ? prefix
                    : prefix + Environment.NewLine + description;
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Library notification season/episode enhancement skipped: " + ex.Message);
            }
        }

        private static string BuildEpisodeLabel(int? season, int? episode)
        {
            if (season.HasValue && episode.HasValue) return $"S{season.Value:00}E{episode.Value:00}";
            if (season.HasValue) return $"S{season.Value:00}";
            if (episode.HasValue) return $"E{episode.Value:00}";
            return null;
        }
    }
}
