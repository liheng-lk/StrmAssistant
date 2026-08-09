using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.Experience;
using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class MultiVersionUserDataIsolationCapabilityStatus
    {
        public bool TargetFound { get; set; }
        public bool Patched { get; set; }
        public string Target { get; set; }
        public string Error { get; set; }
    }

    public static class MultiVersionUserDataIsolationModState
    {
        public static MultiVersionUserDataIsolationCapabilityStatus Status { get; internal set; } =
            new MultiVersionUserDataIsolationCapabilityStatus();
    }

    /// <summary>
    /// Experimental runtime-only user-data key isolation for merged Video items.
    /// It does not write user-data rows or mutate BaseItem.UserDataKey. The patch changes only the
    /// value returned by Video.GetUserDataKeyInternal when the current item can be proven to be part
    /// of an alternate-version group. Existing user data is deliberately not migrated automatically.
    /// </summary>
    public sealed class MultiVersionUserDataIsolationRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.multiversion-userdata";
        private Harmony _harmony;

        public void Run()
        {
            var status = new MultiVersionUserDataIsolationCapabilityStatus();
            MultiVersionUserDataIsolationModState.Status = status;
            try
            {
                var target = typeof(Video)
                    .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .FirstOrDefault(method => method.Name == "GetUserDataKeyInternal" &&
                                              method.ReturnType == typeof(string) &&
                                              method.GetParameters().Length == 1);
                status.TargetFound = target != null;
                status.Target = target?.ToString();
                if (target == null) return;

                _harmony = new Harmony(HarmonyId);
                var postfix = typeof(MultiVersionUserDataIsolationPatches).GetMethod(
                    nameof(MultiVersionUserDataIsolationPatches.GetUserDataKeyInternalPostfix),
                    BindingFlags.Public | BindingFlags.Static);
                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                status.Patched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Multi-version UserData isolation patch failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
        }
    }

    public static class MultiVersionUserDataIsolationPatches
    {
        public static void GetUserDataKeyInternalPostfix(Video __instance, ref string __result)
        {
            try
            {
                if (__instance == null || MultiVersionRuntimeSettings.GetSnapshot().IsolateUserDataPerVersion != true)
                    return;
                if (!IsMergedVersion(__instance)) return;

                __result = "strmassistant-version:" +
                           __instance.InternalId.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Multi-version UserData isolation skipped: " + ex.Message);
            }
        }

        internal static bool IsMergedVersion(Video video)
        {
            if (video == null) return false;

            // Use reflection for alternate-version signals so older/newer Emby builds can compile
            // even when one of these convenience members is renamed or removed.
            try
            {
                var method = video.GetType().GetMethod("GetAlternateVersionIds",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                var value = method?.Invoke(video, Array.Empty<object>());
                if (value is ICollection collection && collection.Count > 0) return true;
                if (value is IEnumerable enumerable)
                {
                    var enumerator = enumerable.GetEnumerator();
                    try { if (enumerator.MoveNext()) return true; }
                    finally { (enumerator as IDisposable)?.Dispose(); }
                }
            }
            catch { }

            try
            {
                var secondaryProperty = video.GetType().GetProperty("IsSecondaryMergedItemInSameFolder",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (secondaryProperty?.CanRead == true &&
                    secondaryProperty.GetValue(video) is bool secondary && secondary)
                    return true;
            }
            catch { }

            try
            {
                var property = video.GetType().GetProperty("PrimaryVersionId",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.CanRead == true)
                {
                    var value = property.GetValue(video);
                    if (value != null)
                    {
                        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
                        if (!string.IsNullOrWhiteSpace(text) &&
                            !string.Equals(text, "0", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(text, Guid.Empty.ToString(), StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch { }

            return false;
        }
    }
}
