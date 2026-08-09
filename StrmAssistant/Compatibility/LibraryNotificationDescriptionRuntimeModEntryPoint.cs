using Emby.Notifications;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using StrmAssistant.Common;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

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
    /// Enhances Emby's native NewLibraryContent notification without changing recipients or
    /// notification preferences. Episode notifications get SxxExx on the first description line.
    /// In Catch-up mode, a notification for an item that is actually queued for screenshot /
    /// embedded-image work can be deferred until the image work finishes (bounded timeout).
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
                        if (method.ReturnType != typeof(Task)) return false;
                        var parameters = method.GetParameters();
                        return parameters.Length == 3 &&
                               typeof(NotificationRequest).IsAssignableFrom(parameters[0].ParameterType) &&
                               typeof(BaseItem).IsAssignableFrom(parameters[1].ParameterType) &&
                               parameters[2].ParameterType == typeof(CancellationToken);
                    });

                status.TargetFound = target != null;
                status.Target = target?.ToString();
                if (target == null) return;

                LibraryNotificationDescriptionPatches.TargetMethod = target;
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
                Plugin.Instance?.Logger?.Warn("Library notification enhancement patch failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
            LibraryNotificationDescriptionPatches.TargetMethod = null;
        }
    }

    public static class LibraryNotificationDescriptionPatches
    {
        private static readonly AsyncLocal<int> BypassDepth = new AsyncLocal<int>();
        public static MethodInfo TargetMethod { get; set; }

        public static bool SendNotificationPrefix(object __instance, object[] __args, ref Task __result)
        {
            if (BypassDepth.Value > 0) return true;

            try
            {
                var plugin = Plugin.Instance;
                var options = plugin?.GetPluginOptions()?.ExperienceEnhanceOptions;
                if (options?.EnableNotificationEnhance != true || __args == null) return true;

                var request = __args.OfType<NotificationRequest>().FirstOrDefault();
                var relatedItem = __args.OfType<BaseItem>().FirstOrDefault();
                if (request == null || relatedItem == null ||
                    !string.Equals(request.NotificationType, "NewLibraryContent", StringComparison.OrdinalIgnoreCase))
                    return true;

                EnhanceEpisodeDescription(request, relatedItem as Episode);

                if (ShouldBeginImageDelay(relatedItem))
                {
                    __result = DelayAndSendAsync(__instance, __args, relatedItem.InternalId);
                    return false;
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Library notification enhancement skipped: " + ex.Message);
            }

            return true;
        }

        private static void EnhanceEpisodeDescription(NotificationRequest request, Episode episode)
        {
            if (request == null || episode == null) return;
            var prefix = BuildEpisodeLabel(episode.ParentIndexNumber, episode.IndexNumber);
            if (string.IsNullOrWhiteSpace(prefix)) return;

            var description = request.Description ?? string.Empty;
            var firstLine = description.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).FirstOrDefault() ?? string.Empty;
            if (firstLine.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0) return;
            request.Description = string.IsNullOrWhiteSpace(description)
                ? prefix
                : prefix + Environment.NewLine + description;
        }

        private static bool ShouldBeginImageDelay(BaseItem item)
        {
            try
            {
                if (item == null || item.HasImage(ImageType.Primary)) return false;
                var plugin = Plugin.Instance;
                var pluginOptions = plugin?.GetPluginOptions();
                var general = pluginOptions?.GeneralOptions;
                var media = pluginOptions?.MediaInfoExtractOptions;
                if (general?.CatchupMode != true || media?.EnableImageCapture != true) return false;
                if (string.IsNullOrWhiteSpace(general.CatchupTaskScope) ||
                    general.CatchupTaskScope.IndexOf("MediaInfo", StringComparison.OrdinalIgnoreCase) < 0) return false;
                if (!QueueManager.MediaInfoExtractItemQueue.Any(value => value?.InternalId == item.InternalId)) return false;
                return IsImageWorkStillNeeded(item);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsImageWorkStillNeeded(BaseItem item)
        {
            try
            {
                if (item == null || item.HasImage(ImageType.Primary)) return false;
                var plugin = Plugin.Instance;
                var media = plugin?.GetPluginOptions()?.MediaInfoExtractOptions;
                if (plugin?.LibraryApi == null || media?.EnableImageCapture != true) return false;

                var libraryManager = plugin.ApplicationHost.Resolve<ILibraryManager>();
                var options = libraryManager?.GetLibraryOptions(item);
                if (options == null || !plugin.LibraryApi.ImageCaptureEnabled(item, options)) return false;
                return plugin.LibraryApi.IsExtractNeeded(item, true);
            }
            catch
            {
                return false;
            }
        }

        private static async Task DelayAndSendAsync(object instance, object[] args, long itemId)
        {
            const int pollSeconds = 2;
            const int maxWaitSeconds = 120;
            var cancellationToken = args?.OfType<CancellationToken>().FirstOrDefault() ?? CancellationToken.None;
            var waited = 0;

            try
            {
                while (waited < maxWaitSeconds && !cancellationToken.IsCancellationRequested)
                {
                    BaseItem current = null;
                    try
                    {
                        var manager = Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>();
                        current = manager?.GetItemById(itemId);
                    }
                    catch { }

                    if (current == null || !IsImageWorkStillNeeded(current)) break;
                    await Task.Delay(TimeSpan.FromSeconds(pollSeconds), cancellationToken).ConfigureAwait(false);
                    waited += pollSeconds;
                }

                if (waited > 0 && Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Delayed NewLibraryContent notification {0}s for item {1}.", waited, itemId);

                var target = TargetMethod;
                if (target == null || instance == null) return;
                BypassDepth.Value++;
                try
                {
                    if (target.Invoke(instance, args) is Task task)
                        await task.ConfigureAwait(false);
                }
                finally
                {
                    BypassDepth.Value--;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Delayed NewLibraryContent notification failed for item {0}: {1}",
                    itemId, ex.GetBaseException().Message);
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
