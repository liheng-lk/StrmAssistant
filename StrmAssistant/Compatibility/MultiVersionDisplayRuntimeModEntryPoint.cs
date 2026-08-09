using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Dto;
using StrmAssistant.Experience;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class MultiVersionDisplayCapabilityStatus
    {
        public bool TargetFound { get; set; }
        public bool Patched { get; set; }
        public List<string> Targets { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    public static class MultiVersionDisplayModState
    {
        public static MultiVersionDisplayCapabilityStatus Status { get; internal set; } =
            new MultiVersionDisplayCapabilityStatus();
    }

    /// <summary>
    /// Runtime-only media-source naming/sorting. No merge state, media path, item metadata or user data is written.
    /// </summary>
    public sealed class MultiVersionDisplayRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.multiversion-display";
        private Harmony _harmony;

        public void Run()
        {
            var status = new MultiVersionDisplayCapabilityStatus();
            MultiVersionDisplayModState.Status = status;

            try
            {
                var targets = typeof(Video).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method => string.Equals(method.Name, "GetMediaSources", StringComparison.Ordinal) &&
                                     typeof(IEnumerable<MediaSourceInfo>).IsAssignableFrom(method.ReturnType))
                    .ToArray();

                // Most Emby builds return List<MediaSourceInfo>, which does not satisfy the interface
                // assignability test above in every reflection/runtime combination. Include exact generic list.
                if (targets.Length == 0)
                {
                    targets = typeof(Video).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(method => string.Equals(method.Name, "GetMediaSources", StringComparison.Ordinal) &&
                                         method.ReturnType == typeof(List<MediaSourceInfo>))
                        .ToArray();
                }

                status.TargetFound = targets.Length > 0;
                if (targets.Length == 0)
                {
                    status.Error = "Video.GetMediaSources returning MediaSourceInfo list was not found.";
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                var postfix = typeof(MultiVersionDisplayPatches).GetMethod(
                    nameof(MultiVersionDisplayPatches.GetMediaSourcesPostfix),
                    BindingFlags.Static | BindingFlags.Public);

                foreach (var target in targets)
                {
                    // Only patch concrete List<MediaSourceInfo> returns; a future incompatible return type
                    // is reported through capability state rather than guessed.
                    if (target.ReturnType != typeof(List<MediaSourceInfo>)) continue;
                    _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                    status.Targets.Add(target.ToString());
                }

                status.Patched = status.Targets.Count > 0;
                if (!status.Patched)
                    status.Error = "GetMediaSources overloads were found but no compatible List<MediaSourceInfo> return type was available.";
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Multi-version display runtime mod unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }
    }

    public static class MultiVersionDisplayPatches
    {
        public static void GetMediaSourcesPostfix(Video __instance, ref List<MediaSourceInfo> __result)
        {
            try
            {
                if (__instance == null || __result == null || __result.Count <= 1) return;
                __result = MultiVersionRuntimeSettings.Enhance(__result);
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Multi-version media-source display enhancement skipped: " + ex.Message);
            }
        }
    }
}
