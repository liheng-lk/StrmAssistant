using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using StrmAssistant.MediaEnhance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    public sealed class StrmMediaInfoFleetSample
    {
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public bool RecoverableLocally { get; set; }
        public bool ProbeRequired { get; set; }
        public string RecommendedAction { get; set; }
    }

    public sealed class StrmMediaInfoFleetHealthResponse
    {
        public string GeneratedUtc { get; set; }
        public int TotalStrmItems { get; set; }
        public int InScopeItems { get; set; }
        public int CompleteItems { get; set; }
        public int IncompleteItems { get; set; }
        public int LocallyRecoverableItems { get; set; }
        public int ProbeRequiredItems { get; set; }
        public int OutsideScopeItems { get; set; }
        public int BlacklistedItems { get; set; }
        public List<StrmMediaInfoFleetSample> Samples { get; set; } = new List<StrmMediaInfoFleetSample>();
    }

    public sealed class StrmMediaInfoRebuildResponse
    {
        public bool Success { get; set; }
        public bool Confirmed { get; set; }
        public bool AllowRemoteRebuild { get; set; }
        public MediaInfoIntegrityAssessment Before { get; set; }
        public StrmMediaInfoRepairResult Execution { get; set; }
        public MediaInfoIntegrityAssessment After { get; set; }
        public string Error { get; set; }
    }

    [Route("/StrmAssistant/MediaInfo/StrmFleetHealth", "GET",
        Summary = "Inspect STRM MediaInfo health without probing remote media")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetStrmMediaInfoFleetHealth : IReturn<StrmMediaInfoFleetHealthResponse>
    {
        public int SampleLimit { get; set; } = 50;
    }

    [Route("/StrmAssistant/MediaInfo/{Id}/Rebuild", "POST",
        Summary = "Repair STRM MediaInfo locally and optionally run one explicit remote rebuild")]
    [Authenticated(Roles = "Admin")]
    public sealed class RebuildStrmMediaInfo : IReturn<StrmMediaInfoRebuildResponse>
    {
        public string Id { get; set; }
        public bool Confirm { get; set; }
        public bool AllowRemoteRebuild { get; set; }
    }

    public sealed class StrmMediaInfoRepairApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly StrmMediaInfoRepairService _repairService;

        public StrmMediaInfoRepairApiService(ILibraryManager libraryManager, IProviderManager providerManager)
        {
            _libraryManager = libraryManager;
            _repairService = new StrmMediaInfoRepairService(libraryManager, providerManager);
        }

        public object Get(GetStrmMediaInfoFleetHealth request)
        {
            var result = new StrmMediaInfoFleetHealthResponse
            {
                GeneratedUtc = DateTimeOffset.UtcNow.ToString("O")
            };
            var limit = Math.Max(0, Math.Min(200, request?.SampleLimit ?? 50));
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                HasPath = true,
                MediaTypes = new[] { MediaType.Video, MediaType.Audio }
            }) ?? Array.Empty<BaseItem>();

            foreach (var item in items.Where(MediaInfoReliabilityShadowStore.AppliesTo)
                         .GroupBy(value => value.InternalId).Select(group => group.First()))
            {
                result.TotalStrmItems++;
                if (Plugin.LibraryApi?.IsLibraryInScope(item) != true)
                {
                    result.OutsideScopeItems++;
                    continue;
                }
                result.InScopeItems++;

                if (MediaExtractionFilter.ShouldSkip(item, out _))
                {
                    result.BlacklistedItems++;
                    continue;
                }

                if (MediaInfoIntegrityService.IsCoreMediaInfoComplete(item))
                {
                    result.CompleteItems++;
                    continue;
                }

                result.IncompleteItems++;
                var assessment = MediaInfoIntegrityService.Assess(item);
                if (assessment.Recoverable)
                    result.LocallyRecoverableItems++;
                else
                    result.ProbeRequiredItems++;

                if (result.Samples.Count < limit)
                {
                    result.Samples.Add(new StrmMediaInfoFleetSample
                    {
                        ItemId = item.InternalId.ToString(),
                        ItemName = item.Name,
                        RecoverableLocally = assessment.Recoverable,
                        ProbeRequired = !assessment.Recoverable,
                        RecommendedAction = assessment.RecommendedAction
                    });
                }
            }

            return result;
        }

        public async Task<object> Post(RebuildStrmMediaInfo request)
        {
            var item = Resolve(request?.Id);
            var response = new StrmMediaInfoRebuildResponse
            {
                Confirmed = request?.Confirm == true,
                AllowRemoteRebuild = request?.AllowRemoteRebuild == true,
                Before = item == null ? null : MediaInfoIntegrityService.Assess(item)
            };

            if (item == null)
            {
                response.Error = "Item was not found.";
                return response;
            }
            if (request?.Confirm != true)
            {
                response.Error = "Confirm=true is required before writing MediaInfo to the Emby item repository.";
                return response;
            }

            try
            {
                response.Execution = await _repairService.RepairAsync(item, request.AllowRemoteRebuild,
                        "Admin STRM MediaInfo Rebuild", CancellationToken.None)
                    .ConfigureAwait(false);
                response.Success = response.Execution?.Success == true;
                response.Error = response.Success ? null : response.Execution?.Error;
            }
            catch (Exception ex)
            {
                response.Error = ex.GetBaseException().Message;
            }

            response.After = MediaInfoIntegrityService.Assess(Resolve(request.Id) ?? item);
            if (!response.Success && response.After.CoreMediaInfoComplete)
                response.Success = true;
            return response;
        }

        private BaseItem Resolve(string id)
        {
            return long.TryParse(id, out var internalId) ? _libraryManager.GetItemById(internalId) : null;
        }
    }
}
