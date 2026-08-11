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
using System.Threading;
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
        public int PlaybackHydrationPatchedTargets { get; set; }
        public long PlaybackHydrationAttempts { get; set; }
        public long PlaybackHydrationSucceeded { get; set; }
        public long ShadowCaptures { get; set; }
        public long ShadowRestores { get; set; }
        public long ExternalTrackWritesBlocked { get; set; }
        public string ProbeRiskReason { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/MediaInfo/{Id}/Integrity", "GET",
        Summary = "Inspect persisted and shadow MediaInfo integrity without probing the media")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetMediaInfoIntegrity : IReturn<MediaInfoIntegrityAssessment>
    {
        public string Id { get; set; }
    }

    [Route("/StrmAssistant/MediaInfo/{Id}/Repair", "POST",
        Summary = "Restore core MediaInfo from a validated local persistence or STRM shadow snapshot")]
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

        public async Task<object> Post(RepairMediaInfoIntegrity request)
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
                return result;
            }
            if (request?.Confirm != true)
            {
                result.Error = "Confirm=true is required before restoring MediaInfo.";
                return result;
            }
            if (result.Before.CoreMediaInfoComplete)
            {
                result.Success = true;
                result.After = result.Before;
                return result;
            }
            if (!result.Before.Recoverable)
            {
                result.Error = "No validated persisted or STRM shadow snapshot is available for recovery.";
                result.After = result.Before;
                return result;
            }

            result.Executed = true;
            result.Success = await MediaInfoIntegrityMonitor
                .RecoverAsync(item, "Admin IntegrityRepair", CancellationToken.None)
                .ConfigureAwait(false);
            result.After = MediaInfoIntegrityService.Assess(Resolve(request.Id));
            if (!result.Success && string.IsNullOrWhiteSpace(result.Error))
                result.Error = "Validated local recovery sources could not restore complete core MediaInfo.";
            return result;
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

            var hydration = MediaInfoPreReadGuardState.Status;
            result.PlaybackHydrationPatchedTargets = hydration?.MediaSourceTargetsPatched ?? 0;
            result.PlaybackHydrationPatchActive = result.PlaybackHydrationPatchedTargets > 0;
            result.PlaybackHydrationAttempts = hydration?.PreReadRestoreAttempts ?? 0;
            result.PlaybackHydrationSucceeded = hydration?.PreReadRestoreSucceeded ?? 0;
            result.ShadowCaptures = hydration?.ShadowCaptures ?? 0;
            result.ShadowRestores = hydration?.ShadowRestores ?? 0;
            result.ExternalTrackWritesBlocked = hydration?.ExternalTrackWritesBlocked ?? 0;

            if (result.Integrity.CoreMediaInfoComplete)
                result.ProbeRiskReason = "Core MediaInfo is already present.";
            else if (result.Integrity.Recoverable)
                result.ProbeRiskReason = result.PlaybackHydrationPatchActive
                    ? "Core MediaInfo is missing but a validated local snapshot can be pre-hydrated before playback."
                    : "Core MediaInfo is missing and playback pre-hydration is not active; Emby may probe the source.";
            else
                result.ProbeRiskReason = "Core MediaInfo and valid local recovery snapshots are both missing; one media probe is required before a shadow can be seeded.";

            if (result.StaticMediaSourceMilliseconds > 500)
                result.Warnings.Add("Building static media sources exceeded 500 ms even without an explicit probe; inspect STRM resolution/mount latency.");
            return result;
        }

        private BaseItem Resolve(string id)
        {
            return long.TryParse(id, out var internalId) ? _libraryManager.GetItemById(internalId) : null;
        }
    }
}
