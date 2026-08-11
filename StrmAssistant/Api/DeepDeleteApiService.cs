using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using StrmAssistant.Experience;
using StrmAssistant.MediaEnhance;
using StrmAssistant.Notification;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    [Route("/StrmAssistant/DeepDelete/{Id}/Plan", "GET", Summary = "Preview a Strm Assistant deep-delete plan")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetDeepDeletePlan : IReturn<DeepDeleteResponse>
    {
        public string Id { get; set; }
    }

    [Route("/StrmAssistant/DeepDelete/{Id}", "DELETE", Summary = "Execute an explicit Strm Assistant deep delete")]
    [Authenticated(Roles = "Admin")]
    public sealed class ExecuteDeepDelete : IReturn<DeepDeleteResponse>
    {
        public string Id { get; set; }
        public bool Confirm { get; set; }
    }

    public sealed class DeepDeleteResponse
    {
        public bool Success { get; set; }
        public bool Executed { get; set; }
        public bool DryRun { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string SourcePath { get; set; }
        public string RemoteProvider { get; set; }
        public string RemotePath { get; set; }
        public bool RemoteDeleteAccepted { get; set; }
        public bool RemoteVerifiedDeleted { get; set; }
        public int RemoteDeleteStatusCode { get; set; }
        public int RemoteVerificationStatusCode { get; set; }
        public string NotificationEventId { get; set; } = StrmAssistantNotificationTypes.DeepDelete;
        public bool NotificationAccepted { get; set; }
        public List<string> NotificationTargets { get; set; } = new List<string>();
        public List<DeepDeleteResponseEntry> Entries { get; set; } = new List<DeepDeleteResponseEntry>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> DeletedPaths { get; set; } = new List<string>();
        public List<string> DeletedDirectories { get; set; } = new List<string>();
        public List<string> SkippedPaths { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    public sealed class DeepDeleteResponseEntry
    {
        public string Path { get; set; }
        public string Kind { get; set; }
        public bool Allowed { get; set; }
        public string Reason { get; set; }
    }

    public sealed class DeepDeleteApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IAuthorizationContext _authorizationContext;
        private readonly DeepDeleteService _deepDeleteService = new DeepDeleteService();
        private readonly RemoteDeepDeleteService _remoteDeepDeleteService = new RemoteDeepDeleteService();

        public DeepDeleteApiService(ILibraryManager libraryManager, IAuthorizationContext authorizationContext)
        {
            _libraryManager = libraryManager;
            _authorizationContext = authorizationContext;
        }

        public object Get(GetDeepDeletePlan request)
        {
            var item = ResolveItem(request?.Id);
            if (item == null) return ErrorResponse(request?.Id, "Item was not found.");

            var options = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            if (options == null) return ErrorResponse(request.Id, "Plugin options are unavailable.");

            var remotePlan = _remoteDeepDeleteService.BuildPlan(item);
            var notificationTargets = CaptureNotificationTargets(item, remotePlan);

            DeepDeleteResponse response;
            if (remotePlan.Applicable)
            {
                response = ToRemoteResponse(item, remotePlan, options.DeepDeleteDryRun);
            }
            else
            {
                var plan = _deepDeleteService.BuildPlan(item.Path, options);
                response = ToResponse(item, plan, options.DeepDeleteDryRun);
                if (DeepDeleteNotificationTargets.ContainsHttpTarget(notificationTargets))
                {
                    response.Warnings.RemoveAll(value =>
                        value?.IndexOf("Remote STRM target is not deletable", StringComparison.OrdinalIgnoreCase) >= 0);
                    response.Warnings.Add("Remote STRM target will be delegated through the provider-agnostic deep.delete notification; the plugin will not delete that remote URL directly.");
                }
            }

            AttachNotificationPreview(response, notificationTargets);
            return response;
        }

        public async Task<object> Delete(ExecuteDeepDelete request)
        {
            var item = ResolveItem(request?.Id);
            if (item == null) return ErrorResponse(request?.Id, "Item was not found.");

            var options = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            if (options == null || !options.EnableDeepDelete)
                return ErrorResponse(request.Id, "Deep delete is disabled in plugin options.");

            // Capture before any provider/local/Emby delete. Once Emby removes the .strm file the
            // external webhook consumer would otherwise lose the original direct-link target.
            var remotePlan = _remoteDeepDeleteService.BuildPlan(item);
            var notificationTargets = CaptureNotificationTargets(item, remotePlan);

            if (remotePlan.Applicable)
                return await ExecuteRemoteAsync(item, request, options.DeepDeleteDryRun, remotePlan, notificationTargets)
                    .ConfigureAwait(false);

            return ExecuteLocal(item, request, options, notificationTargets);
        }

        private async Task<DeepDeleteResponse> ExecuteRemoteAsync(BaseItem item, ExecuteDeepDelete request,
            bool dryRun, RemoteDeepDeletePlan remotePlan, HashSet<string> notificationTargets)
        {
            var response = ToRemoteResponse(item, remotePlan, dryRun);
            AttachNotificationPreview(response, notificationTargets);

            if (request?.Confirm != true)
            {
                response.Warnings.Add("Execution was not confirmed. Set Confirm=true after reviewing the remote plan.");
                return response;
            }
            if (dryRun)
            {
                response.Warnings.Add("Dry Run is enabled. No remote object, STRM file, Emby item, or deep.delete notification was executed.");
                return response;
            }
            if (!remotePlan.Allowed)
            {
                if (!string.IsNullOrWhiteSpace(remotePlan.Error) && !response.Errors.Contains(remotePlan.Error))
                    response.Errors.Add(remotePlan.Error);
                return response;
            }

            var execution = await _remoteDeepDeleteService.ExecuteAsync(remotePlan, CancellationToken.None)
                .ConfigureAwait(false);
            response.RemoteDeleteAccepted = execution.DeleteAccepted;
            response.RemoteVerifiedDeleted = execution.VerifiedDeleted;
            response.RemoteDeleteStatusCode = execution.HttpStatusCode;
            response.RemoteVerificationStatusCode = execution.VerificationStatusCode;

            if (!execution.Success)
            {
                response.Errors.Add(execution.Error ?? "Remote provider deletion failed or could not be verified.");
                if (!string.IsNullOrWhiteSpace(execution.VerificationError))
                    response.Errors.Add("Verification: " + execution.VerificationError);
                response.Errors.Add("Local STRM and Emby item were preserved because verified remote deletion did not complete.");
                return response;
            }

            response.DeletedPaths.Add(remotePlan.RemotePath);

            // deep.delete is also emitted for direct providers so existing Emby webhook automation
            // keeps observing the same operation. It contains both the raw STRM target and any
            // provider-specific mapped path captured above.
            if (!Notify(item, notificationTargets, response))
            {
                response.Errors.Add("Remote target was deleted, but deep.delete could not be accepted by the Emby notification system. The local STRM/Emby item was preserved for recovery/retry.");
                return response;
            }

            if (!DeleteEmbyItem(item, response)) return response;
            CleanupPersistenceSnapshot(item, response);

            response.Executed = true;
            response.Success = true;
            return response;
        }

        private DeepDeleteResponse ExecuteLocal(BaseItem item, ExecuteDeepDelete request,
            StrmAssistant.Options.ExperienceEnhanceOptions options, HashSet<string> notificationTargets)
        {
            var plan = _deepDeleteService.BuildPlan(item.Path, options);
            var response = ToResponse(item, plan, options.DeepDeleteDryRun);
            AttachNotificationPreview(response, notificationTargets);

            var delegatedExternalTarget = DeepDeleteNotificationTargets.ContainsHttpTarget(notificationTargets);
            if (delegatedExternalTarget)
            {
                response.Warnings.RemoveAll(value =>
                    value?.IndexOf("Remote STRM target is not deletable", StringComparison.OrdinalIgnoreCase) >= 0);
                response.Warnings.Add("The HTTP/HTTPS STRM target is not deleted by the local executor. It is sent unchanged in deep.delete -> Mount Paths for external webhook automation.");
            }

            if (request == null || !request.Confirm)
            {
                response.Warnings.Add("Execution was not confirmed. Set Confirm=true after reviewing the plan.");
                return response;
            }
            if (options.DeepDeleteDryRun)
            {
                response.Warnings.Add("Dry Run is enabled. No files, Emby items, or deep.delete notification were executed.");
                return response;
            }
            if (plan.HasBlockedEntries)
            {
                response.Errors.Add("The plan contains paths outside the configured allowed roots. Execution was aborted.");
                return response;
            }
            if (options.DeepDeleteTargetFile && !plan.HasResolvedMediaTarget && !delegatedExternalTarget)
            {
                response.Errors.Add("A local STRM or symbolic-link media target could not be resolved. Execution was aborted.");
                return response;
            }

            var execution = _deepDeleteService.Execute(plan, options);
            response.DeletedPaths.AddRange(execution.DeletedPaths);
            response.DeletedDirectories.AddRange(execution.DeletedDirectories);
            response.SkippedPaths.AddRange(execution.SkippedPaths);
            response.Errors.AddRange(execution.Errors);
            if (execution.Errors.Count > 0)
            {
                response.Errors.Add("The target deletion did not complete successfully. The Emby item was preserved.");
                return response;
            }

            if (!Notify(item, notificationTargets, response))
            {
                response.Errors.Add("deep.delete could not be accepted by the Emby notification system. The local STRM/Emby item was preserved so the operation can be retried.");
                return response;
            }

            if (!DeleteEmbyItem(item, response)) return response;
            CleanupPersistenceSnapshot(item, response);

            response.Executed = true;
            response.Success = true;
            return response;
        }

        private bool DeleteEmbyItem(BaseItem item, DeepDeleteResponse response)
        {
            try
            {
                _libraryManager.DeleteItem(item, new DeleteOptions
                {
                    DeleteFileLocation = true,
                    DeleteFromExternalProvider = true
                });
                return true;
            }
            catch (Exception ex)
            {
                response.Errors.Add("The external operation was accepted, but Emby item deletion failed: " + ex.Message);
                return false;
            }
        }

        private static void CleanupPersistenceSnapshot(BaseItem item, DeepDeleteResponse response)
        {
            try
            {
                MediaInfoPersistenceReliabilityPatches.ExplicitDeepDeletePrefix();
                try
                {
                    var directoryService = Plugin.MediaInfoApi.GetMediaInfoRefreshOptions().DirectoryService;
                    var primary = Common.MediaInfoApi.GetMediaInfoJsonPath(item);
                    Plugin.MediaInfoApi.DeleteMediaInfoJson(item, directoryService, "Explicit Deep Delete");
                    var backup = MediaInfoPersistenceReliabilityPatches.BackupPath(primary);
                    if (!string.IsNullOrWhiteSpace(backup) && File.Exists(backup)) File.Delete(backup);
                    MediaInfoReliabilityShadowStore.Delete(item);
                }
                finally
                {
                    MediaInfoPersistenceReliabilityPatches.ExplicitDeepDeleteFinalizer(null);
                }
            }
            catch (Exception ex)
            {
                response.Warnings.Add("MediaInfo persistence/shadow cleanup failed: " + ex.Message);
            }
        }

        private bool Notify(BaseItem item, HashSet<string> targetPaths, DeepDeleteResponse response)
        {
            try
            {
                var actingUser = _authorizationContext.GetAuthorizationInfo(Request)?.User;
                if (actingUser == null)
                {
                    actingUser = Common.LibraryApi.AllUsers
                        .Where(kvp => kvp.Value)
                        .Select(kvp => kvp.Key)
                        .FirstOrDefault();
                }

                if (actingUser == null)
                {
                    response.Warnings.Add("deep.delete notification was not sent because no acting/admin user could be resolved.");
                    return false;
                }
                if (Plugin.NotificationApi == null)
                {
                    response.Warnings.Add("deep.delete notification was not sent because NotificationApi is unavailable.");
                    return false;
                }

                Plugin.NotificationApi.DeepDeleteSendNotification(item, actingUser,
                    new HashSet<string>(targetPaths ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase));
                response.NotificationAccepted = true;
                return true;
            }
            catch (Exception ex)
            {
                response.Warnings.Add("deep.delete notification dispatch failed: " + ex.Message);
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Deep Delete notification failed: " + ex.Message);
                return false;
            }
        }

        private static HashSet<string> CaptureNotificationTargets(BaseItem item, RemoteDeepDeletePlan remotePlan)
        {
            var extras = new[]
            {
                remotePlan?.SourceTarget,
                remotePlan?.RemotePath
            };
            return DeepDeleteNotificationTargets.Capture(item?.Path, extras);
        }

        private static void AttachNotificationPreview(DeepDeleteResponse response, HashSet<string> notificationTargets)
        {
            if (response == null) return;

            response.NotificationEventId = StrmAssistantNotificationTypes.DeepDelete;
            response.NotificationTargets = (notificationTargets ?? new HashSet<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var target in response.NotificationTargets)
            {
                if (response.Entries.Any(entry => string.Equals(entry.Path, target, StringComparison.OrdinalIgnoreCase)))
                    continue;

                response.Entries.Add(new DeepDeleteResponseEntry
                {
                    Path = target,
                    Kind = "WebhookTarget",
                    Allowed = true,
                    Reason = "Captured before STRM deletion and emitted unchanged in deep.delete -> Mount Paths. The external webhook consumer decides how/if to delete it."
                });
            }
        }

        private BaseItem ResolveItem(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !long.TryParse(id, out var internalId)) return null;
            return _libraryManager.GetItemById(internalId);
        }

        private static DeepDeleteResponse ToRemoteResponse(BaseItem item, RemoteDeepDeletePlan plan, bool dryRun)
        {
            var response = new DeepDeleteResponse
            {
                Success = plan != null && plan.Allowed,
                Executed = false,
                DryRun = dryRun,
                ItemId = item?.InternalId.ToString(),
                ItemName = item?.Name,
                SourcePath = item?.Path,
                RemoteProvider = plan?.Provider,
                RemotePath = plan?.RemotePath,
                Warnings = plan?.Warnings?.ToList() ?? new List<string>()
            };
            if (plan != null)
            {
                response.Entries.Add(new DeepDeleteResponseEntry
                {
                    Path = plan.RemotePath ?? plan.SourceTarget,
                    Kind = "Remote" + (plan.Provider ?? "Target"),
                    Allowed = plan.Allowed,
                    Reason = plan.Allowed
                        ? "Mapped remote target is inside an explicitly allowed remote root; execution also requires post-delete verification."
                        : plan.Error
                });
                if (!string.IsNullOrWhiteSpace(item?.Path))
                {
                    response.Entries.Add(new DeepDeleteResponseEntry
                    {
                        Path = item.Path,
                        Kind = item.IsShortcut ? "StrmFile" : "EmbySource",
                        Allowed = true,
                        Reason = "Removed only after the remote provider deletion and deep.delete dispatch have completed."
                    });
                }
                if (!plan.Allowed && !string.IsNullOrWhiteSpace(plan.Error)) response.Errors.Add(plan.Error);
            }
            return response;
        }

        private static DeepDeleteResponse ToResponse(BaseItem item, DeepDeletePlan plan, bool dryRun)
        {
            return new DeepDeleteResponse
            {
                Success = plan != null && !plan.HasBlockedEntries,
                Executed = false,
                DryRun = dryRun,
                ItemId = item.InternalId.ToString(),
                ItemName = item.Name,
                SourcePath = item.Path,
                Entries = plan?.Entries.Select(entry => new DeepDeleteResponseEntry
                {
                    Path = entry.Path,
                    Kind = entry.Kind.ToString(),
                    Allowed = entry.Allowed,
                    Reason = entry.Reason
                }).ToList() ?? new List<DeepDeleteResponseEntry>(),
                Warnings = plan?.Warnings.ToList() ?? new List<string>()
            };
        }

        private static DeepDeleteResponse ErrorResponse(string id, string message)
        {
            return new DeepDeleteResponse
            {
                Success = false,
                Executed = false,
                ItemId = id,
                Errors = new List<string> { message }
            };
        }
    }
}
