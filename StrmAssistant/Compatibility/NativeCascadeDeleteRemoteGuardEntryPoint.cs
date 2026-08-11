using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.Experience;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class NativeCascadeDeleteRemoteGuardStatus
    {
        public int SingleDeleteTargetsPatched { get; set; }
        public int BatchDeleteTargetsPatched { get; set; }
        public long RequestsObserved { get; set; }
        public long RemoteCascadeRequired { get; set; }
        public long NativeDeletesBlocked { get; set; }
        public string LastRootIds { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class NativeCascadeDeleteRemoteGuardState
    {
        public static NativeCascadeDeleteRemoteGuardStatus Status { get; internal set; } =
            new NativeCascadeDeleteRemoteGuardStatus();
    }

    /// <summary>
    /// Safety gate for Emby's folder/Series/Season and batch delete routes. A direct single media leaf is
    /// still handled by NativeItemDeleteRemoteBridge. A parent/batch containing remote STRM leaves is
    /// blocked so Emby cannot silently remove the local tree while leaving cloud objects behind. The
    /// administrator must review the plugin cascade plan and use the explicit confirmed cascade API.
    /// </summary>
    public sealed class NativeCascadeDeleteRemoteGuardEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.native-cascade-delete-guard";
        private Harmony _harmony;

        public void Run()
        {
            var status = new NativeCascadeDeleteRemoteGuardStatus();
            NativeCascadeDeleteRemoteGuardState.Status = status;
            try
            {
                TryLoad("Emby.Api");
                TryLoad("MediaBrowser.Api");
                var targets = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(SafeGetTypes)
                    .Where(type => type != null &&
                                   type.Assembly != typeof(NativeCascadeDeleteRemoteGuardEntryPoint).Assembly &&
                                   type.Name.IndexOf("LibraryService", StringComparison.OrdinalIgnoreCase) >= 0)
                    .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    .Select(method => new { Method = method, Kind = GetKind(method) })
                    .Where(value => value.Kind != DeleteRouteKind.None)
                    .ToArray();

                _harmony = new Harmony(HarmonyId);
                var prefix = new HarmonyMethod(typeof(NativeCascadeDeleteRemoteGuardPatches).GetMethod(
                    nameof(NativeCascadeDeleteRemoteGuardPatches.Prefix), BindingFlags.Public | BindingFlags.Static));
                foreach (var target in targets)
                {
                    try
                    {
                        _harmony.Patch(target.Method, prefix: prefix);
                        if (target.Kind == DeleteRouteKind.Batch) status.BatchDeleteTargetsPatched++;
                        else status.SingleDeleteTargetsPatched++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Instance?.Logger?.Warn("Native cascade delete guard could not patch {0}: {1}",
                            target.Method, ex.Message);
                    }
                }

                if (status.SingleDeleteTargetsPatched == 0 && status.BatchDeleteTargetsPatched == 0)
                    status.Error = "No compatible Emby LibraryService delete routes were discovered.";
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Error("Native cascade delete guard initialization failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }

        internal enum DeleteRouteKind { None, Single, Batch }

        internal static DeleteRouteKind GetKind(MethodBase method)
        {
            if (!(method is MethodInfo info)) return DeleteRouteKind.None;
            var parameters = info.GetParameters();
            if (parameters.Length != 1 || parameters[0].ParameterType == typeof(string)) return DeleteRouteKind.None;
            foreach (var attribute in parameters[0].ParameterType.GetCustomAttributes(true))
            {
                var type = attribute?.GetType();
                if (type == null || !string.Equals(type.Name, "RouteAttribute", StringComparison.Ordinal)) continue;
                var path = Read(attribute, "Path") ?? Read(attribute, "Template");
                var verbs = Read(attribute, "Verbs") ?? Read(attribute, "Verb");
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (!string.IsNullOrWhiteSpace(verbs) &&
                    verbs.IndexOf("DELETE", StringComparison.OrdinalIgnoreCase) < 0 &&
                    verbs.IndexOf("POST", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var normalized = "/" + path.Trim().Trim('/');
                if (string.Equals(normalized, "/Items", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalized, "/Items/Delete", StringComparison.OrdinalIgnoreCase))
                    return DeleteRouteKind.Batch;
                if (string.Equals(normalized, "/Items/{Id}", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalized, "/Items/{Id}/Delete", StringComparison.OrdinalIgnoreCase))
                    return DeleteRouteKind.Single;
            }
            return DeleteRouteKind.None;
        }

        private static string Read(object target, string name)
        {
            try { return target?.GetType().GetProperty(name)?.GetValue(target)?.ToString(); }
            catch { return null; }
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            if (assembly == null) return Array.Empty<Type>();
            try { return assembly.GetTypes(); }
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

    public static class NativeCascadeDeleteRemoteGuardPatches
    {
        private static readonly object StatusSync = new object();

        public static void Prefix(object[] __args, MethodBase __originalMethod)
        {
            Increment(status => status.RequestsObserved++);
            var remote = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            var experience = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            if (!remote.Enabled || remote.Provider == RemoteDeepDeleteProviderType.None ||
                experience?.EnableDeepDelete != true) return;

            var kind = NativeCascadeDeleteRemoteGuardEntryPoint.GetKind(__originalMethod);
            if (kind == NativeCascadeDeleteRemoteGuardEntryPoint.DeleteRouteKind.None) return;
            var request = __args?.FirstOrDefault(arg => arg != null);
            var ids = ReadIds(request, kind).ToArray();
            if (ids.Length == 0) return;

            var manager = Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>();
            if (manager == null) return;
            var roots = ids.Select(id => ResolveItem(manager, id)).Where(item => item != null)
                .GroupBy(item => item.InternalId).Select(group => group.First()).ToArray();
            if (roots.Length != ids.Length)
                Block(ids, "One or more requested Emby delete ids could not be resolved before remote cascade safety inspection.");

            var cascade = new RemoteDeepDeleteCascadeService(manager)
                .BuildPlan(roots, RemoteDeepDeleteCascadeService.DefaultMaxRemoteCandidates);
            var protectedEntries = kind == NativeCascadeDeleteRemoteGuardEntryPoint.DeleteRouteKind.Single
                ? cascade.Entries.Where(entry => entry.IsDescendant && (entry.RequiresRemoteDelete || entry.LooksRemote)).ToList()
                : cascade.Entries.Where(entry => entry.RequiresRemoteDelete || entry.LooksRemote).ToList();
            if (protectedEntries.Count == 0) return;

            Increment(status => status.RemoteCascadeRequired++);
            var message = cascade.CandidateLimitExceeded
                ? cascade.Error
                : "This native delete contains " + protectedEntries.Count +
                  " remote STRM leaf item(s). Native local deletion is blocked to prevent orphaning cloud media. " +
                  "Review GET /StrmAssistant/DeepDelete/{Id}/CascadePlan (or /DeepDelete/CascadePlan?Ids=...) and execute the explicit confirmed cascade delete instead.";
            Block(ids, message);
        }

        private static IEnumerable<string> ReadIds(object request,
            NativeCascadeDeleteRemoteGuardEntryPoint.DeleteRouteKind kind)
        {
            if (request == null) return Array.Empty<string>();
            try
            {
                if (kind == NativeCascadeDeleteRemoteGuardEntryPoint.DeleteRouteKind.Single)
                {
                    var id = request.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.Public)
                        ?.GetValue(request)?.ToString()?.Trim();
                    return string.IsNullOrWhiteSpace(id) ? Array.Empty<string>() : new[] { id };
                }
                var raw = request.GetType().GetProperty("Ids", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(request);
                if (raw == null) return Array.Empty<string>();
                if (raw is string text)
                    return text.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(value => value.Trim()).Where(value => value.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase);
                if (raw is IEnumerable enumerable)
                {
                    var result = new List<string>();
                    foreach (var value in enumerable)
                    {
                        var textValue = value?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(textValue)) result.Add(textValue);
                    }
                    return result.Distinct(StringComparer.OrdinalIgnoreCase);
                }
                var single = raw.ToString()?.Trim();
                return string.IsNullOrWhiteSpace(single) ? Array.Empty<string>() : new[] { single };
            }
            catch { return Array.Empty<string>(); }
        }

        private static BaseItem ResolveItem(ILibraryManager manager, string id)
        {
            if (manager == null || string.IsNullOrWhiteSpace(id)) return null;
            if (long.TryParse(id, out var internalId))
            {
                try
                {
                    var item = manager.GetItemById(internalId);
                    if (item != null) return item;
                }
                catch { }
            }
            foreach (var method in manager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                         .Where(method => string.Equals(method.Name, "GetItemById", StringComparison.Ordinal) &&
                                          method.GetParameters().Length == 1))
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

        private static void Block(IEnumerable<string> ids, string error)
        {
            lock (StatusSync)
            {
                var status = NativeCascadeDeleteRemoteGuardState.Status;
                if (status != null)
                {
                    status.NativeDeletesBlocked++;
                    status.LastRootIds = string.Join(",", (ids ?? Enumerable.Empty<string>()).Take(20));
                    status.LastError = error;
                }
            }
            throw new InvalidOperationException("StrmAssistant remote cascade required: " + error);
        }

        private static void Increment(Action<NativeCascadeDeleteRemoteGuardStatus> action)
        {
            lock (StatusSync)
            {
                var status = NativeCascadeDeleteRemoteGuardState.Status;
                if (status != null) action(status);
            }
        }
    }
}
