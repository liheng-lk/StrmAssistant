using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using StrmAssistant.Experience;
using System;

namespace StrmAssistant.Api
{
    public sealed class NativeDeleteWebhookBridgeDiagnosticsResponse
    {
        public string GeneratedUtc { get; set; }
        public bool DeepDeleteEnabled { get; set; }
        public bool DeepDeleteDryRun { get; set; }
        public bool DirectRemoteProviderEnabled { get; set; }
        public NativeItemDeleteWebhookBridgeStatus WebhookBridge { get; set; }
        public NativeItemDeleteRemoteBridgeStatus DirectRemoteBridge { get; set; }
        public string ExpectedWebhookEvent { get; set; } = "deep.delete";
        public string Note { get; set; }
    }

    [Route("/StrmAssistant/DeepDelete/WebhookBridgeStatus", "GET",
        Summary = "Inspect native Emby delete to deep.delete webhook bridge status")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetNativeDeleteWebhookBridgeDiagnostics :
        IReturn<NativeDeleteWebhookBridgeDiagnosticsResponse>
    {
    }

    public sealed class NativeDeleteWebhookBridgeDiagnosticsApiService : BaseApiService
    {
        public object Get(GetNativeDeleteWebhookBridgeDiagnostics request)
        {
            var experience = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            var remote = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            return new NativeDeleteWebhookBridgeDiagnosticsResponse
            {
                GeneratedUtc = DateTimeOffset.UtcNow.ToString("O"),
                DeepDeleteEnabled = experience?.EnableDeepDelete == true,
                DeepDeleteDryRun = experience?.DeepDeleteDryRun == true,
                DirectRemoteProviderEnabled = remote.Enabled &&
                                              remote.Provider != RemoteDeepDeleteProviderType.None,
                WebhookBridge = NativeItemDeleteWebhookBridgeState.Status,
                DirectRemoteBridge = NativeItemDeleteRemoteBridgeState.Status,
                Note = remote.Enabled && remote.Provider != RemoteDeepDeleteProviderType.None
                    ? "A direct OpenList/WebDAV provider is enabled, so the provider-agnostic webhook bridge intentionally stays idle to prevent duplicate destructive triggers."
                    : "With Deep Delete enabled, deleting an HTTP/HTTPS STRM through Emby's native DELETE /Items/{Id} should emit Event=deep.delete before the local STRM is removed."
            };
        }
    }
}
