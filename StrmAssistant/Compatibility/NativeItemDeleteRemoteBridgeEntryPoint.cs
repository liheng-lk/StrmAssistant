using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Services;
using StrmAssistant.Common;
using StrmAssistant.Experience;
using StrmAssistant.MediaEnhance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace StrmAssistant.Compatibility
{
    public sealed class NativeItemDeleteRemoteBridgeStatus
    {
        public int ExplicitDeleteTargetsPatched { get; set; }
        public long NativeDeleteRequestsObserved { get; set; }
        public long RemoteDeletePlansApplicable { get; set; }
        public long RemoteDeletesSucceeded { get; set; }
        public long RemoteDeletesBlocked { get; set; }
        public string LastTargetMethod { get; set; }
        public string LastItemPath { get; set; }
        public string LastRemotePath { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class NativeItemDeleteRemoteBridgeState
    {
        public static NativeItemDeleteRemoteBridgeStatus Status { get; internal set; } =
            new NativeItemDeleteRemoteBridgeStatus();
    }

    public sealed class NativeItemDeleteRemoteBridgeEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.native-delete-remote-bridge";
        private Harmony _harmony;

        public void Run()
        {
            var status = new NativeItemDeleteRemoteBridgeStatus();
            NativeItemDeleteRemoteBridgeState.Status = status;

            try
            {
                TryLoad("Emby.Api");
                TryLoad("MediaBrowser.Api");

                var targets = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(SafeGetTypes)
                    .Where(type => type != null &&
                                   type.Assembly != typeof(NativeItemDeleteRemoteBridgeEntryPoint).Assembly &&
                                   type.Name.IndexOf("LibraryService", StringComparison.OrdinalIgnoreCase) >= 0)
                    .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    .Where(IsExplicitSingleItemDeleteMethod)
                    .Distinct()
                    .ToArray();

                _harmony = new Harmony(HarmonyId);
                var prefix = new HarmonyMethod(typeof(NativeItemDeleteRemoteBridgePatches).GetMethod(
                    nameof(NativeItemDeleteRemoteBridgePatches.Prefix), BindingFlags.Public | BindingFlags.Static));

                foreach (var target in targets)
                {
                    try
                    {
                        _harmony.Patch(target, prefix: prefix);
                        status.ExplicitDeleteTargetsPatched++;
                        status.LastTargetMethod = target.DeclaringType?.FullName + "." + target.Name;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Instance?.Logger?.Warn("Native delete bridge could not patch {0}: {1}", target, ex.Message);
                    }
                }

                if (status.ExplicitDeleteTargetsPatched == 0)
                    status.Error = "No explicit single-item Emby LibraryService delete route was discovered at runtime.";
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Error("Native item-delete remote bridge initialization failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
        }

        private static bool IsExplicitSingleItemDeleteMethod(MethodInfo method)
        {
            if (method == null) return false;
            var parameters = method.GetParameters();
            if (parameters.Length != 1) return false;
            var requestType = parameters[0].ParameterType;
            if (requestType == null || requestType == typeof(string)) return false;

            foreach (var attribute in requestType.GetCustomAttributes(true))
            {
                var attributeType = attribute?.GetType();
                if (attributeType == null ||
                    !string.Equals(attributeType.Name, "RouteAttribute", StringComparison.Ordinal)) continue;

                var path = ReadStringProperty(attribute, "Path") ?? ReadStringProperty(attribute, "Template");
                var verbs = ReadStringProperty(attribute, "Verbs") ?? ReadStringProperty(attribute, "Verb");
                if (string.IsNullOrWhiteSpace(path)) continue;

                var normalized = "/" + path.Trim().Trim('/');
                var isSingle = string.Equals(normalized, "/Items/{Id}", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(normalized, "/Items/{Id}/Delete", StringComparison.OrdinalIgnoreCase);
                if (!isSingle) continue;

                if (string.IsNullOrWhiteSpace(verbs)) return true;
                if (verbs.IndexOf("DELETE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    verbs.IndexOf("POST", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static string ReadStringProperty(object target, string propertyName)
        {
            try
            {
                return target?.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(target)?.ToString();
            }
            catch
            {
                return null;
            }
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

    public static class NativeItemDeleteRemoteBridgePatches
    {
        private static readonly RemoteDeepDeleteService RemoteService = new RemoteDeepDeleteService();
        private static readonly object StatusSync = new object();
        private static readonly AsyncLocal<int> BridgeDepth = new AsyncLocal<int>();

        public static void Prefix(object __instance, object[] __args, MethodBase __originalMethod)
        {
            if (BridgeDepth.Value > 0) return;
            Increment(status => status.NativeDeleteRequestsObserved++);

            var remoteOptions = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            var experience = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            if (!remoteOptions.Enabled || remoteOptions.Provider == RemoteDeepDeleteProviderType.None ||
                experience?.EnableDeepDelete != true)
                return;

            var request = __args?.FirstOrDefault(arg => arg != null);
            var id = ReadRequestId(request);
            if (string.IsNullOrWhiteSpace(id)) return;

            var libraryManager = Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>();
            var item = ResolveItem(libraryManager, id);
            if (item == null) return;

            SetStatus(status =>
            {
                status.LastTargetMethod = __originalMethod?.DeclaringType?.FullName + "." + __originalMethod?.Name;
                status.LastItemPath = item.Path;
            });

            var plan = RemoteService.BuildPlan(item);
            if (!plan.Applicable)
            {
                if (LooksLikeRemoteTarget(plan?.SourceTarget))
                    Block(plan, plan?.Error ?? "Remote STRM target could not be mapped safely.");
                return;
            }

            Increment(status => status.RemoteDeletePlansApplicable++);
            if (!plan.Allowed)
                Block(plan, plan.Error ?? "Remote deep-delete plan is not allowed.");

            if (experience.DeepDeleteDryRun)
                Block(plan, "Deep Delete Dry Run is enabled. Native deletion was blocked so the local STRM cannot be removed without its remote target.");

            var user = ResolveAuthenticatedUser(__instance);
            if (!CanDeleteItem(user, item, libraryManager))
                Block(plan, "The current authenticated user could not be independently verified as having permission to delete this item.");

            try
            {
                BridgeDepth.Value++;
                var execution = RemoteService.ExecuteAsync(plan, CancellationToken.None).GetAwaiter().GetResult();
                if (execution?.Success != true)
                    Block(plan, execution?.Error ?? "Remote provider deletion failed.");

                CleanupPersistenceSnapshot(item);
                SetStatus(status =>
                {
                    status.RemoteDeletesSucceeded++;
                    status.LastRemotePath = plan.RemotePath;
                    status.LastError = null;
                });
                Plugin.Instance?.Logger?.Info(
                    "Native delete bridge removed remote target before Emby item deletion: {0} -> {1}",
                    item.Path, plan.RemotePath);
            }
            finally
            {
                BridgeDepth.Value = Math.Max(0, BridgeDepth.Value - 1);
            }
        }

        private static string ReadRequestId(object request)
        {
            if (request == null) return null;
            try
            {
                var value = request.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(request);
                return value?.ToString();
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

        private static bool CanDeleteItem(User user, BaseItem item, ILibraryManager libraryManager)
        {
            if (user?.Policy == null || item == null) return false;
            if (user.Policy.IsAdministrator || user.Policy.EnableContentDeletion) return true;

            var allowed = user.Policy.EnableContentDeletionFromFolders ?? Array.Empty<string>();
            if (allowed.Length == 0 || libraryManager == null) return false;

            try
            {
                var folders = libraryManager.GetCollectionFolders(item)?.Where(folder => folder != null).ToArray()
                              ?? Array.Empty<BaseItem>();
                return folders.Any(folder => allowed.Any(value => MatchesFolderId(value, folder)));
            }
            catch { return false; }
        }

        private static bool MatchesFolderId(string value, BaseItem folder)
        {
            if (string.IsNullOrWhiteSpace(value) || folder == null) return false;
            var text = value.Trim();
            if (string.Equals(text, folder.InternalId.ToString(), StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                if (string.Equals(text, folder.Id.ToString(), StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(text, folder.Id.ToString("N"), StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        private static void CleanupPersistenceSnapshot(BaseItem item)
        {
            if (item == null || Plugin.MediaInfoApi == null) return;
            try
            {
                MediaInfoPersistenceReliabilityPatches.ExplicitDeepDeletePrefix();
                try
                {
                    var directoryService = Plugin.MediaInfoApi.GetMediaInfoRefreshOptions().DirectoryService;
                    var primary = MediaInfoApi.GetMediaInfoJsonPath(item);
                    Plugin.MediaInfoApi.DeleteMediaInfoJson(item, directoryService, "Explicit Native Remote Deep Delete");
                    var backup = MediaInfoPersistenceReliabilityPatches.BackupPath(primary);
                    if (!string.IsNullOrWhiteSpace(backup) && System.IO.File.Exists(backup))
                        System.IO.File.Delete(backup);
                    MediaInfoReliabilityShadowStore.Delete(item);
                }
                finally
                {
                    MediaInfoPersistenceReliabilityPatches.ExplicitDeepDeleteFinalizer(null);
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Native delete bridge could not clear MediaInfo persistence/shadow snapshot: " + ex.Message);
            }
        }

        private static bool LooksLikeRemoteTarget(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ||
                   uri.Scheme == "webdav" || uri.Scheme == "webdavs";
        }

        private static void Block(RemoteDeepDeletePlan plan, string error)
        {
            SetStatus(status =>
            {
                status.RemoteDeletesBlocked++;
                status.LastRemotePath = plan?.RemotePath;
                status.LastError = error;
            });
            throw new InvalidOperationException("StrmAssistant remote deep delete blocked native deletion: " + error);
        }

        private static void Increment(Action<NativeItemDeleteRemoteBridgeStatus> action)
        {
            lock (StatusSync)
            {
                var status = NativeItemDeleteRemoteBridgeState.Status;
                if (status != null) action(status);
            }
        }

        private static void SetStatus(Action<NativeItemDeleteRemoteBridgeStatus> action)
        {
            lock (StatusSync)
            {
                var status = NativeItemDeleteRemoteBridgeState.Status;
                if (status != null) action(status);
            }
        }
    }
}
