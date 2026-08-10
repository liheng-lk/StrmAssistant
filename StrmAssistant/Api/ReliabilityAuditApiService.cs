using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using StrmAssistant.Experience;
using StrmAssistant.MediaEnhance;
using System;
using System.Collections.Generic;

namespace StrmAssistant.Api
{
    public sealed class ReliabilityAuditResult
    {
        public bool Healthy { get; set; }
        public string PluginVersion { get; set; }
        public string EmbyVersion { get; set; }
        public MediaInfoPreReadGuardStatus MediaInfoPreRead { get; set; }
        public MediaInfoPersistenceReliabilityStatus MediaInfoPersistence { get; set; }
        public MediaInfoReliabilityShadowStatus MediaInfoShadow { get; set; }
        public RemoteDeepDeleteCapabilityStatus RemoteDeepDelete { get; set; }
        public NativeItemDeleteRemoteBridgeStatus NativeDeleteBridge { get; set; }
        public string RemoteProvider { get; set; }
        public string RemoteEndpointHost { get; set; }
        public bool RemoteDeleteEnabled { get; set; }
        public bool RemoteCredentialsConfigured { get; set; }
        public int RemotePathMappingCount { get; set; }
        public int RemoteAllowedRootCount { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/ReliabilityAudit", "GET",
        Summary = "Audit Strm Assistant runtime reliability capabilities without exposing secrets")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetReliabilityAudit : IReturn<ReliabilityAuditResult> { }

    public sealed class ReliabilityAuditApiService : BaseApiService
    {
        public object Get(GetReliabilityAudit request)
        {
            var remote = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            var result = new ReliabilityAuditResult
            {
                PluginVersion = Plugin.Instance?.Version?.ToString(),
                EmbyVersion = Plugin.Instance?.ApplicationHost?.ApplicationVersion?.ToString(),
                MediaInfoPreRead = MediaInfoPreReadGuardState.Status,
                MediaInfoPersistence = MediaInfoPersistenceReliabilityState.Status,
                MediaInfoShadow = MediaInfoReliabilityShadowStore.Status,
                RemoteDeepDelete = RemoteDeepDeleteModState.Status,
                NativeDeleteBridge = NativeItemDeleteRemoteBridgeState.Status,
                RemoteProvider = remote.Provider.ToString(),
                RemoteDeleteEnabled = remote.Enabled,
                RemoteEndpointHost = SafeHost(remote.BaseUrl),
                RemoteCredentialsConfigured = remote.Provider == RemoteDeepDeleteProviderType.OpenList
                    ? !string.IsNullOrWhiteSpace(remote.AccessToken)
                    : remote.Provider == RemoteDeepDeleteProviderType.WebDav &&
                      (!string.IsNullOrWhiteSpace(remote.Username) || !string.IsNullOrEmpty(remote.Password)),
                RemotePathMappingCount = RemoteDeepDeleteRuntimeSettings.ParseMappings(remote.PathMappings).Count,
                RemoteAllowedRootCount = RemoteDeepDeleteRuntimeSettings.ParseAllowedRoots(remote.AllowedRemoteRoots).Count
            };

            if (result.MediaInfoPreRead?.MediaSourceTargetsPatched <= 0)
                result.Warnings.Add("No compatible playback/static MediaSourceManager pre-read target is active.");
            if (!string.IsNullOrWhiteSpace(result.MediaInfoPreRead?.Error))
                result.Warnings.Add("MediaInfo pre-read: " + result.MediaInfoPreRead.Error);
            if (!string.IsNullOrWhiteSpace(result.MediaInfoPersistence?.Error))
                result.Warnings.Add("MediaInfo persistence: " + result.MediaInfoPersistence.Error);

            if (remote.Enabled)
            {
                if (remote.Provider == RemoteDeepDeleteProviderType.None)
                    result.Warnings.Add("Remote deep delete is enabled but no provider is selected.");
                if (string.IsNullOrWhiteSpace(remote.BaseUrl))
                    result.Warnings.Add("Remote deep delete BaseUrl is missing or invalid.");
                if (result.RemoteAllowedRootCount == 0)
                    result.Warnings.Add("Remote deep delete has no allowed roots; destructive calls are blocked.");
                if (!result.RemoteCredentialsConfigured)
                    result.Warnings.Add("Remote provider credentials are not configured.");
                if (result.RemoteDeepDelete?.DirectApiIntegration != true)
                    result.Warnings.Add("Direct remote deep-delete API integration is not active.");
            }

            result.Healthy = result.MediaInfoPreRead?.MediaSourceTargetsPatched > 0 &&
                             string.IsNullOrWhiteSpace(result.MediaInfoPreRead?.Error) &&
                             string.IsNullOrWhiteSpace(result.MediaInfoPersistence?.Error) &&
                             (!remote.Enabled ||
                              (remote.Provider != RemoteDeepDeleteProviderType.None &&
                               !string.IsNullOrWhiteSpace(remote.BaseUrl) &&
                               result.RemoteAllowedRootCount > 0 &&
                               result.RemoteCredentialsConfigured &&
                               result.RemoteDeepDelete?.DirectApiIntegration == true));
            return result;
        }

        private static string SafeHost(string baseUrl)
        {
            return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
                ? uri.GetLeftPart(UriPartial.Authority)
                : null;
        }
    }
}
