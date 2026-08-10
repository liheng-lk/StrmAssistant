using HarmonyLib;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.MediaEnhance;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class MediaInfoReliabilityShadowRuntimeStatus
    {
        public int SaveMediaStreamsTargetsPatched { get; set; }
        public long DeferredCapturesScheduled { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class MediaInfoReliabilityShadowRuntimeState
    {
        public static MediaInfoReliabilityShadowRuntimeStatus Status { get; internal set; } =
            new MediaInfoReliabilityShadowRuntimeStatus();
    }

    /// <summary>
    /// Captures a STRM reliability shadow after MediaStreams are persisted. Playback pre-read restore
    /// is intentionally owned only by MediaInfoPreReadAndExternalTrackGuardEntryPoint so a playback
    /// request crosses one recovery prefix rather than two competing Harmony prefixes.
    /// </summary>
    public sealed class MediaInfoReliabilityShadowEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.mediainfo-shadow";
        private Harmony _harmony;

        public void Run()
        {
            var status = new MediaInfoReliabilityShadowRuntimeStatus();
            MediaInfoReliabilityShadowRuntimeState.Status = status;
            try
            {
                var repository = Plugin.Instance?.ApplicationHost?.Resolve<IItemRepository>();
                if (repository == null)
                {
                    status.Error = "IItemRepository is unavailable.";
                    return;
                }

                var saveTargets = repository.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method => string.Equals(method.Name, "SaveMediaStreams", StringComparison.Ordinal) &&
                                     method.GetParameters().Length >= 2)
                    .Distinct()
                    .ToArray();
                if (saveTargets.Length == 0)
                {
                    status.Error = "No runtime SaveMediaStreams target was found for the STRM reliability shadow.";
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                var postfix = new HarmonyMethod(typeof(MediaInfoReliabilityShadowPatches).GetMethod(
                    nameof(MediaInfoReliabilityShadowPatches.SaveMediaStreamsPostfix),
                    BindingFlags.Public | BindingFlags.Static));
                foreach (var target in saveTargets)
                {
                    try
                    {
                        _harmony.Patch(target, postfix: postfix);
                        status.SaveMediaStreamsTargetsPatched++;
                    }
                    catch (Exception ex)
                    {
                        status.LastError = ex.Message;
                    }
                }

                if (status.SaveMediaStreamsTargetsPatched == 0)
                    status.Error = "SaveMediaStreams targets were discovered but none could be patched.";
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Error("MediaInfo reliability shadow patch failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }
    }

    public static class MediaInfoReliabilityShadowPatches
    {
        public static void SaveMediaStreamsPostfix(object[] __args)
        {
            if (__args == null) return;
            var itemId = FindItemId(__args);
            if (itemId <= 0) return;

            try
            {
                var libraryManager = Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>();
                var item = libraryManager?.GetItemById(itemId);
                if (!MediaInfoReliabilityShadowStore.AppliesTo(item)) return;

                MediaInfoReliabilityShadowRuntimeState.Status.DeferredCapturesScheduled++;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // SaveMediaStreams often precedes the Item runtime/container update. A short
                        // delay lets the same extraction transaction finish before the snapshot check.
                        await Task.Delay(750).ConfigureAwait(false);
                        var fresh = libraryManager.GetItemById(itemId);
                        MediaInfoReliabilityShadowStore.Capture(fresh);
                    }
                    catch (Exception ex)
                    {
                        MediaInfoReliabilityShadowRuntimeState.Status.LastError = ex.GetBaseException().Message;
                    }
                });
            }
            catch (Exception ex)
            {
                MediaInfoReliabilityShadowRuntimeState.Status.LastError = ex.GetBaseException().Message;
            }
        }

        private static long FindItemId(object[] args)
        {
            if (args == null) return 0;
            foreach (var arg in args)
            {
                if (arg is long longValue && longValue > 0) return longValue;
                if (arg is int intValue && intValue > 0) return intValue;
            }
            return 0;
        }
    }
}
