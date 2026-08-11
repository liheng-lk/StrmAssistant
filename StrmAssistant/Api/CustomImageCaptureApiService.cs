using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using StrmAssistant.MediaEnhance;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    [Route("/StrmAssistant/ImageCapture/{Id}/Plan", "GET",
        Summary = "Preview a custom/distributed ffmpeg image capture without saving an image")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetCustomImageCapturePlan : IReturn<CustomImageCapturePlan>
    {
        public string Id { get; set; }
        public int? PositionPercent { get; set; }
        public bool ResolveStrmTarget { get; set; }
    }

    [Route("/StrmAssistant/ImageCapture/{Id}/Apply", "POST",
        Summary = "Capture and save a primary image after explicit confirmation")]
    [Authenticated(Roles = "Admin")]
    public sealed class ApplyCustomImageCapture : IReturn<CustomImageCaptureResult>
    {
        public string Id { get; set; }
        public bool Confirm { get; set; }
        public bool ReplaceExistingPrimaryImage { get; set; }
        public int? PositionPercent { get; set; }
        public bool ResolveStrmTarget { get; set; }
    }

    public sealed class CustomImageCaptureApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly CustomImageCapture _capture;
        private readonly OpticalMediaProbe _opticalProbe;
        private readonly BluRayDiscInfoEnricher _bluRayEnricher;

        public CustomImageCaptureApiService(ILibraryManager libraryManager, IProviderManager providerManager,
            IFileSystem fileSystem, IApplicationPaths applicationPaths, IJsonSerializer jsonSerializer,
            IApplicationHost applicationHost)
        {
            _libraryManager = libraryManager;
            _capture = new CustomImageCapture(libraryManager, providerManager, fileSystem, applicationPaths);
            _opticalProbe = new OpticalMediaProbe(jsonSerializer);
            _bluRayEnricher = new BluRayDiscInfoEnricher(applicationHost);
        }

        public async Task<object> Get(GetCustomImageCapturePlan request)
        {
            var item = ResolveVideo(request?.Id);
            if (item == null)
                return new CustomImageCapturePlan { Error = "Video item was not found." };

            var inputPath = await ResolveInputPath(item, request?.ResolveStrmTarget == true).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                return new CustomImageCapturePlan
                {
                    ItemId = item.InternalId.ToString(),
                    ItemName = item.Name,
                    Error = item.IsShortcut
                        ? "This is a STRM item. Set ResolveStrmTarget=true to capture from the mounted target explicitly."
                        : "Media input path is empty."
                };
            }

            var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
            var optical = await GetOpticalContext(item, options).ConfigureAwait(false);
            return _capture.BuildPlan(item, inputPath, options, optical.Probe, optical.Disc,
                request?.PositionPercent);
        }

        public async Task<object> Post(ApplyCustomImageCapture request)
        {
            var item = ResolveVideo(request?.Id);
            if (item == null)
                return new CustomImageCaptureResult { Error = "Video item was not found." };

            if (request == null || !request.Confirm)
            {
                return new CustomImageCaptureResult
                {
                    Error = "Image capture was not confirmed. Review the Plan endpoint first, then submit Confirm=true."
                };
            }

            var inputPath = await ResolveInputPath(item, request.ResolveStrmTarget).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                return new CustomImageCaptureResult
                {
                    Error = item.IsShortcut
                        ? "STRM capture requires ResolveStrmTarget=true and a mountable target."
                        : "Media input path is empty."
                };
            }

            var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
            var optical = await GetOpticalContext(item, options).ConfigureAwait(false);

            return await _capture.CaptureAndSaveAsync(item, inputPath, options,
                    request.ReplaceExistingPrimaryImage, request.PositionPercent, optical.Probe, optical.Disc,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        private Video ResolveVideo(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            if (!long.TryParse(id, out var internalId)) return null;
            return _libraryManager.GetItemById(internalId) as Video;
        }

        private static async Task<string> ResolveInputPath(Video item, bool resolveStrmTarget)
        {
            if (!item.IsShortcut) return item.Path;
            if (!resolveStrmTarget) return null;
            return await Plugin.LibraryApi.GetStrmMountPath(item.Path).ConfigureAwait(false);
        }

        private async Task<OpticalContext> GetOpticalContext(Video item,
            StrmAssistant.Options.MediaInfoExtractOptions options)
        {
            if (OpticalMediaProbe.GetMediaKind(item) == OpticalMediaKind.Unsupported)
                return new OpticalContext();

            var probe = await _opticalProbe.ProbeAsync(item, options, CancellationToken.None).ConfigureAwait(false);
            var disc = _bluRayEnricher.Enrich(item, probe);
            return new OpticalContext { Probe = probe, Disc = disc };
        }

        private sealed class OpticalContext
        {
            public OpticalProbeResult Probe { get; set; }
            public BluRayDiscEnrichmentSummary Disc { get; set; }
        }
    }
}
