using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
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
        public List<OpticalProbeStreamInfo> Streams { get; set; } = new List<OpticalProbeStreamInfo>();
        public List<OpticalProbeChapterInfo> Chapters { get; set; } = new List<OpticalProbeChapterInfo>();
    }

    /// <summary>
    /// Read-only test surface for the Phase 2 optical-media pipeline. No media streams,
    /// chapters or BaseItem fields are changed by these endpoints.
    /// </summary>
    public sealed class OpticalMediaProbeApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly OpticalMediaProbe _probe;

        public OpticalMediaProbeApiService(ILibraryManager libraryManager, IJsonSerializer jsonSerializer)
        {
            _libraryManager = libraryManager;
            _probe = new OpticalMediaProbe(jsonSerializer);
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
                Streams = result.Streams,
                Chapters = result.Chapters
            };
        }

        private Video ResolveVideo(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            if (!long.TryParse(id, out var internalId)) return null;
            return _libraryManager.GetItemById(internalId) as Video;
        }
    }
}
