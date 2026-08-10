using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.IO;
using StrmAssistant.Api;
using StrmAssistant.Experience;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

namespace StrmAssistant.Compatibility
{
    public sealed class RemoteDeepDeleteCapabilityStatus
    {
        public bool PlanTargetFound { get; set; }
        public bool ExecuteTargetFound { get; set; }
        public bool Patched { get; set; }
        public long RemotePlansHandled { get; set; }
        public long RemoteDeletesSucceeded { get; set; }
        public long RemoteDeletesFailed { get; set; }
        public string LastProvider { get; set; }
        public string LastRemotePath { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class RemoteDeepDeleteModState
    {
        public static RemoteDeepDeleteCapabilityStatus Status { get; internal set; } =
            new RemoteDeepDeleteCapabilityStatus();
    }

    public sealed class RemoteDeepDeleteRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.remote-deep-delete";
        private Harmony _harmony;

        public void Run()
        {
            var status = new RemoteDeepDeleteCapabilityStatus();
            RemoteDeepDeleteModState.Status = status;
            try
            {
                var planTarget = typeof(DeepDeleteApiService).GetMethod("Get",
                    BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(GetDeepDeletePlan) }, null);
                var executeTarget = typeof(DeepDeleteApiService).GetMethod("Delete",
                    BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(ExecuteDeepDelete) }, null);
                status.PlanTargetFound = planTarget != null;
                status.ExecuteTargetFound = executeTarget != null;

                _harmony = new Harmony(HarmonyId);
                if (planTarget != null)
                {
                    _harmony.Patch(planTarget, prefix: new HarmonyMethod(
                        typeof(RemoteDeepDeletePatches).GetMethod(nameof(RemoteDeepDeletePatches.PlanPrefix),
                            BindingFlags.Public | BindingFlags.Static)));
                }
                if (executeTarget != null)
                {
                    _harmony.Patch(executeTarget, prefix: new HarmonyMethod(
                        typeof(RemoteDeepDeletePatches).GetMethod(nameof(RemoteDeepDeletePatches.ExecutePrefix),
                            BindingFlags.Public | BindingFlags.Static)));
                }
                status.Patched = planTarget != null && executeTarget != null;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Error("Remote Deep Delete patch failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
        }
    }

    public static class RemoteDeepDeletePatches
    {
        private static readonly RemoteDeepDeleteService Service = new RemoteDeepDeleteService();
        private static readonly object StatusSync = new object();

        public static bool PlanPrefix(GetDeepDeletePlan request, ref object __result)
        {
            try
            {
                var item = ResolveItem(request?.Id);
                if (item == null) return true;
                var plan = Service.BuildPlan(item);
                if (!plan.Applicable) return true;

                IncrementPlan(plan);
                __result = BuildResponse(item, plan,
                    Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions?.DeepDeleteDryRun == true);
                return false;
            }
            catch (Exception ex)
            {
                RecordFailure(null, ex.Message);
                return true;
            }
        }

        public static bool ExecutePrefix(DeepDeleteApiService __instance, ExecuteDeepDelete request, ref object __result)
        {
            var item = ResolveItem(request?.Id);
            if (item == null) return true;

            var remotePlan = Service.BuildPlan(item);
            if (!remotePlan.Applicable) return true;

            IncrementPlan(remotePlan);
            var experience = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            var response = BuildResponse(item, remotePlan, experience?.DeepDeleteDryRun == true);

            if (experience == null || !experience.EnableDeepDelete)
            {
                response.Errors.Add("Deep delete is disabled in plugin options.");
                __result = response;
                return false;
            }

            if (request?.Confirm != true)
            {
                response.Warnings.Add("Execution was not confirmed. Set Confirm=true after reviewing the remote plan.");
                __result = response;
                return false;
            }

            if (experience.DeepDeleteDryRun)
            {
                response.Warnings.Add("Dry Run is enabled. No remote object or Emby item was deleted.");
                __result = response;
                return false;
            }

            if (!remotePlan.Allowed)
            {
                response.Errors.Add(remotePlan.Error ?? "Remote deletion is not allowed by the configured mapping/root policy.");
                __result = response;
                return false;
            }

            RemoteDeepDeleteExecutionResult execution;
            try
            {
                execution = Service.ExecuteAsync(remotePlan, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                execution = new RemoteDeepDeleteExecutionResult
                {
                    Success = false,
                    Provider = remotePlan.Provider,
                    RemotePath = remotePlan.RemotePath,
                    Error = ex.GetBaseException().Message
                };
            }

            if (!execution.Success)
            {
                response.Errors.Add(execution.Error ?? "Remote provider deletion failed.");
                response.Errors.Add("The Emby item and local STRM were preserved because remote deletion did not succeed.");
                RecordFailure(remotePlan, execution.Error);
                __result = response;
                return false;
            }

            response.DeletedPaths.Add(remotePlan.RemotePath);
            if (execution.AlreadyMissing)
                response.Warnings.Add("Remote provider reported the target as already missing; cleanup continues idempotently.");

            // Explicit destructive action: remove primary + backup persistence snapshots so a future
            // file with the same STRM name cannot inherit stale MediaInfo.
            CleanupPersistenceSnapshot(item, response);

            try
            {
                var libraryManager = Plugin.Instance.ApplicationHost.Resolve<ILibraryManager>();
                libraryManager.DeleteItem(item, new DeleteOptions
                {
                    DeleteFileLocation = true,
                    DeleteFromExternalProvider = true
                });
            }
            catch (Exception ex)
            {
                response.Errors.Add("Remote object was deleted, but Emby item deletion failed: " + ex.Message);
                RecordFailure(remotePlan, ex.Message);
                __result = response;
                return false;
            }

            response.Executed = true;
            response.Success = true;
            Notify(__instance, item, remotePlan.RemotePath);
            RecordSuccess(remotePlan);
            __result = response;
            return false;
        }

        private static BaseItem ResolveItem(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !long.TryParse(id, out var internalId)) return null;
            try
            {
                var manager = Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>();
                return manager?.GetItemById(internalId);
            }
            catch { return null; }
        }

