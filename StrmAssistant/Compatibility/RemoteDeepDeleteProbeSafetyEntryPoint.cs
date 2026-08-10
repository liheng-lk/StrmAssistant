using HarmonyLib;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.Experience;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class RemoteDeepDeleteProbeSafetyStatus
    {
        public bool Patched { get; set; }
        public long ResultsObserved { get; set; }
        public long SuccessResponsesCorrectedToExists { get; set; }
        public long UnsafeMissingResultsRejected { get; set; }
        public long AuthorizationFailuresRejected { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class RemoteDeepDeleteProbeSafetyState
    {
        public static RemoteDeepDeleteProbeSafetyStatus Status { get; internal set; } =
            new RemoteDeepDeleteProbeSafetyStatus();
    }

    /// <summary>
    /// Final semantic guard for OpenList existence probes. The underlying compatibility parser accepts
    /// common historical "not found" response text, but destructive decisions must prefer structured
    /// transport/API status over fuzzy body text whenever those status values are available.
    /// </summary>
    public sealed class RemoteDeepDeleteProbeSafetyEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.remote-delete-probe-safety";
        private Harmony _harmony;

        public void Run()
        {
            var status = new RemoteDeepDeleteProbeSafetyStatus();
            RemoteDeepDeleteProbeSafetyState.Status = status;
            try
            {
                var target = typeof(RemoteDeepDeleteService).GetMethod(nameof(RemoteDeepDeleteService.ProbeAsync),
                    BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(RemoteDeepDeletePlan), typeof(CancellationToken) }, null);
                if (target == null)
                {
                    status.Error = "RemoteDeepDeleteService.ProbeAsync was not found.";
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(target, postfix: new HarmonyMethod(
                    typeof(RemoteDeepDeleteProbeSafetyPatches).GetMethod(
                        nameof(RemoteDeepDeleteProbeSafetyPatches.Postfix),
                        BindingFlags.Public | BindingFlags.Static)));
                status.Patched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Error("Remote deep-delete probe safety patch failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }
    }

    public static class RemoteDeepDeleteProbeSafetyPatches
    {
        private static readonly object Sync = new object();

        public static void Postfix(ref Task<RemoteDeepDeleteProbeResult> __result)
        {
            if (__result == null) return;
            __result = NormalizeAsync(__result);
        }

        private static async Task<RemoteDeepDeleteProbeResult> NormalizeAsync(
            Task<RemoteDeepDeleteProbeResult> original)
        {
            var result = await original.ConfigureAwait(false);
            if (result == null || !string.Equals(result.Provider,
                    RemoteDeepDeleteProviderType.OpenList.ToString(), StringComparison.OrdinalIgnoreCase))
                return result;

            Increment(status => status.ResultsObserved++);
            var http = result.HttpStatusCode;
            var api = result.ApiCode;

            if (http == 401 || http == 403 || api == 401 || api == 403)
            {
                result.Success = false;
                result.Exists = false;
                result.Missing = false;
                result.Error = "OpenList probe authorization failed (HTTP " + http +
                               (api.HasValue ? ", API code " + api.Value : string.Empty) + ").";
                Increment(status =>
                {
                    status.AuthorizationFailuresRejected++;
                    status.LastError = result.Error;
                });
                return result;
            }

            // A structured OpenList success response wins over fuzzy words that may appear in a valid
            // filename or metadata field inside the JSON body.
            if (http >= 200 && http < 300 && api == 200 && result.Missing)
            {
                result.Success = true;
                result.Exists = true;
                result.Missing = false;
                result.Error = null;
                Increment(status =>
                {
                    status.SuccessResponsesCorrectedToExists++;
                    status.LastError = null;
                });
                return result;
            }

            // Only explicit HTTP 404/410 may represent missing at the transport level. Other non-2xx
            // responses are provider/auth/backend failures and must never authorize local cleanup.
            if (http != 0 && (http < 200 || http >= 300) && http != 404 && http != 410 && result.Missing)
            {
                result.Success = false;
                result.Exists = false;
                result.Missing = false;
                result.Error = "OpenList probe returned non-success HTTP " + http +
                               "; fuzzy response text is not accepted as proof that the object is missing.";
                Increment(status =>
                {
                    status.UnsafeMissingResultsRejected++;
                    status.LastError = result.Error;
                });
            }

            return result;
        }

        private static void Increment(Action<RemoteDeepDeleteProbeSafetyStatus> action)
        {
            lock (Sync)
            {
                var status = RemoteDeepDeleteProbeSafetyState.Status;
                if (status != null) action(status);
            }
        }
    }
}
