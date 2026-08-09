using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.MediaEnhance;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    public sealed class FingerprintHealthResult
    {
        public FingerprintRuntimeCapabilityResult NativeRuntime { get; set; }
        public DistributedExtractHealthResult DistributedTools { get; set; }
    }

    [Route("/StrmAssistant/Fingerprint/Health", "GET",
        Summary = "Inspect Emby fingerprint runtime bindings and optionally test distributed Chromaprint")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetFingerprintHealth : IReturn<FingerprintHealthResult>
    {
        public bool RunChromaprintTest { get; set; }
    }

    /// <summary>
    /// Read-only fingerprint diagnostics. It does not create fingerprint files,
    /// change intro markers, or replace Emby's AudioFingerprintManager.
    /// </summary>
    public sealed class FingerprintDiagnosticsApiService : BaseApiService
    {
        private readonly FingerprintRuntimeDiagnostics _runtimeDiagnostics = new FingerprintRuntimeDiagnostics();
        private readonly DistributedExtractDiagnostics _distributedDiagnostics = new DistributedExtractDiagnostics();

        public async Task<object> Get(GetFingerprintHealth request)
        {
            var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
            var tools = await _distributedDiagnostics.CheckAsync(options, false,
                    request?.RunChromaprintTest == true, CancellationToken.None)
                .ConfigureAwait(false);

            return new FingerprintHealthResult
            {
                NativeRuntime = _runtimeDiagnostics.Inspect(Plugin.FingerprintApi),
                DistributedTools = tools
            };
        }
    }
}