using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using StrmAssistant.MediaEnhance;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    public sealed class MediaInfoRepairResult
    {
        public bool Success { get; set; }
        public bool Executed { get; set; }
        public string ItemId { get; set; }
        public MediaInfoIntegrityAssessment Before { get; set; }
        public MediaInfoIntegrityAssessment After { get; set; }
        public string Error { get; set; }
    }

    public sealed class MediaInfoPlaybackDiagnosticsResult
    {
        public bool Success { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public MediaInfoIntegrityAssessment Integrity { get; set; }
        public long StaticMediaSourceMilliseconds { get; set; }
        public int StaticMediaSourceCount { get; set; }
        public bool PlaybackHydrationPatchActive { get; set; }
        public long PlaybackHydrationAttempts { get; set; }
        public long PlaybackHydrationSucceeded { get; set; }
        public string ProbeRiskReason { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/MediaInfo/{Id}/Integrity", "GET",
        Summary = "Inspect persisted MediaInfo integrity without probing the media")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetMediaInfoIntegrity : IReturn<MediaInfoIntegrityAssessment>
    {
        public string Id { get; set; }
    }

    [Route("/StrmAssistant/MediaInfo/{Id}/Repair", "POST",
        Summary = "Restore core MediaInfo from a validated local persistence snapshot")]
    [Authenticated(Roles = "Admin")]
    public sealed class RepairMediaInfoIntegrity : IReturn<MediaInfoRepairResult>
    {
        public string Id { get; set; }
        public bool Confirm { get; set; }
    }

    [Route("/StrmAssistant/MediaInfo/{Id}/PlaybackDiagnostics", "GET",
        Summary = "Measure static playback-source construction without running ffprobe")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetMediaInfoPlaybackDiagnostics : IReturn<MediaInfoPlaybackDiagnosticsResult>
    {
        public string Id { get; set; }
    }

    public sealed class MediaInfoIntegrityApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;

        public MediaInfoIntegrityApiService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public object Get(GetMediaInfoIntegrity request)
        {
            var item = Resolve(request?.Id);
            return item == null
                ? new MediaInfoIntegrityAssessment { ItemId = request?.Id, RecommendedAction = "Item was not found." }
                : MediaInfoIntegrityService.Assess(item);
        }

        public Task<object> Post(RepairMediaInfoIntegrity request)
        {
            var item = Resolve(request?.Id);
            var result = new MediaInfoRepairResult
            {
                ItemId = request?.Id,
                Before = item == null ? null : MediaInfoIntegrityService.Assess(item)
            };

            if (item == null)
            {
                result.Error = "Item was not found.";
                return Task.FromResult<object>(result);
            }
            if (request?.Confirm != true)
            {
                result.Error = "Confirm=true is required before restoring persisted MediaInfo.";
                return Task.FromResult<object>(result);
            }
            if (!result.Before.PersistenceEnabled)
            {
                result.Error = "MediaInfo persistence is disabled for this item.";
                return Task.FromResult<object>(result);
            }
            if (!result.Before.LibraryInScope)
            {
                result.Error = "Item is outside the MediaInfo extraction scope.";
                return Task.FromResult<object>(result);
            }
            if (!result.Before.Recoverable && !result.Before.CoreMediaInfoComplete)
            {
                result.Error = "No validated primary/backup snapshot is available for recovery.";
                return Task.FromResult<object>(result);
            }

            result.Executed = !result.Before.CoreMediaInfoComplete;
            result.Success = result.Before.CoreMediaInfoComplete ||
                             MediaInfoIntegrityService.HydrateCore(item, "Admin IntegrityRepair");
            result.After = MediaInfoIntegrityService.Assess(Resolve(request.Id));
            if (!result.Success && string.IsNullOrWhiteSpace(result.Error))
                result.Error = "Persisted snapshot could not restore complete core MediaInfo.";
            return Task.FromResult<object>(result);
        }

        public object Get(GetMediaInfoPlaybackDiagnostics request)
        {
            var item = Resolve(request?.Id);
            var result = new MediaInfoPlaybackDiagnosticsResult
            {
                ItemId = request?.Id,
                ItemName = item?.Name
            };
            if (item == null)
            {
                result.Warnings.Add("Item was not found.");
                return result;
            }

            result.Integrity = MediaInfoIntegrityService.Assess(item);
            try
            {
                var watch = Stopwatch.StartNew();
                var sources = Plugin.MediaInfoApi.GetStaticMediaSources(item, false);
                watch.Stop();
                result.StaticMediaSourceMilliseconds = watch.ElapsedMilliseconds;
                result.StaticMediaSourceCount = sources?.Count ?? 0;
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Static media source inspection failed: " + ex.Message);
            }

            var hydration = MediaInfoPlaybackHydrationState.Status;
            result.PlaybackHydrationPatchActive = hydration?.Patched == true;
            result.PlaybackHydrationAttempts = hydration?.HydrationAttempts ?? 0;
            result.PlaybackHydrationSucceeded = hydration?.HydrationSucceeded ?? 0;

            if (result.Integrity.CoreMediaInfoComplete)
                result.ProbeRiskReason = "Core MediaInfo is already present.";
            else if (result.Integrity.Recoverable)
                result.ProbeRiskReason = result.PlaybackHydrationPatchActive
                    ? "Core MediaInfo is missing but a validated local snapshot can be pre-hydrated before playback."
                    : "Core MediaInfo is missing and playback pre-hydration is not active; Emby may probe the source.";
            else
                result.ProbeRiskReason = "Core MediaInfo and valid persisted snapshots are both missing; an Emby media probe may be required.";

            if (result.StaticMediaSourceMilliseconds > 500)
                result.Warnings.Add("Building static media sources exceeded 500 ms even without an explicit probe; inspect path/mount latency.");
            return result;
        }

        private BaseItem Resolve(string id)
        {
            return long.TryParse(id, out var internalId) ? _libraryManager.GetItemById(internalId) : null;
        }
    }
}
