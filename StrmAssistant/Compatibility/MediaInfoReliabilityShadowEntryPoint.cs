using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.MediaEnhance;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class MediaInfoReliabilityShadowRuntimeStatus
    {
        public int SaveMediaStreamsTargetsPatched { get; set; }
        public int MediaSourceReadTargetsPatched { get; set; }
        public long DeferredCapturesScheduled { get; set; }
        public long PreReadShadowRestores { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class MediaInfoReliabilityShadowRuntimeState
    {
        public static MediaInfoReliabilityShadowRuntimeStatus Status { get; internal set; } =
            new MediaInfoReliabilityShadowRuntimeStatus();
    }

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
                _harmony = new Harmony(HarmonyId);
                var repository = Plugin.Instance?.ApplicationHost?.Resolve<IItemRepository>();
                if (repository != null)
                {
                    var saveTargets = repository.GetType()
                        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(method => string.Equals(method.Name, "SaveMediaStreams", StringComparison.Ordinal) &&
                                         method.GetParameters().Length >= 2)
                        .Distinct()
                        .ToArray();
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
                }

                var mediaSourceManager = Plugin.Instance?.ApplicationHost?.Resolve<IMediaSourceManager>();
                if (mediaSourceManager != null)
                {
                    var readTargets = mediaSourceManager.GetType()
                        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(method =>
                            (string.Equals(method.Name, "GetPlaybackMediaSources", StringComparison.Ordinal) ||
                             string.Equals(method.Name, "GetPlayackMediaSources", StringComparison.Ordinal) ||
                             string.Equals(method.Name, "GetStaticMediaSources", StringComparison.Ordinal)) &&
                            method.GetParameters().Any(parameter => typeof(BaseItem).IsAssignableFrom(parameter.ParameterType)))
                        .Distinct()
                        .ToArray();
                    var prefix = new HarmonyMethod(typeof(MediaInfoReliabilityShadowPatches).GetMethod(
                        nameof(MediaInfoReliabilityShadowPatches.MediaSourceReadPrefix),
                        BindingFlags.Public | BindingFlags.Static)) { priority = Priority.Low };
                    foreach (var target in readTargets)
                    {
                        try
                        {
                            _harmony.Patch(target, prefix: prefix);
                            status.MediaSourceReadTargetsPatched++;
                        }
                        catch (Exception ex)
                        {
                            status.LastError = ex.Message;
                        }
                    }
                }

                if (status.SaveMediaStreamsTargetsPatched == 0 && status.MediaSourceReadTargetsPatched == 0)
                    status.Error = "No repository/media-source target could be patched for the STRM reliability shadow.";
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
        private static readonly AsyncLocal<int> HydrationDepth = new AsyncLocal<int>();

        public static void SaveMediaStreamsPostfix(object[] __args)
        {
            if (HydrationDepth.Value > 0 || __args == null) return;
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

        [HarmonyPriority(Priority.Low)]
        public static void MediaSourceReadPrefix(object[] __args)
        {
            if (HydrationDepth.Value > 0 || __args == null) return;
            var itemIndex = -1;
            BaseItem item = null;
            for (var i = 0; i < __args.Length; i++)
            {
                if (!(__args[i] is BaseItem candidate)) continue;
                item = candidate;
                itemIndex = i;
                break;
            }
            if (!MediaInfoReliabilityShadowStore.AppliesTo(item) ||
                MediaInfoIntegrityService.IsCoreMediaInfoComplete(item)) return;
            if (!MediaInfoReliabilityShadowStore.Exists(item)) return;

            try
            {
                HydrationDepth.Value++;
                if (!MediaInfoReliabilityShadowStore.Restore(item, "PreRead STRM Shadow")) return;
                MediaInfoReliabilityShadowRuntimeState.Status.PreReadShadowRestores++;
                var fresh = Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>()?.GetItemById(item.InternalId);
                if (fresh != null && itemIndex >= 0) __args[itemIndex] = fresh;
            }
            catch (Exception ex)
            {
                MediaInfoReliabilityShadowRuntimeState.Status.LastError = ex.GetBaseException().Message;
            }
            finally
            {
                HydrationDepth.Value = Math.Max(0, HydrationDepth.Value - 1);
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
