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
        public long PreflightBlocked { get; set; }
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
    /// Optional transaction extension for OpenList remote deep delete.
    ///
    /// The sidecar directory listing is frozen in a prefix before the main destructive request. If the
    /// listing is unreadable/truncated/unsafe, the main remote file is not touched. After the main file
    /// is verified missing, only candidates from that frozen conservative plan are removed and verified.
    /// Local STRM/Emby deletion remains blocked until both stages complete.
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
                var prefix = new HarmonyMethod(typeof(OpenListRemoteSidecarDeletePatches).GetMethod(
                    nameof(OpenListRemoteSidecarDeletePatches.Prefix), BindingFlags.Public | BindingFlags.Static));
                var postfix = new HarmonyMethod(typeof(OpenListRemoteSidecarDeletePatches).GetMethod(
                    nameof(OpenListRemoteSidecarDeletePatches.Postfix), BindingFlags.Public | BindingFlags.Static));
                _harmony.Patch(target, prefix: prefix, postfix: postfix);
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

        /// <summary>
        /// Freeze the candidate set before the main file is deleted. This intentionally blocks the
        /// delete call for one bounded OpenList directory-list request; deep delete is rare/destructive,
        /// and avoiding an irreversible partial state is more important than request-thread throughput.
        /// </summary>
        public static bool Prefix(RemoteDeepDeletePlan plan,
            ref Task<RemoteDeepDeleteExecutionResult> __result,
            out OpenListRemoteSidecarPlan __state)
        {
            __state = null;
            var options = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            if (!options.DeleteAssociatedSidecars || options.Provider != RemoteDeepDeleteProviderType.OpenList)
                return true;

            Increment(status =>
            {
                status.TransactionsObserved++;
                status.LastRemotePath = plan?.RemotePath;
            });

            try
            {
                var frozen = Service.PlanAsync(plan, CancellationToken.None).GetAwaiter().GetResult();
                if (frozen?.Success != true)
                {
                    var error = frozen?.Error ?? "OpenList sidecar preflight did not return a valid plan.";
                    Increment(status =>
                    {
                        status.PreflightBlocked++;
                        status.SidecarTransactionsFailed++;
                        status.LastError = error;
                    });
                    __result = Task.FromResult(new RemoteDeepDeleteExecutionResult
                    {
                        Success = false,
                        Provider = plan?.Provider,
                        RemotePath = plan?.RemotePath,
                        Error = "Associated sidecar preflight failed before the main remote object was touched: " + error
                    });
                    return false;
                }

                __state = frozen;
                Increment(status =>
                {
                    status.PlansSucceeded++;
                    status.CandidatesSelected += frozen.Candidates?.Count ?? 0;
                    status.LastError = null;
                });
                return true;
            }
            catch (Exception ex)
            {
                var error = ex.GetBaseException().Message;
                Increment(status =>
                {
                    status.PreflightBlocked++;
                    status.SidecarTransactionsFailed++;
                    status.LastError = error;
                });
                __result = Task.FromResult(new RemoteDeepDeleteExecutionResult
                {
                    Success = false,
                    Provider = plan?.Provider,
                    RemotePath = plan?.RemotePath,
                    Error = "Associated sidecar preflight threw before the main remote object was touched: " + error
                });
                return false;
            }
        }

        public static void Postfix(RemoteDeepDeletePlan plan, OpenListRemoteSidecarPlan __state,
            ref Task<RemoteDeepDeleteExecutionResult> __result)
        {
            if (__result == null || plan == null || __state == null) return;
            __result = CompleteSidecarsAsync(plan, __state, __result);
        }

        private static async Task<RemoteDeepDeleteExecutionResult> CompleteSidecarsAsync(RemoteDeepDeletePlan plan,
            OpenListRemoteSidecarPlan frozenPlan, Task<RemoteDeepDeleteExecutionResult> original)
        {
            var main = await original.ConfigureAwait(false);
            if (main?.Success != true) return main;

            if (frozenPlan.Candidates == null || frozenPlan.Candidates.Count == 0)
            {
                Increment(status =>
                {
                    status.SidecarTransactionsSucceeded++;
                    status.LastError = null;
                });
                return main;
            }

            try
            {
                var sidecars = await Service.DeleteAndVerifyAsync(plan, frozenPlan, CancellationToken.None)
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
                main.Error = "Main remote target is verified deleted, but associated sidecar cleanup failed; " +
                             "local deletion was blocked. Retry is safe because sidecar mode requires missing main targets to be idempotent: " +
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
                main.Error = "Main remote target is verified deleted, but associated sidecar cleanup threw an exception; " +
                             "local deletion was blocked: " + ex.GetBaseException().Message;
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
