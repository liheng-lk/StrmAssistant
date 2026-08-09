using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
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
                var candidates = typeof(Video)
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method => string.Equals(method.Name, "GetMediaSources", StringComparison.Ordinal) &&
                                     method.ReturnType == typeof(List<MediaSourceInfo>))
                    .Concat(typeof(BaseItem)
                        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .Where(method => string.Equals(method.Name, "GetMediaSources", StringComparison.Ordinal) &&
                                         method.ReturnType == typeof(List<MediaSourceInfo>)))
                    .ToArray();

                var targets = candidates
                    .Select(ResolveImplementedDeclaration)
                    .Where(method => method != null && !method.IsAbstract)
                    .GroupBy(GetMethodIdentity, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToArray();

                status.TargetFound = targets.Length > 0;
                if (targets.Length == 0)
                {
                    status.Error = "No implemented BaseItem/Video.GetMediaSources returning List<MediaSourceInfo> was found.";
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                var postfix = typeof(MultiVersionDisplayPatches).GetMethod(
                    nameof(MultiVersionDisplayPatches.GetMediaSourcesPostfix),
                    BindingFlags.Static | BindingFlags.Public);

                foreach (var target in targets)
                {
                    try
                    {
                        _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                        status.Targets.Add(target.DeclaringType?.FullName + "." + target);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Instance?.Logger?.Debug("Multi-version GetMediaSources candidate skipped: " + ex.Message);
                    }
                }

                status.Patched = status.Targets.Count > 0;
                if (!status.Patched)
                    status.Error = "GetMediaSources candidates were found, but none could be patched.";
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

        private static MethodInfo ResolveImplementedDeclaration(MethodInfo method)
        {
            if (method == null) return null;

            try
            {
                var baseDefinition = method.GetBaseDefinition();
                if (baseDefinition != null && !baseDefinition.IsAbstract)
                    return baseDefinition;
            }
            catch
            {
                // Fall through to the reflected method.
            }

            return method.IsAbstract ? null : method;
        }

        private static string GetMethodIdentity(MethodInfo method)
        {
            if (method == null) return string.Empty;
            try
            {
                return method.Module.ModuleVersionId + ":" + method.MetadataToken;
            }
            catch
            {
                return (method.DeclaringType?.AssemblyQualifiedName ?? string.Empty) + ":" + method;
            }
        }
    }

    public static class MultiVersionDisplayPatches
    {
        public static void GetMediaSourcesPostfix(BaseItem __instance, ref List<MediaSourceInfo> __result)
        {
            try
            {
                if (!(__instance is Video) || __result == null || __result.Count <= 1) return;
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
