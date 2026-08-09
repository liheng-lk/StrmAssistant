using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Dto;
using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class MediaInfoRuntimeFallbackCapabilityStatus
    {
        public bool TargetFound { get; set; }
        public bool Patched { get; set; }
        public bool ReflectionStaticMediaSourceAvailable { get; set; }
        public string Target { get; set; }
        public string Error { get; set; }
    }

    public static class MediaInfoRuntimeFallbackState
    {
        public static MediaInfoRuntimeFallbackCapabilityStatus Status { get; internal set; } =
            new MediaInfoRuntimeFallbackCapabilityStatus();
    }

    /// <summary>
    /// Compatibility guard for Emby builds where IMediaSourceManager.GetStaticMediaSources
    /// changed its private/runtime signature. The original MediaInfoApi path is retained when
    /// its reflected target exists; otherwise the stable BaseItem.GetMediaSources path is used.
    /// </summary>
    public sealed class MediaInfoRuntimeFallbackEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.mediainfo-fallback";
        private Harmony _harmony;

        public void Run()
        {
            var status = new MediaInfoRuntimeFallbackCapabilityStatus();
            MediaInfoRuntimeFallbackState.Status = status;

            try
            {
                var target = typeof(MediaInfoApi).GetMethod(
                    "GetStaticMediaSources",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(BaseItem), typeof(bool) },
                    null);

                status.TargetFound = target != null;
                status.Target = target?.ToString();
                if (target == null)
                {
                    status.Error = "MediaInfoApi.GetStaticMediaSources(BaseItem,bool) was not found.";
                    return;
                }

                var reflectedField = typeof(MediaInfoApi).GetField(
                    "_getStaticMediaSources",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                status.ReflectionStaticMediaSourceAvailable =
                    reflectedField?.GetValue(Plugin.MediaInfoApi) is MethodInfo;

                _harmony = new Harmony(HarmonyId);
                var prefix = typeof(MediaInfoRuntimeFallbackPatches).GetMethod(
                    nameof(MediaInfoRuntimeFallbackPatches.GetStaticMediaSourcesPrefix),
                    BindingFlags.Static | BindingFlags.Public);
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                status.Patched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("MediaInfo runtime fallback unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }
    }

    public static class MediaInfoRuntimeFallbackPatches
    {
        public static bool GetStaticMediaSourcesPrefix(MediaInfoApi __instance, BaseItem item,
            bool enableAlternateMediaSources, ref List<MediaSourceInfo> __result)
        {
            try
            {
                if (__instance == null || item == null)
                {
                    __result = new List<MediaSourceInfo>();
                    return false;
                }

                var reflectedField = typeof(MediaInfoApi).GetField(
                    "_getStaticMediaSources",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (reflectedField?.GetValue(__instance) is MethodInfo)
                    return true;

                var libraryManagerField = typeof(MediaInfoApi).GetField(
                    "_libraryManager",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var libraryManager = libraryManagerField?.GetValue(__instance) as ILibraryManager;
                if (libraryManager == null)
                {
                    Plugin.Instance?.Logger?.Warn("MediaInfo runtime fallback could not resolve ILibraryManager.");
                    __result = new List<MediaSourceInfo>();
                    return false;
                }

                var libraryOptions = libraryManager.GetLibraryOptions(item);
                __result = item.GetMediaSources(enableAlternateMediaSources, false, libraryOptions)
                           ?? new List<MediaSourceInfo>();

                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("MediaInfo runtime fallback used BaseItem.GetMediaSources for {0}", item.Path);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("MediaInfo runtime fallback failed: " + ex.Message);
                __result = new List<MediaSourceInfo>();
                return false;
            }
        }
    }
}
