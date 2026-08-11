using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.MediaEnhance;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class MediaInfoReliabilityShadowUpdateStatus
    {
        public int UpdateItemsTargetsPatched { get; set; }
        public long CompleteStrmItemsObserved { get; set; }
        public long CompleteStrmItemsQueued { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class MediaInfoReliabilityShadowUpdateState
    {
        public static MediaInfoReliabilityShadowUpdateStatus Status { get; internal set; } =
            new MediaInfoReliabilityShadowUpdateStatus();
    }

    public sealed class MediaInfoReliabilityShadowUpdateEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.mediainfo-shadow-update";
        private Harmony _harmony;

        public void Run()
        {
            var status = new MediaInfoReliabilityShadowUpdateStatus();
            MediaInfoReliabilityShadowUpdateState.Status = status;
            try
            {
                var manager = Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>();
                if (manager == null)
                {
                    status.Error = "ILibraryManager is unavailable.";
                    return;
                }

                var targets = manager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method => string.Equals(method.Name, "UpdateItems", StringComparison.Ordinal) &&
                                     method.GetParameters().Any(parameter =>
                                         typeof(IEnumerable).IsAssignableFrom(parameter.ParameterType)))
                    .Distinct()
                    .ToArray();

                _harmony = new Harmony(HarmonyId);
                var postfix = new HarmonyMethod(typeof(MediaInfoReliabilityShadowUpdatePatches).GetMethod(
                    nameof(MediaInfoReliabilityShadowUpdatePatches.Postfix), BindingFlags.Public | BindingFlags.Static));
                foreach (var target in targets)
                {
                    try
                    {
                        _harmony.Patch(target, postfix: postfix);
                        status.UpdateItemsTargetsPatched++;
                    }
                    catch (Exception ex)
                    {
                        status.LastError = ex.Message;
                    }
                }

                if (status.UpdateItemsTargetsPatched == 0)
                    status.Error = "No runtime UpdateItems method with an enumerable item parameter was found.";
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }
    }

    public static class MediaInfoReliabilityShadowUpdatePatches
    {
        public static void Postfix(object[] __args)
        {
            if (__args == null) return;
            foreach (var arg in __args)
            {
                if (!(arg is IEnumerable enumerable) || arg is string) continue;
                try
                {
                    foreach (var value in enumerable)
                    {
                        if (!(value is BaseItem item)) continue;
                        if (!MediaInfoReliabilityShadowStore.AppliesTo(item) ||
                            !MediaInfoIntegrityService.IsCoreMediaInfoComplete(item)) continue;

                        MediaInfoReliabilityShadowUpdateState.Status.CompleteStrmItemsObserved++;
                        MediaInfoReliabilityShadowPatches.QueueCapture(item.InternalId);
                        MediaInfoReliabilityShadowUpdateState.Status.CompleteStrmItemsQueued++;
                    }
                }
                catch (Exception ex)
                {
                    MediaInfoReliabilityShadowUpdateState.Status.LastError = ex.GetBaseException().Message;
                }
                break;
            }
        }
    }
}
