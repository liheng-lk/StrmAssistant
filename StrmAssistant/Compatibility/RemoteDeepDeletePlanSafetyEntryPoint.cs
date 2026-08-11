using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.Experience;
using System;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class RemoteDeepDeletePlanSafetyStatus
    {
        public bool Patched { get; set; }
        public long PlansObserved { get; set; }
        public long ManualMappingsValidated { get; set; }
        public long UnsafeMappingsBlocked { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class RemoteDeepDeletePlanSafetyState
    {
        public static RemoteDeepDeletePlanSafetyStatus Status { get; internal set; } =
            new RemoteDeepDeletePlanSafetyStatus();
    }

    /// <summary>
    /// Final path-boundary guard for manually mapped remote URLs. RemoteDeepDeleteService keeps legacy
    /// StartsWith mapping compatibility, while this guard refuses destructive execution unless the
    /// chosen source prefix is an HTTP(S) URI on the same authority and its decoded path is either an
    /// exact match or a complete parent path segment of the STRM target.
    /// </summary>
    public sealed class RemoteDeepDeletePlanSafetyEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.remote-delete-plan-safety";
        private Harmony _harmony;

        public void Run()
        {
            var status = new RemoteDeepDeletePlanSafetyStatus();
            RemoteDeepDeletePlanSafetyState.Status = status;
            try
            {
                var target = typeof(RemoteDeepDeleteService).GetMethod(nameof(RemoteDeepDeleteService.BuildPlan),
                    BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(BaseItem) }, null);
                if (target == null)
                {
                    status.Error = "RemoteDeepDeleteService.BuildPlan(BaseItem) was not found.";
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(target, postfix: new HarmonyMethod(
                    typeof(RemoteDeepDeletePlanSafetyPatches).GetMethod(
                        nameof(RemoteDeepDeletePlanSafetyPatches.Postfix),
                        BindingFlags.Public | BindingFlags.Static)));
                status.Patched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Error("Remote deep-delete plan safety patch failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }
    }

    public static class RemoteDeepDeletePlanSafetyPatches
    {
        private static readonly object Sync = new object();

        public static void Postfix(ref RemoteDeepDeletePlan __result)
        {
            var plan = __result;
            if (plan == null || !plan.Allowed) return;
            Increment(status => status.PlansObserved++);

            // OpenList same-origin /d/ auto-map has its own exact authority and /d/ path checks.
            if (string.IsNullOrWhiteSpace(plan.MatchedSourcePrefix) ||
                plan.MatchedSourcePrefix.StartsWith("[OpenList same-origin", StringComparison.Ordinal))
                return;

            if (IsSafeManualMapping(plan.SourceTarget, plan.MatchedSourcePrefix, out var error))
            {
                Increment(status =>
                {
                    status.ManualMappingsValidated++;
                    status.LastError = null;
                });
                return;
            }

            plan.Allowed = false;
            plan.Error = error;
            plan.Warnings.Add("The legacy prefix match was rejected by the destructive path-boundary safety guard.");
            Increment(status =>
            {
                status.UnsafeMappingsBlocked++;
                status.LastError = error;
            });
        }

        private static bool IsSafeManualMapping(string sourceTarget, string sourcePrefix, out string error)
        {
            error = null;
            if (!Uri.TryCreate(sourceTarget, UriKind.Absolute, out var target) ||
                !Uri.TryCreate(sourcePrefix, UriKind.Absolute, out var prefix))
            {
                error = "Remote destructive mappings must use absolute HTTP/HTTPS SourcePrefix URLs.";
                return false;
            }
            if (!IsHttp(target) || !IsHttp(prefix))
            {
                error = "Remote destructive mappings only accept HTTP/HTTPS SourcePrefix URLs.";
                return false;
            }
            if (!string.Equals(target.Scheme, prefix.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(target.IdnHost, prefix.IdnHost, StringComparison.OrdinalIgnoreCase) ||
                EffectivePort(target) != EffectivePort(prefix))
            {
                error = "Resolved STRM target authority does not exactly match the selected mapping SourcePrefix authority.";
                return false;
            }

            var targetPath = DecodePath(target.AbsolutePath);
            var prefixPath = DecodePath(prefix.AbsolutePath);
            if (targetPath == null || prefixPath == null)
            {
                error = "Remote mapping path could not be decoded safely.";
                return false;
            }

            prefixPath = prefixPath.TrimEnd('/');
            if (prefixPath.Length == 0) prefixPath = "/";
            var exact = string.Equals(targetPath, prefixPath, StringComparison.Ordinal);
            var child = prefixPath == "/"
                ? targetPath.StartsWith("/", StringComparison.Ordinal)
                : targetPath.StartsWith(prefixPath + "/", StringComparison.Ordinal);
            if (!exact && !child)
            {
                error = "Resolved STRM URL matched the configured SourcePrefix textually but not at a case-sensitive URI path-segment boundary.";
                return false;
            }
            return true;
        }

        private static string DecodePath(string path)
        {
            try
            {
                var value = Uri.UnescapeDataString(path ?? string.Empty).Replace('\\', '/');
                while (value.Contains("//")) value = value.Replace("//", "/");
                if (!value.StartsWith("/", StringComparison.Ordinal)) value = "/" + value;
                return value;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsHttp(Uri uri)
        {
            return uri != null && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static int EffectivePort(Uri uri)
        {
            if (!uri.IsDefaultPort) return uri.Port;
            return uri.Scheme == Uri.UriSchemeHttps ? 443 : 80;
        }

        private static void Increment(Action<RemoteDeepDeletePlanSafetyStatus> action)
        {
            lock (Sync)
            {
                var status = RemoteDeepDeletePlanSafetyState.Status;
                if (status != null) action(status);
            }
        }
    }
}
