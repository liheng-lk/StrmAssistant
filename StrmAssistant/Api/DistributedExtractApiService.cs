using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using StrmAssistant.MediaEnhance;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    [Route("/StrmAssistant/DistributedExtract/Health", "GET",
        Summary = "Check custom ffprobe/ffmpeg and rffmpeg capabilities")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetDistributedExtractHealth : IReturn<DistributedExtractHealthResult>
    {
        public bool RunVulkanTest { get; set; }
    }

    [Route("/StrmAssistant/DistributedExtract/Probe/{Id}", "GET",
        Summary = "Probe one item through the configured distributed ffprobe without writing Emby metadata")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetDistributedExtractProbe : IReturn<DistributedMediaInfoPreviewResult>
    {
        public string Id { get; set; }
        public bool ResolveStrmTarget { get; set; }
    }

    /// <summary>
    /// Read-only distributed extraction diagnostics. Health checks tool capabilities;
    /// Probe verifies that one concrete media path is actually readable by the configured wrapper/worker.
    /// Neither endpoint changes Emby's global encoder paths or media metadata.
    /// </summary>
    public sealed class DistributedExtractApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly DistributedExtractDiagnostics _diagnostics = new DistributedExtractDiagnostics();
        private readonly DistributedMediaInfoPreview _preview;

        public DistributedExtractApiService(ILibraryManager libraryManager, IJsonSerializer jsonSerializer)
        {
            _libraryManager = libraryManager;
            _preview = new DistributedMediaInfoPreview(jsonSerializer);
        }

        public async Task<object> Get(GetDistributedExtractHealth request)
        {
            var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
            return await _diagnostics.CheckAsync(options, request?.RunVulkanTest == true,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        public async Task<object> Get(GetDistributedExtractProbe request)
        {
            var item = ResolveItem(request?.Id);
            if (item == null)
            {
                return new DistributedMediaInfoPreviewResult
                {
                    Error = "Media item was not found."
                };
            }

            var inputPath = item.Path;
            if (item.IsShortcut)
            {
                if (request?.ResolveStrmTarget != true)
                {
                    return new DistributedMediaInfoPreviewResult
                    {
                        InputPath = item.Path,
                        Error = "This is a STRM item. Set ResolveStrmTarget=true to test the mounted target path explicitly."
                    };
                }

                inputPath = await Plugin.LibraryApi.GetStrmMountPath(item.Path).ConfigureAwait(false);
            }

            var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
            return await _preview.ProbeAsync(item, inputPath, options, CancellationToken.None)
                .ConfigureAwait(false);
        }

        private BaseItem ResolveItem(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            if (!long.TryParse(id, out var internalId)) return null;
            return _libraryManager.GetItemById(internalId);
        }
    }
}
