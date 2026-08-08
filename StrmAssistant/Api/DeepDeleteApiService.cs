using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Experience;
using System;
using System.Collections.Generic;
using System.Linq;

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

    /// <summary>
    /// Explicit deep-delete API. This intentionally uses a plugin-owned route instead of
    /// intercepting Emby's native DELETE /Items/{Id}; a library scan can never invoke it.
    /// </summary>
    public sealed class DeepDeleteApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IAuthorizationContext _authorizationContext;
        private readonly DeepDeleteService _deepDeleteService = new DeepDeleteService();

        public DeepDeleteApiService(ILibraryManager libraryManager, IAuthorizationContext authorizationContext)
        {
            _libraryManager = libraryManager;
            _authorizationContext = authorizationContext;
        }

        public object Get(GetDeepDeletePlan request)
        {
            var item = ResolveItem(request?.Id);
            if (item == null)
            {
                return ErrorResponse(request?.Id, "Item was not found.");
            }

            var options = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            if (options == null)
            {
                return ErrorResponse(request.Id, "Plugin options are unavailable.");
            }

            var plan = _deepDeleteService.BuildPlan(item.Path, options);
            return ToResponse(item, plan, options.DeepDeleteDryRun);
        }

        public object Delete(ExecuteDeepDelete request)
        {
            var item = ResolveItem(request?.Id);
            if (item == null)
            {
                return ErrorResponse(request?.Id, "Item was not found.");
            }

            var options = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            if (options == null || !options.EnableDeepDelete)
            {
                return ErrorResponse(request.Id, "Deep delete is disabled in plugin options.");
            }

            var plan = _deepDeleteService.BuildPlan(item.Path, options);
            var response = ToResponse(item, plan, options.DeepDeleteDryRun);

            if (request == null || !request.Confirm)
            {
                response.Warnings.Add("Execution was not confirmed. Set Confirm=true after reviewing the plan.");
                return response;
            }

            if (options.DeepDeleteDryRun)
            {
                response.Warnings.Add("Dry Run is enabled. No files or Emby items were deleted.");
                return response;
            }

            if (plan.HasBlockedEntries)
            {
                response.Errors.Add("The plan contains paths outside the configured allowed roots. Execution was aborted.");
                return response;
            }

            if (options.DeepDeleteTargetFile && !plan.Entries.Any(entry => entry.Kind == DeepDeleteEntryKind.StrmTarget))
            {
                response.Errors.Add("A local STRM target could not be resolved. Execution was aborted.");
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

            try
            {
                _libraryManager.DeleteItem(item, new DeleteOptions
                {
                    DeleteFileLocation = true,
                    DeleteFromExternalProvider = true
                });
            }
            catch (Exception ex)
            {
                response.Errors.Add("Emby item deletion failed: " + ex.Message);
                return response;
            }

            response.Executed = true;
            response.Success = true;

            var actingUser = _authorizationContext.GetAuthorizationInfo(Request)?.User;
            if (actingUser != null && Plugin.NotificationApi != null)
            {
                var targetPaths = plan.Entries
                    .Where(entry => entry.Kind == DeepDeleteEntryKind.StrmTarget &&
                                    response.DeletedPaths.Contains(entry.Path, StringComparer.OrdinalIgnoreCase))
                    .Select(entry => entry.Path);

                Plugin.NotificationApi.DeepDeleteSendNotification(
                    item,
                    actingUser,
                    new HashSet<string>(targetPaths, StringComparer.OrdinalIgnoreCase));
            }

            return response;
        }

        private MediaBrowser.Controller.Entities.BaseItem ResolveItem(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            if (!long.TryParse(id, out var internalId)) return null;
            return _libraryManager.GetItemById(internalId);
        }

        private static DeepDeleteResponse ToResponse(MediaBrowser.Controller.Entities.BaseItem item,
            DeepDeletePlan plan, bool dryRun)
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
