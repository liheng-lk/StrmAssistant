using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;
using StrmAssistant.IntroSkip;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    public sealed class UnifiedIntroDbSettingsStatus
    {
        public UnifiedIntroDbOptions Options { get; set; }
        public string SettingsPath { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class UnifiedIntroDbMarkerView
    {
        public string MarkerType { get; set; }
        public double Seconds { get; set; }
        public string Name { get; set; }
    }

    public class UnifiedIntroDbPreviewResult
    {
        public bool Success { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public UnifiedIntroDbIdentity Identity { get; set; }
        public UnifiedIntroDbDocument Remote { get; set; }
        public List<UnifiedIntroDbMarkerView> ExistingMarkers { get; set; } = new List<UnifiedIntroDbMarkerView>();
        public string Error { get; set; }
    }

    public sealed class UnifiedIntroDbPlanResult : UnifiedIntroDbPreviewResult
    {
        public bool MeetsMinimumConfidence { get; set; }
        public bool ExistingIntroMarkers { get; set; }
        public bool ExistingCreditsMarker { get; set; }
        public bool OverwriteExistingMarkers { get; set; }
        public bool AllowCreditsMarker { get; set; }
        public List<UnifiedIntroDbMarkerView> ProposedMarkers { get; set; } = new List<UnifiedIntroDbMarkerView>();
        public List<UnifiedIntroDbMarkerView> MarkersToRemove { get; set; } = new List<UnifiedIntroDbMarkerView>();
        public List<UnifiedIntroDbMarkerView> MarkersToAdd { get; set; } = new List<UnifiedIntroDbMarkerView>();
        public bool CanApply { get; set; }
    }

    public sealed class UnifiedIntroDbApplyResult
    {
        public bool Success { get; set; }
        public bool Applied { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public int PreviousChapterCount { get; set; }
        public int NewChapterCount { get; set; }
        public List<UnifiedIntroDbMarkerView> AppliedMarkers { get; set; } = new List<UnifiedIntroDbMarkerView>();
        public string Error { get; set; }
    }

    [Route("/StrmAssistant/IntroDb", "GET", Summary = "Get Unified IntroDb settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetUnifiedIntroDbSettings : IReturn<UnifiedIntroDbSettingsStatus> { }

    [Route("/StrmAssistant/IntroDb", "POST", Summary = "Update Unified IntroDb settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class SaveUnifiedIntroDbSettings : IReturn<UnifiedIntroDbSettingsStatus>
    {
        public bool Enabled { get; set; }
        public string EndpointTemplate { get; set; }
        public int TimeoutSeconds { get; set; } = 15;
        public double MinimumConfidence { get; set; } = 0.75;
        public bool AllowCreditsMarker { get; set; } = true;
        public bool OverwriteExistingMarkers { get; set; }
        public bool AutoApplyOnItemAdded { get; set; }
        public int AutoApplyDelaySeconds { get; set; } = 30;
    }

    [Route("/StrmAssistant/IntroDb/{Id}/Preview", "GET", Summary = "Preview Unified IntroDb markers without modifying chapters")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetUnifiedIntroDbPreview : IReturn<UnifiedIntroDbPreviewResult>
    {
        public string Id { get; set; }
    }

    [Route("/StrmAssistant/IntroDb/{Id}/Plan", "GET", Summary = "Plan Unified IntroDb marker changes without modifying chapters")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetUnifiedIntroDbPlan : IReturn<UnifiedIntroDbPlanResult>
    {
        public string Id { get; set; }
        public bool? OverwriteExistingMarkers { get; set; }
    }

    [Route("/StrmAssistant/IntroDb/{Id}/Apply", "POST", Summary = "Apply Unified IntroDb marker changes after explicit confirmation")]
    [Authenticated(Roles = "Admin")]
    public sealed class ApplyUnifiedIntroDb : IReturn<UnifiedIntroDbApplyResult>
    {
        public string Id { get; set; }
        public bool Confirm { get; set; }
        public bool? OverwriteExistingMarkers { get; set; }
    }

    public sealed class UnifiedIntroDbApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly UnifiedIntroDbBridge _bridge;

        public UnifiedIntroDbApiService(ILibraryManager libraryManager, IItemRepository itemRepository,
            MediaBrowser.Common.Net.IHttpClient httpClient, MediaBrowser.Model.Serialization.IJsonSerializer jsonSerializer)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _bridge = new UnifiedIntroDbBridge(httpClient, jsonSerializer);
        }

        public object Get(GetUnifiedIntroDbSettings request) => BuildStatus();

        public object Post(SaveUnifiedIntroDbSettings request)
        {
            UnifiedIntroDbRuntimeSettings.Save(new UnifiedIntroDbOptions
            {
                Enabled = request?.Enabled == true,
                EndpointTemplate = request?.EndpointTemplate,
                TimeoutSeconds = request?.TimeoutSeconds ?? 15,
                MinimumConfidence = request?.MinimumConfidence ?? 0.75,
                AllowCreditsMarker = request?.AllowCreditsMarker != false,
                OverwriteExistingMarkers = request?.OverwriteExistingMarkers == true,
                AutoApplyOnItemAdded = request?.AutoApplyOnItemAdded == true,
                AutoApplyDelaySeconds = request?.AutoApplyDelaySeconds ?? 30
            });
            return BuildStatus();
        }

        public async Task<object> Get(GetUnifiedIntroDbPreview request)
        {
            var episode = ResolveEpisode(request?.Id);
            var result = new UnifiedIntroDbPreviewResult { ItemId = request?.Id };
            if (episode == null)
            {
                result.Error = "Episode was not found.";
                return result;
            }

            result.ItemName = episode.Name;
            result.Identity = _bridge.ResolveIdentity(episode);
            result.ExistingMarkers = GetMarkerViews(episode);
            result.Remote = await _bridge.FetchAsync(episode, CancellationToken.None).ConfigureAwait(false);
            if (result.Remote == null)
            {
                result.Error = "Unified IntroDb did not return a valid marker document.";
                return result;
            }

            result.Success = true;
            return result;
        }

        public async Task<object> Get(GetUnifiedIntroDbPlan request)
        {
            var episode = ResolveEpisode(request?.Id);
            return await BuildPlanAsync(episode, request?.Id, request?.OverwriteExistingMarkers, CancellationToken.None)
                .ConfigureAwait(false);
        }

        public async Task<object> Post(ApplyUnifiedIntroDb request)
        {
            var result = new UnifiedIntroDbApplyResult { ItemId = request?.Id };
            if (request?.Confirm != true)
            {
                result.Error = "Confirm=true is required before marker changes are written.";
                return result;
            }

            var episode = ResolveEpisode(request.Id);
            if (episode == null)
            {
                result.Error = "Episode was not found.";
                return result;
            }

            result.ItemName = episode.Name;
            var plan = await BuildPlanAsync(episode, request.Id, request.OverwriteExistingMarkers, CancellationToken.None)
                .ConfigureAwait(false);
            if (!plan.CanApply)
            {
                result.Error = plan.Error ?? "The marker plan cannot be applied.";
                return result;
            }

            var chapters = _itemRepository.GetChapters(episode) ?? new List<ChapterInfo>();
            result.PreviousChapterCount = chapters.Count;

            var overwrite = plan.OverwriteExistingMarkers;
            var markerTypes = new HashSet<MarkerType> { MarkerType.IntroStart, MarkerType.IntroEnd, MarkerType.CreditsStart };
            if (overwrite)
                chapters.RemoveAll(c => markerTypes.Contains(c.MarkerType));
            else
            {
                var existingTypes = new HashSet<MarkerType>(chapters.Where(c => markerTypes.Contains(c.MarkerType)).Select(c => c.MarkerType));
                plan.MarkersToAdd = plan.MarkersToAdd.Where(v => !existingTypes.Contains(ParseMarkerType(v.MarkerType))).ToList();
            }

            foreach (var marker in plan.MarkersToAdd)
            {
                var type = ParseMarkerType(marker.MarkerType);
                chapters.Add(new ChapterInfo
                {
                    MarkerType = type,
                    StartPositionTicks = TimeSpan.FromSeconds(marker.Seconds).Ticks,
                    Name = marker.Name
                });
            }

            chapters = chapters
                .OrderBy(c => c.StartPositionTicks)
                .ThenBy(c => (int)c.MarkerType)
                .ToList();
            _itemRepository.SaveChapters(episode.InternalId, chapters);

            result.Applied = true;
            result.Success = true;
            result.NewChapterCount = chapters.Count;
            result.AppliedMarkers = plan.MarkersToAdd;
            return result;
        }

        private async Task<UnifiedIntroDbPlanResult> BuildPlanAsync(Episode episode, string itemId,
            bool? overwriteOverride, CancellationToken cancellationToken)
        {
            var options = UnifiedIntroDbRuntimeSettings.GetSnapshot();
            var result = new UnifiedIntroDbPlanResult
            {
                ItemId = itemId,
                OverwriteExistingMarkers = overwriteOverride ?? options.OverwriteExistingMarkers,
                AllowCreditsMarker = options.AllowCreditsMarker
            };
            if (episode == null)
            {
                result.Error = "Episode was not found.";
                return result;
            }

            result.ItemName = episode.Name;
            result.Identity = _bridge.ResolveIdentity(episode);
            result.ExistingMarkers = GetMarkerViews(episode);
            result.ExistingIntroMarkers = result.ExistingMarkers.Any(m => m.MarkerType == MarkerType.IntroStart.ToString() || m.MarkerType == MarkerType.IntroEnd.ToString());
            result.ExistingCreditsMarker = result.ExistingMarkers.Any(m => m.MarkerType == MarkerType.CreditsStart.ToString());
            result.Remote = await _bridge.FetchAsync(episode, cancellationToken).ConfigureAwait(false);
            if (result.Remote == null)
            {
                result.Error = "Unified IntroDb did not return a valid marker document.";
                return result;
            }

            result.Success = true;
            result.MeetsMinimumConfidence = !result.Remote.Confidence.HasValue || result.Remote.Confidence.Value >= options.MinimumConfidence;
            if (!result.MeetsMinimumConfidence)
            {
                result.Error = "Remote marker confidence is below MinimumConfidence.";
                return result;
            }

            result.ProposedMarkers.Add(NewMarker(MarkerType.IntroStart, result.Remote.IntroStartSeconds.Value));
            result.ProposedMarkers.Add(NewMarker(MarkerType.IntroEnd, result.Remote.IntroEndSeconds.Value));
            if (options.AllowCreditsMarker && result.Remote.CreditsStartSeconds.HasValue)
                result.ProposedMarkers.Add(NewMarker(MarkerType.CreditsStart, result.Remote.CreditsStartSeconds.Value));

            var existingMarkerTypes = new HashSet<string>(result.ExistingMarkers.Select(m => m.MarkerType), StringComparer.OrdinalIgnoreCase);
            if (result.OverwriteExistingMarkers)
            {
                result.MarkersToRemove = result.ExistingMarkers
                    .Where(m => m.MarkerType == MarkerType.IntroStart.ToString() ||
                                m.MarkerType == MarkerType.IntroEnd.ToString() ||
                                m.MarkerType == MarkerType.CreditsStart.ToString())
                    .ToList();
                result.MarkersToAdd = result.ProposedMarkers.ToList();
            }
            else
            {
                result.MarkersToAdd = result.ProposedMarkers
                    .Where(m => !existingMarkerTypes.Contains(m.MarkerType))
                    .ToList();
            }

            result.CanApply = result.MarkersToAdd.Count > 0 || result.MarkersToRemove.Count > 0;
            if (!result.CanApply && string.IsNullOrWhiteSpace(result.Error))
                result.Error = "No marker changes are required under the current overwrite policy.";
            return result;
        }

        private List<UnifiedIntroDbMarkerView> GetMarkerViews(Episode episode)
        {
            var markerTypes = new HashSet<MarkerType> { MarkerType.IntroStart, MarkerType.IntroEnd, MarkerType.CreditsStart };
            return (_itemRepository.GetChapters(episode) ?? new List<ChapterInfo>())
                .Where(c => markerTypes.Contains(c.MarkerType))
                .OrderBy(c => c.StartPositionTicks)
                .Select(c => new UnifiedIntroDbMarkerView
                {
                    MarkerType = c.MarkerType.ToString(),
                    Seconds = TimeSpan.FromTicks(c.StartPositionTicks).TotalSeconds,
                    Name = c.Name
                })
                .ToList();
        }

        private static UnifiedIntroDbMarkerView NewMarker(MarkerType markerType, double seconds)
        {
            return new UnifiedIntroDbMarkerView
            {
                MarkerType = markerType.ToString(),
                Seconds = seconds,
                Name = markerType.ToString()
            };
        }

        private static MarkerType ParseMarkerType(string value)
        {
            if (Enum.TryParse(value, true, out MarkerType markerType)) return markerType;
            return MarkerType.Chapter;
        }

        private UnifiedIntroDbSettingsStatus BuildStatus()
        {
            var options = UnifiedIntroDbRuntimeSettings.GetSnapshot();
            var result = new UnifiedIntroDbSettingsStatus
            {
                Options = options,
                SettingsPath = UnifiedIntroDbRuntimeSettings.SettingsPath
            };
            if (options.Enabled && string.IsNullOrWhiteSpace(options.EndpointTemplate))
                result.Warnings.Add("Unified IntroDb is enabled but EndpointTemplate is empty.");
            if (options.Enabled && options.AutoApplyOnItemAdded)
                result.Warnings.Add("Automatic marker write-back is enabled for newly added episodes. Existing markers are preserved unless OverwriteExistingMarkers is enabled.");
            return result;
        }

        private Episode ResolveEpisode(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !long.TryParse(id, out var internalId)) return null;
            return _libraryManager.GetItemById(internalId) as Episode;
        }
    }
}
