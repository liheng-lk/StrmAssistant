using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using StrmAssistant.Experience;
using StrmAssistant.MediaEnhance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    public sealed class RemoteDeleteReliabilitySummary
    {
        public bool Enabled { get; set; }
        public string Provider { get; set; }
        public string BaseUrl { get; set; }
        public bool HasAccessToken { get; set; }
        public bool HasCredentials { get; set; }
        public string AllowedRemoteRoots { get; set; }
        public bool HasManualPathMappings { get; set; }
        public RemoteDeepDeletePlan Plan { get; set; }
        public RemoteDeepDeleteProbeResult Probe { get; set; }
        public OpenListDirectLinkDeepDeleteStatus OpenListDirectLinkBridge { get; set; }
        public NativeItemDeleteRemoteBridgeStatus NativeDeleteBridge { get; set; }
        public NativeRemoteDeleteTransactionStatus NativeDeleteTransaction { get; set; }
    }

    public sealed class ItemReliabilityReport
    {
        public string GeneratedUtc { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string ItemPath { get; set; }
        public MediaInfoIntegrityAssessment MediaInfo { get; set; }
        public bool ShadowApplicable { get; set; }
        public bool ShadowValidForCurrentStrmTarget { get; set; }
        public MediaInfoPreReadGuardStatus MediaInfoPreReadGuard { get; set; }
        public MediaInfoPersistenceReliabilityStatus MediaInfoPersistenceGuard { get; set; }
        public MediaInfoReliabilityShadowStatus MediaInfoShadow { get; set; }
        public MediaInfoReliabilityShadowRuntimeStatus MediaInfoShadowCaptureHook { get; set; }
        public MediaInfoReliabilityShadowUpdateStatus MediaInfoShadowUpdateHook { get; set; }
        public RemoteDeleteReliabilitySummary RemoteDelete { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/Reliability/{Id}", "GET",
        Summary = "Inspect MediaInfo and remote deep-delete reliability for one item")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetItemReliabilityReport : IReturn<ItemReliabilityReport>
    {
        public string Id { get; set; }
        public bool ProbeRemote { get; set; }
    }

    public sealed class ReliabilityDiagnosticsApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly RemoteDeepDeleteService _remoteDeepDelete = new RemoteDeepDeleteService();

        public ReliabilityDiagnosticsApiService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public async Task<object> Get(GetItemReliabilityReport request)
        {
            var item = ResolveItem(request?.Id);
            if (item == null) throw new ArgumentException("Media item was not found: " + request?.Id);

            var remoteOptions = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            var remotePlan = _remoteDeepDelete.BuildPlan(item);
            RemoteDeepDeleteProbeResult probe = null;
            if (request?.ProbeRemote == true && remotePlan?.Applicable == true && remotePlan.Allowed)
                probe = await _remoteDeepDelete.ProbeAsync(remotePlan, CancellationToken.None).ConfigureAwait(false);

            var shadowApplicable = MediaInfoReliabilityShadowStore.AppliesTo(item);
            var shadowValid = shadowApplicable && MediaInfoReliabilityShadowStore.Exists(item);
            var report = new ItemReliabilityReport
            {
                GeneratedUtc = DateTimeOffset.UtcNow.ToString("O"),
                ItemId = item.InternalId.ToString(),
                ItemName = item.Name,
                ItemPath = item.Path,
                MediaInfo = MediaInfoIntegrityService.Assess(item),
                ShadowApplicable = shadowApplicable,
                ShadowValidForCurrentStrmTarget = shadowValid,
                MediaInfoPreReadGuard = MediaInfoPreReadGuardState.Status,
                MediaInfoPersistenceGuard = MediaInfoPersistenceReliabilityState.Status,
                MediaInfoShadow = MediaInfoReliabilityShadowStore.Status,
                MediaInfoShadowCaptureHook = MediaInfoReliabilityShadowRuntimeState.Status,
                MediaInfoShadowUpdateHook = MediaInfoReliabilityShadowUpdateState.Status,
                RemoteDelete = new RemoteDeleteReliabilitySummary
                {
                    Enabled = remoteOptions.Enabled,
                    Provider = remoteOptions.Provider.ToString(),
                    BaseUrl = RedactAuthority(remoteOptions.BaseUrl),
                    HasAccessToken = !string.IsNullOrWhiteSpace(remoteOptions.AccessToken),
                    HasCredentials = !string.IsNullOrWhiteSpace(remoteOptions.Username) ||
                                     !string.IsNullOrWhiteSpace(remoteOptions.Password),
                    AllowedRemoteRoots = remoteOptions.AllowedRemoteRoots,
                    HasManualPathMappings = RemoteDeepDeleteRuntimeSettings.ParseMappings(remoteOptions.PathMappings).Count > 0,
                    Plan = remotePlan,
                    Probe = probe,
                    OpenListDirectLinkBridge = OpenListDirectLinkDeepDeleteState.Status,
                    NativeDeleteBridge = NativeItemDeleteRemoteBridgeState.Status,
                    NativeDeleteTransaction = NativeRemoteDeleteTransactionState.Status
                }
            };

            if (report.MediaInfo?.CoreMediaInfoComplete == false && report.MediaInfo.Recoverable)
                report.Warnings.Add("Core MediaInfo is incomplete, but a validated local snapshot/shadow is available; the playback pre-read guard should hydrate it without probing the remote media.");
            if (report.MediaInfo?.PlaybackProbeRisk == true)
                report.Warnings.Add("Core MediaInfo is incomplete and no validated local recovery source exists. One explicit MediaInfo extraction is required before this item can be protected from future loss.");
            if (shadowApplicable && report.MediaInfo?.CoreMediaInfoComplete == true && !shadowValid)
                report.Warnings.Add("This STRM currently has complete MediaInfo but no valid reliability shadow for its current target. Startup seeding or the next successful MediaInfo update should create one.");
            if (report.MediaInfoPreReadGuard?.MediaSourceTargetsPatched <= 0)
                report.Warnings.Add("No playback/static MediaSourceManager pre-read target is active; recoverable MediaInfo may not be hydrated before playback.");
            if (report.MediaInfoShadowCaptureHook?.SaveMediaStreamsTargetsPatched <= 0)
                report.Warnings.Add("No SaveMediaStreams capture hook is active; newly extracted STRM MediaInfo may not be copied into the reliability shadow automatically.");
            if (report.MediaInfoShadowUpdateHook?.UpdateItemsTargetsPatched <= 0)
                report.Warnings.Add("No UpdateItems shadow capture hook is active; a MediaInfo transaction that writes streams before runtime/container fields may miss automatic shadow capture.");
            if (remoteOptions.Enabled && remotePlan?.Applicable == true && !remotePlan.Allowed)
                report.Warnings.Add("The item resolves to a remote target, but the remote deletion plan is blocked: " + remotePlan.Error);
            if (remoteOptions.Enabled && NativeItemDeleteRemoteBridgeState.Status?.ExplicitDeleteTargetsPatched == 0)
                report.Warnings.Add("Remote Deep Delete is enabled, but no native Emby single-item delete route is currently patched. Use the plugin Deep Delete API until the runtime target is resolved.");
            if (request?.ProbeRemote == true && probe != null && !probe.Success)
                report.Warnings.Add("Remote target probe failed: " + probe.Error);

            var transaction = NativeRemoteDeleteTransactionState.Status;
            if (transaction?.LocalDeletesFailedAfterRemoteSuccess > 0 ||
                transaction?.LocalItemsStillPresentAfterRemoteSuccess > 0)
            {
                report.Warnings.Add("A previous native delete reached an irreversible partial state: the remote target was deleted but the local Emby deletion failed or the item remained. Inspect NativeDeleteTransaction immediately.");
            }

            return report;
        }

        private BaseItem ResolveItem(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            if (long.TryParse(id, out var internalId))
            {
                try
                {
                    var byLong = _libraryManager.GetItemById(internalId);
                    if (byLong != null) return byLong;
                }
                catch { }
            }

            foreach (var method in _libraryManager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                         .Where(method => string.Equals(method.Name, "GetItemById", StringComparison.Ordinal) &&
                                          method.GetParameters().Length == 1))
            {
                try
                {
                    var parameterType = method.GetParameters()[0].ParameterType;
                    object argument = null;
                    if (parameterType == typeof(string)) argument = id;
                    else if (parameterType == typeof(Guid) && Guid.TryParse(id, out var guid)) argument = guid;
                    else continue;
                    if (method.Invoke(_libraryManager, new[] { argument }) is BaseItem item) return item;
                }
                catch { }
            }
            return null;
        }

        private static string RedactAuthority(string baseUrl)
        {
            return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
                ? uri.GetLeftPart(UriPartial.Authority)
                : string.Empty;
        }
    }
}
