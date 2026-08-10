using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.Experience;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class NativeRemoteDeleteTransactionStatus
    {
        public bool RemoteExecutionPatched { get; set; }
        public int NativeDeleteTargetsPatched { get; set; }
        public long RemoteSuccessesObserved { get; set; }
        public long LocalDeletesCompletedAfterRemoteSuccess { get; set; }
        public long LocalDeletesFailedAfterRemoteSuccess { get; set; }
        public long LocalItemsStillPresentAfterRemoteSuccess { get; set; }
        public string LastItemPath { get; set; }
        public string LastRemotePath { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class NativeRemoteDeleteTransactionState
    {
        public static NativeRemoteDeleteTransactionStatus Status { get; internal set; } =
            new NativeRemoteDeleteTransactionStatus();
    }

    public sealed class NativeRemoteDeleteCallState
    {
        public long ItemInternalId { get; set; }
        public string ItemPath { get; set; }
        public string RemoteKey { get; set; }
        public string RemotePath { get; set; }
    }

    internal sealed class RecentRemoteDeleteSuccess
    {
        public DateTimeOffset Timestamp { get; set; }
        public string RemotePath { get; set; }
    }

    /// <summary>
    /// Observability-only companion to NativeItemDeleteRemoteBridgeEntryPoint. It never decides whether
    /// a delete is allowed and never issues a destructive request. It correlates a successful remote
    /// delete with the surrounding explicit Emby DeleteItem route so an irreversible partial failure
    /// (cloud object gone, local Emby item still present) becomes immediately visible in diagnostics/logs.
    /// </summary>
    public sealed class NativeRemoteDeleteTransactionMonitorEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.native-remote-delete-transaction-monitor";
        private Harmony _harmony;

        public void Run()
        {
            var status = new NativeRemoteDeleteTransactionStatus();
            NativeRemoteDeleteTransactionState.Status = status;
            try
            {
                _harmony = new Harmony(HarmonyId);

                var execute = typeof(RemoteDeepDeleteService).GetMethod(nameof(RemoteDeepDeleteService.ExecuteAsync),
                    BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(RemoteDeepDeletePlan), typeof(System.Threading.CancellationToken) }, null);
                if (execute != null)
                {
                    _harmony.Patch(execute, postfix: new HarmonyMethod(
                        typeof(NativeRemoteDeleteTransactionPatches).GetMethod(
                            nameof(NativeRemoteDeleteTransactionPatches.RemoteExecutePostfix),
                            BindingFlags.Public | BindingFlags.Static)));
                    status.RemoteExecutionPatched = true;
                }

                TryLoad("Emby.Api");
                TryLoad("MediaBrowser.Api");
                var targets = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(SafeGetTypes)
                    .Where(type => type != null && type.Assembly != typeof(NativeRemoteDeleteTransactionMonitorEntryPoint).Assembly)
                    .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    .Where(IsExplicitSingleItemDeleteMethod)
                    .Distinct()
                    .ToArray();

                var prefix = new HarmonyMethod(typeof(NativeRemoteDeleteTransactionPatches).GetMethod(
                    nameof(NativeRemoteDeleteTransactionPatches.NativeDeletePrefix), BindingFlags.Public | BindingFlags.Static))
                { priority = Priority.First };
                var postfix = new HarmonyMethod(typeof(NativeRemoteDeleteTransactionPatches).GetMethod(
                    nameof(NativeRemoteDeleteTransactionPatches.NativeDeletePostfix), BindingFlags.Public | BindingFlags.Static))
                { priority = Priority.Last };
                var finalizer = new HarmonyMethod(typeof(NativeRemoteDeleteTransactionPatches).GetMethod(
                    nameof(NativeRemoteDeleteTransactionPatches.NativeDeleteFinalizer), BindingFlags.Public | BindingFlags.Static))
                { priority = Priority.Last };

                foreach (var target in targets)
                {
                    try
                    {
                        _harmony.Patch(target, prefix: prefix, postfix: postfix, finalizer: finalizer);
                        status.NativeDeleteTargetsPatched++;
                    }
                    catch (Exception ex)
                    {
                        status.LastError = ex.Message;
                    }
                }

                if (!status.RemoteExecutionPatched || status.NativeDeleteTargetsPatched == 0)
                    status.Error = "Remote execution and/or explicit native DeleteItem route could not be monitored.";
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Error("Native remote-delete transaction monitor failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }

        private static bool IsExplicitSingleItemDeleteMethod(MethodInfo method)
        {
            if (method == null || method.GetParameters().Length != 1) return false;
            var requestType = method.GetParameters()[0].ParameterType;
            if (requestType == null || requestType == typeof(string)) return false;
            foreach (var attribute in requestType.GetCustomAttributes(true))
            {
                if (!string.Equals(attribute?.GetType().Name, "RouteAttribute", StringComparison.Ordinal)) continue;
                var path = ReadString(attribute, "Path") ?? ReadString(attribute, "Template");
                var verbs = ReadString(attribute, "Verbs") ?? ReadString(attribute, "Verb");
                if (string.IsNullOrWhiteSpace(path)) continue;
                var normalized = "/" + path.Trim().Trim('/');
                if (!string.Equals(normalized, "/Items/{Id}", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(normalized, "/Items/{Id}/Delete", StringComparison.OrdinalIgnoreCase)) continue;
                return string.IsNullOrWhiteSpace(verbs) ||
                       verbs.IndexOf("DELETE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       verbs.IndexOf("POST", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return false;
        }

        private static string ReadString(object target, string name)
        {
            try { return target?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target)?.ToString(); }
            catch { return null; }
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try { return assembly?.GetTypes() ?? Array.Empty<Type>(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(type => type != null); }
            catch { return Array.Empty<Type>(); }
        }

        private static Assembly TryLoad(string name)
        {
            try { return Assembly.Load(name); }
            catch
            {
                return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
                    string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public static class NativeRemoteDeleteTransactionPatches
    {
        private static readonly RemoteDeepDeleteService RemoteService = new RemoteDeepDeleteService();
        private static readonly ConcurrentDictionary<string, RecentRemoteDeleteSuccess> RecentSuccesses =
            new ConcurrentDictionary<string, RecentRemoteDeleteSuccess>(StringComparer.OrdinalIgnoreCase);
        private static readonly object StatusSync = new object();
        private static readonly TimeSpan CorrelationWindow = TimeSpan.FromMinutes(5);

        public static void RemoteExecutePostfix(RemoteDeepDeletePlan plan,
            ref Task<RemoteDeepDeleteExecutionResult> __result)
        {
            if (__result == null || plan == null) return;
            __result = ObserveRemoteExecutionAsync(plan, __result);
        }

        public static void NativeDeletePrefix(object[] __args, out NativeRemoteDeleteCallState __state)
        {
            __state = new NativeRemoteDeleteCallState();
            PruneExpired();
            try
            {
                var request = __args?.FirstOrDefault(arg => arg != null);
                var id = ReadRequestId(request);
                if (string.IsNullOrWhiteSpace(id)) return;
                var manager = Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>();
                var item = ResolveItem(manager, id);
                if (item == null) return;
                var plan = RemoteService.BuildPlan(item);
                if (plan?.Applicable != true || string.IsNullOrWhiteSpace(plan.RemotePath)) return;

                __state.ItemInternalId = item.InternalId;
                __state.ItemPath = item.Path;
                __state.RemotePath = plan.RemotePath;
                __state.RemoteKey = Key(plan.Provider, plan.RemotePath);
            }
            catch (Exception ex)
            {
                SetStatus(status => status.LastError = "Transaction monitor prefix: " + ex.Message);
            }
        }

        public static void NativeDeletePostfix(NativeRemoteDeleteCallState __state)
        {
            if (!TryConsumeRecentSuccess(__state, out var remote)) return;
            var manager = Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>();
            var stillExists = false;
            try { stillExists = manager?.GetItemById(__state.ItemInternalId) != null; } catch { }

            SetStatus(status =>
            {
                status.LastItemPath = __state.ItemPath;
                status.LastRemotePath = remote.RemotePath;
                if (stillExists)
                {
                    status.LocalItemsStillPresentAfterRemoteSuccess++;
                    status.LastError = "Remote target was deleted successfully, but the Emby item still exists after the native DeleteItem method returned.";
                }
                else
                {
                    status.LocalDeletesCompletedAfterRemoteSuccess++;
                    status.LastError = null;
                }
            });

            if (stillExists)
                Plugin.Instance?.Logger?.Error(
                    "CRITICAL remote-delete partial state: cloud target is gone but Emby item still exists. item={0}, remote={1}",
                    __state.ItemPath, remote.RemotePath);
        }

        public static Exception NativeDeleteFinalizer(Exception __exception, NativeRemoteDeleteCallState __state)
        {
            if (__exception == null) return null;
            if (!TryConsumeRecentSuccess(__state, out var remote)) return __exception;

            SetStatus(status =>
            {
                status.LocalDeletesFailedAfterRemoteSuccess++;
                status.LastItemPath = __state.ItemPath;
                status.LastRemotePath = remote.RemotePath;
                status.LastError = __exception.GetBaseException().Message;
            });
            Plugin.Instance?.Logger?.Error(
                "CRITICAL remote-delete partial failure: remote target was deleted but native Emby deletion failed. item={0}, remote={1}, error={2}",
                __state.ItemPath, remote.RemotePath, __exception.GetBaseException().Message);
            return __exception;
        }

        private static async Task<RemoteDeepDeleteExecutionResult> ObserveRemoteExecutionAsync(RemoteDeepDeletePlan plan,
            Task<RemoteDeepDeleteExecutionResult> original)
        {
            var result = await original.ConfigureAwait(false);
            if (result?.Success == true && !string.IsNullOrWhiteSpace(plan.RemotePath))
            {
                var key = Key(plan.Provider, plan.RemotePath);
                RecentSuccesses[key] = new RecentRemoteDeleteSuccess
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    RemotePath = plan.RemotePath
                };
                SetStatus(status =>
                {
                    status.RemoteSuccessesObserved++;
                    status.LastRemotePath = plan.RemotePath;
                });
            }
            return result;
        }

        private static bool TryConsumeRecentSuccess(NativeRemoteDeleteCallState state,
            out RecentRemoteDeleteSuccess remote)
        {
            remote = null;
            if (state == null || string.IsNullOrWhiteSpace(state.RemoteKey)) return false;
            if (!RecentSuccesses.TryGetValue(state.RemoteKey, out var candidate)) return false;
            if (DateTimeOffset.UtcNow - candidate.Timestamp > CorrelationWindow)
            {
                RecentSuccesses.TryRemove(state.RemoteKey, out _);
                return false;
            }
            RecentSuccesses.TryRemove(state.RemoteKey, out _);
            remote = candidate;
            return true;
        }

        private static void PruneExpired()
        {
            var cutoff = DateTimeOffset.UtcNow - CorrelationWindow;
            foreach (var pair in RecentSuccesses)
                if (pair.Value.Timestamp < cutoff) RecentSuccesses.TryRemove(pair.Key, out _);
        }

        private static string Key(string provider, string remotePath)
        {
            return (provider ?? string.Empty).Trim().ToLowerInvariant() + "|" +
                   (remotePath ?? string.Empty).Trim().Replace('\\', '/');
        }

        private static string ReadRequestId(object request)
        {
            try { return request?.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.Public)?.GetValue(request)?.ToString(); }
            catch { return null; }
        }

        private static BaseItem ResolveItem(ILibraryManager manager, string id)
        {
            if (manager == null || string.IsNullOrWhiteSpace(id)) return null;
            if (long.TryParse(id, out var internalId))
            {
                try
                {
                    var byLong = manager.GetItemById(internalId);
                    if (byLong != null) return byLong;
                }
                catch { }
            }
            foreach (var method in manager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                         .Where(method => string.Equals(method.Name, "GetItemById", StringComparison.Ordinal) && method.GetParameters().Length == 1))
            {
                try
                {
                    var parameterType = method.GetParameters()[0].ParameterType;
                    object argument = null;
                    if (parameterType == typeof(string)) argument = id;
                    else if (parameterType == typeof(Guid) && Guid.TryParse(id, out var guid)) argument = guid;
                    else continue;
                    if (method.Invoke(manager, new[] { argument }) is BaseItem item) return item;
                }
                catch { }
            }
            return null;
        }

        private static void SetStatus(Action<NativeRemoteDeleteTransactionStatus> action)
        {
            lock (StatusSync)
            {
                var status = NativeRemoteDeleteTransactionState.Status;
                if (status != null) action(status);
            }
        }
    }
}