        private static DeepDeleteResponse BuildResponse(BaseItem item, RemoteDeepDeletePlan plan, bool dryRun)
        {
            var response = new DeepDeleteResponse
            {
                Success = plan != null && plan.Allowed,
                Executed = false,
                DryRun = dryRun,
                ItemId = item?.InternalId.ToString(),
                ItemName = item?.Name,
                SourcePath = item?.Path
            };

            if (plan != null)
            {
                response.Entries.Add(new DeepDeleteResponseEntry
                {
                    Path = plan.RemotePath ?? plan.SourceTarget,
                    Kind = "Remote" + (plan.Provider ?? "Target"),
                    Allowed = plan.Allowed,
                    Reason = plan.Allowed
                        ? "Mapped remote target within an explicitly allowed root."
                        : plan.Error
                });
                if (!string.IsNullOrWhiteSpace(item?.Path))
                {
                    response.Entries.Add(new DeepDeleteResponseEntry
                    {
                        Path = item.Path,
                        Kind = item.IsShortcut ? "StrmFile" : "EmbySource",
                        Allowed = true,
                        Reason = "Removed by Emby only after the remote provider confirms deletion."
                    });
                }
                response.Warnings.AddRange(plan.Warnings);
                if (!plan.Allowed && !string.IsNullOrWhiteSpace(plan.Error)) response.Errors.Add(plan.Error);
            }
            return response;
        }

        private static void CleanupPersistenceSnapshot(BaseItem item, DeepDeleteResponse response)
        {
            try
            {
                MediaInfoPersistenceReliabilityPatches.ExplicitDeepDeletePrefix();
                try
                {
                    var directoryService = Plugin.MediaInfoApi.GetMediaInfoRefreshOptions().DirectoryService;
                    var primary = MediaInfoApi.GetMediaInfoJsonPath(item);
                    Plugin.MediaInfoApi.DeleteMediaInfoJson(item, directoryService, "Explicit Remote Deep Delete");
                    var backup = MediaInfoPersistenceReliabilityPatches.BackupPath(primary);
                    if (!string.IsNullOrWhiteSpace(backup) && File.Exists(backup)) File.Delete(backup);
                }
                finally
                {
                    MediaInfoPersistenceReliabilityPatches.ExplicitDeepDeleteFinalizer(null);
                }
            }
            catch (Exception ex)
            {
                response.Warnings.Add("Remote deletion succeeded but MediaInfo snapshot cleanup failed: " + ex.Message);
            }
        }

        private static void Notify(DeepDeleteApiService service, BaseItem item, string remotePath)
        {
            try
            {
                var field = typeof(DeepDeleteApiService).GetField("_authorizationContext",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var authorization = field?.GetValue(service) as IAuthorizationContext;
                var user = authorization?.GetAuthorizationInfo(service.Request)?.User;
                if (user != null && Plugin.NotificationApi != null)
                {
                    Plugin.NotificationApi.DeepDeleteSendNotification(item, user,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { remotePath });
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Remote Deep Delete notification failed: " + ex.Message);
            }
        }

        private static void IncrementPlan(RemoteDeepDeletePlan plan)
        {
            lock (StatusSync)
            {
                var status = RemoteDeepDeleteModState.Status;
                status.RemotePlansHandled++;
                status.LastProvider = plan?.Provider;
                status.LastRemotePath = plan?.RemotePath;
            }
        }

        private static void RecordSuccess(RemoteDeepDeletePlan plan)
        {
            lock (StatusSync)
            {
                var status = RemoteDeepDeleteModState.Status;
                status.RemoteDeletesSucceeded++;
                status.LastProvider = plan?.Provider;
                status.LastRemotePath = plan?.RemotePath;
                status.LastError = null;
            }
        }

        private static void RecordFailure(RemoteDeepDeletePlan plan, string error)
        {
            lock (StatusSync)
            {
                var status = RemoteDeepDeleteModState.Status;
                status.RemoteDeletesFailed++;
                status.LastProvider = plan?.Provider;
                status.LastRemotePath = plan?.RemotePath;
                status.LastError = error;
            }
        }
    }
}
