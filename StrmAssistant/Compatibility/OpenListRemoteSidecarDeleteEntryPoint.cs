using HarmonyLib;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.Experience;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class OpenListRemoteSidecarDeleteStatus
    {
        public bool ExecuteAsyncPatched { get; set; }
        public long TransactionsObserved { get; set; }
        public long PlansSucceeded { get; set; }
        public long CandidatesSelected { get; set; }
        public long SidecarTransactionsSucceeded { get; set; }
        public long SidecarTransactionsFailed { get; set; }
        public string LastRemotePath { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class OpenListRemoteSidecarDeleteState
    {
        public static OpenListRemoteSidecarDeleteStatus Status { get; internal set; } =
            new OpenListRemoteSidecarDeleteStatus();
    }

    /// <summary>
    /// Optional transaction extension. The main remote object must already have been verified missing by
    /// RemoteDeepDeleteService. Only then are conservative same-stem OpenList sidecars enumerated/deleted.
    /// A sidecar failure changes the overall result back to Success=false, which prevents local STRM/Emby
    /// deletion and makes the operation safely retryable on the next request.
    /// </summary>
    public sealed class OpenListRemoteSidecarDeleteEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.openlist-remote-sidecars";
        private Harmony _harmony;

        public void Run()
        {
            var status = new OpenListRemoteSidecarDeleteStatus();
            OpenListRemoteSidecarDeleteState.Status = status;
            try
            {
                var target = typeof(RemoteDeepDeleteService).GetMethod(nameof(RemoteDeepDeleteService.ExecuteAsync),
                    BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(RemoteDeepDeletePlan), typeof(CancellationToken) }, null);
                if (target == null)
                {
                    status.Error = "RemoteDeepDeleteService.ExecuteAsync was not found.";
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(target, postfix: new HarmonyMethod(
                    typeof(OpenListRemoteSidecarDeletePatches).GetMethod(
                        nameof(OpenListRemoteSidecarDeletePatches.Postfix), BindingFlags.Public | BindingFlags.Static)));
                status.ExecuteAsyncPatched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Error("OpenList remote sidecar patch failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }
    }

    public static class OpenListRemoteSidecarDeletePatches
    {
        private static readonly OpenListRemoteSidecarService Service = new OpenListRemoteSidecarService();
        private static readonly object StatusSync = new object();

        public static void Postfix(RemoteDeepDeletePlan plan, ref Task<RemoteDeepDeleteExecutionResult> __result)
        {
            if (__result == null || plan == null) return;
            var options = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            if (!options.DeleteAssociatedSidecars || options.Provider != RemoteDeepDeleteProviderType.OpenList) return;
            __result = CompleteSidecarsAsync(plan, __result);
        }

        private static async Task<RemoteDeepDeleteExecutionResult> CompleteSidecarsAsync(RemoteDeepDeletePlan plan,
            Task<RemoteDeepDeleteExecutionResult> original)
        {
            var main = await original.ConfigureAwait(false);
            if (main?.Success != true) return main;

            Increment(status =>
            {
                status.TransactionsObserved++;
                status.LastRemotePath = plan.RemotePath;
            });

            try
            {
                var sidecarPlan = await Service.PlanAsync(plan, CancellationToken.None).ConfigureAwait(false);
                if (!sidecarPlan.Success)
                {
                    Increment(status =>
                    {
                        status.SidecarTransactionsFailed++;
                        status.LastError = sidecarPlan.Error;
                    });
                    main.Success = false;
                    main.Error = "Main remote target is verified deleted, but associated sidecar planning failed; local deletion was blocked for retry: " +
                                 sidecarPlan.Error;
                    return main;
                }

                Increment(status =>
                {
                    status.PlansSucceeded++;
                    status.CandidatesSelected += sidecarPlan.Candidates?.Count ?? 0;
                });
                if (sidecarPlan.Candidates == null || sidecarPlan.Candidates.Count == 0)
                {
                    Increment(status =>
                    {
                        status.SidecarTransactionsSucceeded++;
                        status.LastError = null;
                    });
                    return main;
                }

                var sidecars = await Service.DeleteAndVerifyAsync(plan, sidecarPlan, CancellationToken.None)
                    .ConfigureAwait(false);
                if (sidecars.Success)
                {
                    Increment(status =>
                    {
                        status.SidecarTransactionsSucceeded++;
                        status.LastError = null;
                    });
                    Plugin.Instance?.Logger?.Info(
                        "OpenList remote sidecar cleanup verified: {0} sidecars for {1}",
                        sidecars.RequestedNames?.Count ?? 0, plan.RemotePath);
                    return main;
                }

                Increment(status =>
                {
                    status.SidecarTransactionsFailed++;
                    status.LastError = sidecars.Error;
                });
                main.Success = false;
                main.Error = "Main remote target is verified deleted, but associated sidecar cleanup failed; local deletion was blocked so retry can finish cleanup: " +
                             sidecars.Error;
                Plugin.Instance?.Logger?.Warn(main.Error);
                return main;
            }
            catch (Exception ex)
            {
                Increment(status =>
                {
                    status.SidecarTransactionsFailed++;
                    status.LastError = ex.GetBaseException().Message;
                });
                main.Success = false;
                main.Error = "Main remote target is verified deleted, but associated sidecar cleanup threw an exception; local deletion was blocked: " +
                             ex.GetBaseException().Message;
                return main;
            }
        }

        private static void Increment(Action<OpenListRemoteSidecarDeleteStatus> action)
        {
            lock (StatusSync)
            {
                var status = OpenListRemoteSidecarDeleteState.Status;
                if (status != null) action(status);
            }
        }
    }
}
