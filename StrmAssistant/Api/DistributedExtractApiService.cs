using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
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

    /// <summary>
    /// Read-only distributed extraction diagnostics. This endpoint never changes Emby's
    /// global ffmpeg/ffprobe paths and never routes extraction work by itself.
    /// </summary>
    public sealed class DistributedExtractApiService : BaseApiService
    {
        private readonly DistributedExtractDiagnostics _diagnostics = new DistributedExtractDiagnostics();

        public async Task<object> Get(GetDistributedExtractHealth request)
        {
            var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
            return await _diagnostics.CheckAsync(options, request?.RunVulkanTest == true,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }
}
