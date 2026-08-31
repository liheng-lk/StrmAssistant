using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Services;
using StrmAssistant.Common;
using StrmAssistant.Experience;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class NativeItemDeleteWebhookBridgeStatus
    {
        public int DeleteTargetsPatched { get; set; }
        public long NativeDeleteRequestsObserved { get; set; }
        public long RemoteTargetsCaptured { get; set; }
        public long WebhookNotificationsAccepted { get; set; }
        public long NativeDeletesBlocked { get; set; }
        public string LastTargetMethod { get; set; }
        public string LastItemPath { get; set; }
        public string LastCapturedTargets { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class NativeItemDeleteWebhookBridgeState
    {
        public static NativeItemDeleteWebhookBridgeStatus Status { get; internal set; } =
            new NativeItemDeleteWebhookBridgeStatus();
    }

    /// <summary>
    /// Provider-agnostic bridge for Emby's normal DELETE /Items/{Id} route.
    ///
    /// The existing NativeItemDeleteRemoteBridge handles direct OpenList/WebDAV deletion when a
    /// destructive provider is configured. This bridge covers the other supported Deep Delete
    /// contract: an HTTP/HTTPS STRM target is captured before the .strm file disappears and emitted
    /// as the stable deep.delete notification so an external Emby Webhook consumer can perform the
    /// storage-provider-specific deletion.
    ///
    /// It intentionally does not run when the direct remote provider is enabled, preventing a
    /// duplicate direct-delete + webhook-delete race.
    /// </summary>
    public sealed class NativeItemDeleteWebhookBridgeEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.native-delete-webhook-bridge";
        private Harmony _harmony;

        public void Run()
        {
            var status = new NativeItemDeleteWebhookBridgeStatus();
            NativeItemDeleteWebhookBridgeState.Status = status;

            try
            {
                TryLoad("Emby.Api");
                TryLoad("MediaBrowser.Api");

                // Do not depend on the concrete service class name. Emby has kept the public
                // DELETE /Items/{Id} contract while internal service types/signatures may change.
                var targets = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(SafeGetTypes)
                    .Where(type => type != null &&
                                   type.Assembly != typeof(NativeItemDeleteWebhookBridgeEntryPoint).Assembly)
                    .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public |
                                                       BindingFlags.NonPublic))
                    .Where(IsExplicitSingleItemDeleteMethod)
                    .Distinct()
                    .ToArray();

                _harmony = new Harmony(HarmonyId);
                var prefix = new HarmonyMethod(typeof(NativeItemDeleteWebhookBridgePatches).GetMethod(
                    nameof(NativeItemDeleteWebhookBridgePatches.Prefix),
                    BindingFlags.Public | BindingFlags.Static))
                {
                    priority = Priority.First
                };

                foreach (var target in targets)
                {
                    try
                    {
                        _harmony.Patch(target, prefix: prefix);
                        status.DeleteTargetsPatched++;
                        status.LastTargetMethod = target.DeclaringType?.FullName + "." + target.Name;
                    }
                    catch (Exception ex)
                    {
                        status.LastError = ex.Message;
                        Plugin.Instance?.Logger?.Warn(
                            "Native deep.delete webhook bridge could not patch {0}: {1}", target, ex.Message);
                    }
                }

                if (status.DeleteTargetsPatched == 0)
                    status.Error = "No runtime handler for DELETE /Items/{Id} was discovered.";
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Error(
                    "Native deep.delete webhook bridge initialization failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
        }

        internal static bool IsExplicitSingleItemDeleteMethod(MethodInfo method)
        {
            if (method == null) return false;

            foreach (var parameter in method.GetParameters())
            {
                var requestType = parameter.ParameterType;
                if (requestType == null || requestType == typeof(string)) continue;
                if (IsDeleteRequestType(requestType)) return true;
            }

            return false;
        }

        internal static bool IsDeleteRequestType(Type requestType)
        {
            if (requestType == null) return false;
            foreach (var attribute in requestType.GetCustomAttributes(true))
            {
                var attributeType = attribute?.GetType();
                if (attributeType == null ||
                    !string.Equals(attributeType.Name, "RouteAttribute", StringComparison.Ordinal)) continue;

                var path = ReadStringProperty(attribute, "Path") ??
                           ReadStringProperty(attribute, "Template");
                var verbs = ReadStringProperty(attribute, "Verbs") ??
                            ReadStringProperty(attribute, "Verb");
                if (string.IsNullOrWhiteSpace(path)) continue;

                var normalized = "/" + path.Trim().Trim('/');
                if (!string.Equals(normalized, "/Items/{Id}", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(normalized, "/Items/{Id}/Delete", StringComparison.OrdinalIgnoreCase))
                    continue;

                return string.IsNullOrWhiteSpace(verbs) ||
                       verbs.IndexOf("DELETE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       verbs.IndexOf("POST", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return false;
        }

        private static string ReadStringProperty(object target, string propertyName)
        {
            try
            {
                return target?.GetType().GetProperty(propertyName,
                    BindingFlags.Instance | BindingFlags.Public)?.GetValue(target)?.ToString();
            }
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

    public static class NativeItemDeleteWebhookBridgePatches
    {
        private static readonly object StatusSync = new object();

        public static void Prefix(object __instance, object[] __args, MethodBase __originalMethod)
        {
            Increment(status => status.NativeDeleteRequestsObserved++);

            var experience = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            if (experience?.EnableDeepDelete != true) return;

            // A configured destructive provider owns the operation and the existing direct bridge
            // performs provider deletion + verification. Do not emit a second destructive trigger.
            var remote = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            if (remote.Enabled && remote.Provider != RemoteDeepDeleteProviderType.None) return;

            var request = FindDeleteRequest(__args);
            var id = ReadRequestId(request);
            if (string.IsNullOrWhiteSpace(id)) return;

            var libraryManager = Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>();
            var item = ResolveItem(libraryManager, id);
            if (item == null || string.IsNullOrWhiteSpace(item.Path)) return;

            var targets = DeepDeleteNotificationTargets.Capture(item.Path);
            if (!DeepDeleteNotificationTargets.ContainsHttpTarget(targets)) return;

            SetStatus(status =>
            {
                status.RemoteTargetsCaptured++;
                status.LastTargetMethod = __originalMethod?.DeclaringType?.FullName + "." +
                                          __originalMethod?.Name;
                status.LastItemPath = item.Path;
                status.LastCapturedTargets = string.Join("\n", targets.Take(16));
                status.LastError = null;
            });

            // Dry Run must never allow Emby's native delete to remove the local STRM while the
            // remote target is deliberately left untouched.
            if (experience.DeepDeleteDryRun)
                Block("Deep Delete Dry Run is enabled. The remote STRM target was detected, so the native Emby delete was blocked.");

            var actingUser = ResolveAuthenticatedUser(__instance) ?? ResolveFallbackAdmin();
            if (actingUser == null)
                Block("Unable to resolve an authenticated/admin user for the deep.delete notification.");

            try
            {
                if (Plugin.NotificationApi == null)
                    Block("NotificationApi is unavailable; deep.delete could not be emitted.");

                // This call reaches Emby's notification system. A configured Webhook notifier can
                // then receive Event=deep.delete and the raw STRM URL in Description -> Mount Paths.
                Plugin.NotificationApi.DeepDeleteSendNotification(item, actingUser,
                    new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase));

                Increment(status => status.WebhookNotificationsAccepted++);
                Plugin.Instance?.Logger?.Info(
                    "Native delete emitted deep.delete before local STRM removal: item={0}, targets={1}",
                    item.Path, string.Join(" | ", targets.Take(4)));
            }
            catch (Exception ex)
            {
                Block("deep.delete notification dispatch failed: " + ex.GetBaseException().Message);
            }
        }

        private static object FindDeleteRequest(object[] args)
        {
            if (args == null) return null;
            foreach (var arg in args)
            {
                if (arg == null) continue;
                try
                {
                    if (!NativeItemDeleteWebhookBridgeEntryPoint.IsDeleteRequestType(arg.GetType())) continue;
                    if (arg.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.Public) != null)
                        return arg;
                }
                catch { }
            }

            // Fallback for runtimes where the request DTO loses its RouteAttribute after proxying.
            return args.FirstOrDefault(arg => arg != null &&
                arg.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.Public) != null);
        }

        private static string ReadRequestId(object request)
        {
            if (request == null) return null;
            try
            {
                return request.GetType().GetProperty("Id",
                    BindingFlags.Instance | BindingFlags.Public)?.GetValue(request)?.ToString();
            }
            catch { return null; }
        }

        private static BaseItem ResolveItem(ILibraryManager libraryManager, string id)
        {
            if (libraryManager == null || string.IsNullOrWhiteSpace(id)) return null;
            if (long.TryParse(id, out var internalId))
            {
                try
                {
                    var byLong = libraryManager.GetItemById(internalId);
                    if (byLong != null) return byLong;
                }
                catch { }
            }

            foreach (var method in libraryManager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
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
                    if (method.Invoke(libraryManager, new[] { argument }) is BaseItem item) return item;
                }
                catch { }
            }
            return null;
        }

        private static User ResolveAuthenticatedUser(object service)
        {
            try
            {
                var request = service?.GetType().GetProperty("Request",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(service) as IRequest;
                if (request == null) return null;
                var authorization = Plugin.Instance?.ApplicationHost?.Resolve<IAuthorizationContext>();
                return authorization?.GetAuthorizationInfo(request)?.User;
            }
            catch { return null; }
        }

        private static User ResolveFallbackAdmin()
        {
            try
            {
                return LibraryApi.AllUsers.Where(pair => pair.Value).Select(pair => pair.Key).FirstOrDefault();
            }
            catch { return null; }
        }

        private static void Block(string error)
        {
            SetStatus(status =>
            {
                status.NativeDeletesBlocked++;
                status.LastError = error;
            });
            throw new InvalidOperationException(
                "StrmAssistant provider-agnostic deep.delete bridge blocked native deletion: " + error);
        }

        private static void Increment(Action<NativeItemDeleteWebhookBridgeStatus> action)
        {
            lock (StatusSync)
            {
                var status = NativeItemDeleteWebhookBridgeState.Status;
                if (status != null) action(status);
            }
        }

        private static void SetStatus(Action<NativeItemDeleteWebhookBridgeStatus> action)
        {
            lock (StatusSync)
            {
                var status = NativeItemDeleteWebhookBridgeState.Status;
                if (status != null) action(status);
            }
        }
    }
}
