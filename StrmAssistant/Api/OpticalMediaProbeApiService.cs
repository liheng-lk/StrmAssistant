using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using StrmAssistant.MediaEnhance;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    [Route("/StrmAssistant/OpticalProbe/Health", "GET", Summary = "Check the configured ISO/BDMV ffprobe executable")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetOpticalProbeHealth : IReturn<OpticalProbeHealthResponse>
    {
    }

    [Route("/StrmAssistant/OpticalProbe/{Id}", "GET", Summary = "Probe an ISO/BDMV item without changing Emby metadata")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetOpticalProbeItem : IReturn<OpticalProbeResponse>
    {
        public string Id { get; set; }
    }

    [Route("/StrmAssistant/OpticalProbe/{Id}/WritebackPlan", "GET", Summary = "Preview ISO/BDMV MediaInfo write-back")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetOpticalWriteBackPlan : IReturn<OpticalWriteBackResponse>
    {
        public string Id { get; set; }
    }

    [Route("/StrmAssistant/OpticalProbe/{Id}/Apply", "POST", Summary = "Apply confirmed ISO/BDMV MediaInfo write-back")]
    [Authenticated(Roles = "Admin")]
    public sealed class ApplyOpticalWriteBack : IReturn<OpticalWriteBackResponse>
    {
        public string Id { get; set; }
        public bool Confirm { get; set; }
    }

    public sealed class OpticalProbeHealthResponse
    {
        public bool Success { get; set; }
        public bool Enabled { get; set; }
        public string Executable { get; set; }
        public string Version { get; set; }
        public bool HasBlurayProtocol { get; set; }
        public string Error { get; set; }
    }

    public sealed class OpticalProbeResponse
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string Kind { get; set; }
        public string SourcePath { get; set; }
        public string ProbeInput { get; set; }
        public string Executable { get; set; }
        public string FormatName { get; set; }
        public long? RunTimeTicks { get; set; }
        public int? BitRate { get; set; }
        public string StandardError { get; set; }
        public BluRayDiscEnrichmentSummary DiscInfo { get; set; }
        public List<OpticalProbeStreamInfo> Streams { get; set; } = new List<OpticalProbeStreamInfo>();
        public List<OpticalProbeChapterInfo> Chapters { get; set; } = new List<OpticalProbeChapterInfo>();
    }

    public sealed class OpticalWriteBackResponse
    {
        public bool Success { get; set; }
        public bool Executed { get; set; }
        public bool WriteBackEnabled { get; set; }
        public bool RolledBack { get; set; }
        public string Error { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string Kind { get; set; }
        public BluRayDiscEnrichmentSummary DiscInfo { get; set; }
        public OpticalWriteBackPlan Plan { get; set; }
        public int SavedStreamCount { get; set; }
        public int SavedChapterCount { get; set; }
    }

    /// <summary>
    /// Admin-only Phase 2 optical-media surface. Probe and plan endpoints are read-only.
    /// Write-back requires both the plugin option and Confirm=true for every individual item.
    /// BDMV folders are additionally enriched with Emby's own Blu-ray examiner before planning/apply.
    /// </summary>
    public sealed class OpticalMediaProbeApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly OpticalMediaProbe _probe;
        private readonly OpticalMediaWriteBack _writeBack;
        private readonly BluRayDiscInfoEnricher _bluRayEnricher;

        public OpticalMediaProbeApiService(ILibraryManager libraryManager, IItemRepository itemRepository,
            IJsonSerializer jsonSerializer, IBlurayExaminer blurayExaminer)
        {
            _libraryManager = libraryManager;
            _probe = new OpticalMediaProbe(jsonSerializer);
            _writeBack = new OpticalMediaWriteBack(libraryManager, itemRepository, jsonSerializer);
            _bluRayEnricher = new BluRayDiscInfoEnricher(blurayExaminer);
        }

        public async Task<object> Get(GetOpticalProbeHealth request)
        {
            var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
            var result = await _probe.CheckHealthAsync(options, CancellationToken.None).ConfigureAwait(false);

            return new OpticalProbeHealthResponse
            {
                Success = result.Success,
                Enabled = options?.EnableOpticalMediaProbe == true,
                Executable = result.Executable,
                Version = result.Version,
                HasBlurayProtocol = result.HasBlurayProtocol,
                Error = result.Error
            };
        }

        public async Task<object> Get(GetOpticalProbeItem request)
        {
            var item = ResolveVideo(request?.Id);
            if (item == null)
            {
                return new OpticalProbeResponse
                {
                    Success = false,
                    ItemId = request?.Id,
                    Error = "Video item was not found."
                };
            }

            var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
            var result = await _probe.ProbeAsync(item, options, CancellationToken.None).ConfigureAwait(false);
            var discInfo = _bluRayEnricher.Enrich(item, result);
            return ToProbeResponse(item, result, discInfo);
        }

        public async Task<object> Get(GetOpticalWriteBackPlan request)
        {
            var item = ResolveVideo(request?.Id);
            if (item == null) return WriteBackError(request?.Id, "Video item was not found.");

            var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
            var probeResult = await _probe.ProbeAsync(item, options, CancellationToken.None).ConfigureAwait(false);
            var discInfo = _bluRayEnricher.Enrich(item, probeResult);
            var plan = _writeBack.BuildPlan(item, probeResult);

            return new OpticalWriteBackResponse
            {
                Success = plan.Valid,
                Executed = false,
                WriteBackEnabled = options?.EnableOpticalMediaWriteBack == true,
                Error = plan.Error,
                ItemId = item.InternalId.ToString(),
                ItemName = item.Name,
                Kind = probeResult.Kind,
                DiscInfo = discInfo,
                Plan = plan
            };
        }

        public async Task<object> Post(ApplyOpticalWriteBack request)
        {
            var item = ResolveVideo(request?.Id);
            if (item == null) return WriteBackError(request?.Id, "Video item was not found.");

            var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
            if (options?.EnableOpticalMediaProbe != true)
                return WriteBackError(request.Id, "ISO / BDMV optical probing is disabled.");
            if (!options.EnableOpticalMediaWriteBack)
                return WriteBackError(request.Id, "ISO / BDMV write-back is disabled in plugin options.");
            if (request == null || !request.Confirm)
                return WriteBackError(item.InternalId.ToString(),
                    "Write-back was not confirmed. Review WritebackPlan first, then submit Confirm=true.");

            var probeResult = await _probe.ProbeAsync(item, options, CancellationToken.None).ConfigureAwait(false);
            var discInfo = _bluRayEnricher.Enrich(item, probeResult);
            var writeResult = _writeBack.Apply(item, probeResult);

            return new OpticalWriteBackResponse
            {
                Success = writeResult.Success,
                Executed = writeResult.Success,
                WriteBackEnabled = true,
                RolledBack = writeResult.RolledBack,
                Error = writeResult.Error,
                ItemId = item.InternalId.ToString(),
                ItemName = item.Name,
                Kind = probeResult.Kind,
                DiscInfo = discInfo,
                Plan = writeResult.Plan,
                SavedStreamCount = writeResult.SavedStreamCount,
                SavedChapterCount = writeResult.SavedChapterCount
            };
        }

        private Video ResolveVideo(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            if (!long.TryParse(id, out var internalId)) return null;
            return _libraryManager.GetItemById(internalId) as Video;
        }

        private static OpticalProbeResponse ToProbeResponse(Video item, OpticalProbeResult result,
            BluRayDiscEnrichmentSummary discInfo)
        {
            return new OpticalProbeResponse
            {
                Success = result.Success,
                Error = result.Error,
                ItemId = item.InternalId.ToString(),
                ItemName = item.Name,
                Kind = result.Kind,
                SourcePath = result.SourcePath,
                ProbeInput = result.ProbeInput,
                Executable = result.Executable,
                FormatName = result.FormatName,
                RunTimeTicks = result.RunTimeTicks,
                BitRate = result.BitRate,
                StandardError = result.StandardError,
                DiscInfo = discInfo,
                Streams = result.Streams,
                Chapters = result.Chapters
            };
        }

        private static OpticalWriteBackResponse WriteBackError(string itemId, string error)
        {
            return new OpticalWriteBackResponse
            {
                Success = false,
                Executed = false,
                ItemId = itemId,
                Error = error
            };
        }
    }
}
