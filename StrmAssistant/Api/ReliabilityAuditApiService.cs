using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using StrmAssistant.Experience;
using StrmAssistant.MediaEnhance;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Api
{
    public sealed class ReliabilityInventorySummary
    {
        public bool Included { get; set; }
        public int TotalStrmItems { get; set; }
        public int InScopeStrmItems { get; set; }
        public int OutOfScopeStrmItems { get; set; }
        public int CoreMediaInfoComplete { get; set; }
        public int ValidV3Shadow { get; set; }
        public int CompleteButMissingShadow { get; set; }
        public int IncompleteButRecoverable { get; set; }
        public int PlaybackProbeRisk { get; set; }
        public int ProtectedByAnyLocalRecoverySource { get; set; }
        public List<string> PlaybackProbeRiskItemIds { get; set; } = new List<string>();
    }

    public sealed class ReliabilityAuditResult
    {
        public bool Healthy { get; set; }
        public string PluginVersion { get; set; }
        public string EmbyVersion { get; set; }
        public MediaInfoPreReadGuardStatus MediaInfoPreRead { get; set; }
        public MediaInfoPersistenceReliabilityStatus MediaInfoPersistence { get; set; }
        public MediaInfoReliabilityShadowStatus MediaInfoShadow { get; set; }
        public MediaInfoReliabilityShadowRuntimeStatus MediaInfoShadowCaptureHook { get; set; }
        public MediaInfoReliabilityShadowUpdateStatus MediaInfoShadowUpdateHook { get; set; }
        public MediaInfoReliabilitySeedMigrationStatus MediaInfoShadowMigration { get; set; }
        public ExplicitMediaInfoClearReliabilityStatus ExplicitMediaInfoClearInvalidation { get; set; }
        public RemoteDeepDeleteCapabilityStatus RemoteDeepDelete { get; set; }
        public RemoteDeepDeleteProbeSafetyStatus RemoteProbeSafety { get; set; }
        public RemoteDeepDeletePlanSafetyStatus RemotePlanSafety { get; set; }
        public RemoteDeepDeleteUiAuthorityStatus RemoteUiAuthority { get; set; }
        public OpenListDirectLinkDeepDeleteStatus OpenListDirectLinkBridge { get; set; }
        public OpenListRemoteSidecarDeleteStatus OpenListSidecars { get; set; }
        public NativeItemDeleteRemoteBridgeStatus NativeDeleteBridge { get; set; }
        public NativeRemoteDeleteTransactionStatus NativeDeleteTransaction { get; set; }
        public NativeRemoteDeleteDeferredCleanupStatus DeferredDeleteCleanup { get; set; }
        public NativeCascadeDeleteRemoteGuardStatus NativeCascadeDeleteGuard { get; set; }
        public RemoteDeepDeleteTransactionJournalStatus RemoteDeleteJournal { get; set; }
        public string RemoteProvider { get; set; }
        public string RemoteEndpointHost { get; set; }
        public bool RemoteDeleteEnabled { get; set; }
        public bool RemoteCredentialsConfigured { get; set; }
        public bool RemoteSidecarsEnabled { get; set; }
        public bool RemoteUiIsAuthoritative { get; set; }
        public int RemotePathMappingCount { get; set; }
        public int RemoteAllowedRootCount { get; set; }
        public ReliabilityInventorySummary Inventory { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/ReliabilityAudit", "GET",
        Summary = "Audit Strm Assistant runtime reliability capabilities without exposing secrets")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetReliabilityAudit : IReturn<ReliabilityAuditResult>
    {
        public bool IncludeInventory { get; set; }
    }

    public sealed class ReliabilityAuditApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;

        public ReliabilityAuditApiService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public object Get(GetReliabilityAudit request)
        {
            var remote = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            var ui = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            var result = new ReliabilityAuditResult
            {
                PluginVersion = Plugin.Instance?.Version?.ToString(),
                EmbyVersion = Plugin.Instance?.ApplicationHost?.ApplicationVersion?.ToString(),
                MediaInfoPreRead = MediaInfoPreReadGuardState.Status,
                MediaInfoPersistence = MediaInfoPersistenceReliabilityState.Status,
                MediaInfoShadow = MediaInfoReliabilityShadowStore.Status,
                MediaInfoShadowCaptureHook = MediaInfoReliabilityShadowRuntimeState.Status,
                MediaInfoShadowUpdateHook = MediaInfoReliabilityShadowUpdateState.Status,
                MediaInfoShadowMigration = MediaInfoReliabilitySeedMigrationState.Status,
                ExplicitMediaInfoClearInvalidation = ExplicitMediaInfoClearReliabilityState.Status,
                RemoteDeepDelete = RemoteDeepDeleteModState.Status,
                RemoteProbeSafety = RemoteDeepDeleteProbeSafetyState.Status,
                RemotePlanSafety = RemoteDeepDeletePlanSafetyState.Status,
                RemoteUiAuthority = RemoteDeepDeleteUiAuthorityState.Status,
                OpenListDirectLinkBridge = OpenListDirectLinkDeepDeleteState.Status,
                OpenListSidecars = OpenListRemoteSidecarDeleteState.Status,
                NativeDeleteBridge = NativeItemDeleteRemoteBridgeState.Status,
                NativeDeleteTransaction = NativeRemoteDeleteTransactionState.Status,
                DeferredDeleteCleanup = NativeRemoteDeleteDeferredCleanupState.Status,
                NativeCascadeDeleteGuard = NativeCascadeDeleteRemoteGuardState.Status,
                RemoteDeleteJournal = RemoteDeepDeleteTransactionJournalState.Status,
                RemoteProvider = remote.Provider.ToString(),
                RemoteDeleteEnabled = remote.Enabled,
                RemoteEndpointHost = SafeHost(remote.BaseUrl),
                RemoteCredentialsConfigured = remote.Provider == RemoteDeepDeleteProviderType.OpenList
                    ? !string.IsNullOrWhiteSpace(remote.AccessToken)
                    : remote.Provider == RemoteDeepDeleteProviderType.WebDav &&
                      (!string.IsNullOrWhiteSpace(remote.Username) || !string.IsNullOrEmpty(remote.Password)),
                RemoteSidecarsEnabled = remote.DeleteAssociatedSidecars,
                RemoteUiIsAuthoritative = ui?.RemoteDeepDeleteUiAuthoritative == true,
                RemotePathMappingCount = RemoteDeepDeleteRuntimeSettings.ParseMappings(remote.PathMappings).Count,
                RemoteAllowedRootCount = RemoteDeepDeleteRuntimeSettings.ParseAllowedRoots(remote.AllowedRemoteRoots).Count,
                Inventory = request?.IncludeInventory == true
                    ? BuildInventory()
                    : new ReliabilityInventorySummary { Included = false }
            };

            if (result.MediaInfoPreRead?.MediaSourceTargetsPatched <= 0)
                result.Warnings.Add("No compatible playback/static MediaSourceManager pre-read target is active.");
            if (!string.IsNullOrWhiteSpace(result.MediaInfoPreRead?.Error))
                result.Warnings.Add("MediaInfo pre-read: " + result.MediaInfoPreRead.Error);
            if (!string.IsNullOrWhiteSpace(result.MediaInfoPersistence?.Error))
                result.Warnings.Add("MediaInfo persistence: " + result.MediaInfoPersistence.Error);
            if (result.MediaInfoShadowCaptureHook?.SaveMediaStreamsTargetsPatched <= 0)
                result.Warnings.Add("No SaveMediaStreams STRM shadow capture hook is active.");
            if (result.MediaInfoShadowUpdateHook?.UpdateItemsTargetsPatched <= 0)
                result.Warnings.Add("No UpdateItems STRM shadow completion hook is active.");
            if (result.MediaInfoShadowMigration?.ManualSeedRequired == true)
                result.Warnings.Add("STRM shadow schema-v" + result.MediaInfoShadowMigration.SchemaVersion +
                                    " seed is pending. Run the manual reliability seed task when the library scanner is idle.");
            if (!string.IsNullOrWhiteSpace(result.MediaInfoShadowMigration?.Error))
                result.Warnings.Add("STRM shadow schema migration: " + result.MediaInfoShadowMigration.Error);

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
                if (result.NativeDeleteBridge?.ExplicitDeleteTargetsPatched <= 0)
                    result.Warnings.Add("No native single-item Emby delete route is bridged to remote deletion.");
                if (result.DeferredDeleteCleanup?.ImmediateCleanupSuppressed != true ||
                    result.DeferredDeleteCleanup?.CleanupTargetsPatched < 2)
                    result.Warnings.Add("MediaInfo cleanup is not fully deferred until confirmed ItemRemoved for both native and plugin-owned deep-delete paths.");
                if (result.NativeCascadeDeleteGuard?.SingleDeleteTargetsPatched <= 0)
                    result.Warnings.Add("No native single-item parent/folder delete route is protected by recursive remote-cascade inspection.");
                if (result.NativeCascadeDeleteGuard?.BatchDeleteTargetsPatched <= 0)
                    result.Warnings.Add("No native batch /Items delete route is protected by recursive remote-cascade inspection.");
                if (result.RemoteDeleteJournal?.ExecuteAsyncPatched != true)
                    result.Warnings.Add("Verified remote-delete retry journal is inactive; remote-success/local-failure retries may be unsafe when TreatNotFoundAsSuccess is disabled.");
                if (result.RemotePlanSafety?.Patched != true)
                    result.Warnings.Add("Remote destructive mapping path-boundary safety is inactive.");
                if (remote.Provider == RemoteDeepDeleteProviderType.OpenList && result.RemoteProbeSafety?.Patched != true)
                    result.Warnings.Add("OpenList remote deletion is enabled but structured probe-result safety normalization is inactive.");
                if (result.RemoteUiIsAuthoritative && result.RemoteUiAuthority?.Patched != true)
                    result.Warnings.Add("The main remote-delete UI is authoritative, but its legacy-config resurrection guard is inactive.");
                if (remote.DeleteAssociatedSidecars && remote.Provider != RemoteDeepDeleteProviderType.OpenList)
                    result.Warnings.Add("Remote sidecar cleanup is enabled but currently implemented only for OpenList.");
                if (remote.DeleteAssociatedSidecars && !remote.TreatNotFoundAsSuccess)
                    result.Warnings.Add("Remote sidecar cleanup currently requires TreatNotFoundAsSuccess=true for its confirmed cascade transaction path.");
                if (remote.DeleteAssociatedSidecars && result.OpenListSidecars?.ExecuteAsyncPatched != true)
                    result.Warnings.Add("OpenList sidecar cleanup is enabled but its ExecuteAsync transaction extension is inactive.");
            }

            if (result.DeferredDeleteCleanup?.PendingCount > 0)
                result.Warnings.Add(result.DeferredDeleteCleanup.PendingCount +
                                    " deep-delete item(s) are waiting for an Emby ItemRemoved confirmation before MediaInfo/shadow cleanup.");
            if (result.RemoteDeleteJournal?.ActiveEntries > 0)
                result.Warnings.Add(result.RemoteDeleteJournal.ActiveEntries +
                                    " verified remote-delete journal entr" +
                                    (result.RemoteDeleteJournal.ActiveEntries == 1 ? "y is" : "ies are") +
                                    " retained for safe retry until local deletion succeeds or the entry expires.");

            if (result.NativeDeleteTransaction?.LocalDeletesFailedAfterRemoteSuccess > 0 ||
                result.NativeDeleteTransaction?.LocalItemsStillPresentAfterRemoteSuccess > 0)
                result.Warnings.Add("A remote-success/local-delete partial state has previously been observed. Inspect NativeDeleteTransaction, DeferredDeleteCleanup and RemoteDeleteJournal; the new transaction layers preserve local MediaInfo and support bounded retry instead of treating the state as irrecoverable.");

            if (result.Inventory?.Included == true)
            {
                if (result.Inventory.PlaybackProbeRisk > 0)
                    result.Warnings.Add(result.Inventory.PlaybackProbeRisk +
                                        " in-scope STRM items currently have incomplete core MediaInfo and no validated local recovery source; run the STRM MediaInfo repair task while the scanner is idle.");
                if (result.Inventory.CompleteButMissingShadow > 0)
                    result.Warnings.Add(result.Inventory.CompleteButMissingShadow +
                                        " complete in-scope STRM items are not yet protected by a valid schema-v3 shadow.");
            }

            var remoteHealthy = !remote.Enabled ||
                                (remote.Provider != RemoteDeepDeleteProviderType.None &&
                                 !string.IsNullOrWhiteSpace(remote.BaseUrl) &&
                                 result.RemoteAllowedRootCount > 0 &&
                                 result.RemoteCredentialsConfigured &&
                                 result.RemoteDeepDelete?.DirectApiIntegration == true &&
                                 result.NativeDeleteBridge?.ExplicitDeleteTargetsPatched > 0 &&
                                 result.DeferredDeleteCleanup?.ImmediateCleanupSuppressed == true &&
                                 result.DeferredDeleteCleanup?.CleanupTargetsPatched >= 2 &&
                                 result.NativeCascadeDeleteGuard?.SingleDeleteTargetsPatched > 0 &&
                                 result.NativeCascadeDeleteGuard?.BatchDeleteTargetsPatched > 0 &&
                                 result.RemoteDeleteJournal?.ExecuteAsyncPatched == true &&
                                 result.RemotePlanSafety?.Patched == true &&
                                 (!result.RemoteUiIsAuthoritative || result.RemoteUiAuthority?.Patched == true) &&
                                 (remote.Provider != RemoteDeepDeleteProviderType.OpenList ||
                                  result.RemoteProbeSafety?.Patched == true) &&
                                 (!remote.DeleteAssociatedSidecars ||
                                  (remote.Provider == RemoteDeepDeleteProviderType.OpenList &&
                                   remote.TreatNotFoundAsSuccess &&
                                   result.OpenListSidecars?.ExecuteAsyncPatched == true)));

            result.Healthy = result.MediaInfoPreRead?.MediaSourceTargetsPatched > 0 &&
                             string.IsNullOrWhiteSpace(result.MediaInfoPreRead?.Error) &&
                             string.IsNullOrWhiteSpace(result.MediaInfoPersistence?.Error) &&
                             (result.Inventory?.Included != true || result.Inventory.PlaybackProbeRisk == 0) &&
                             remoteHealthy;
            return result;
        }

        private ReliabilityInventorySummary BuildInventory()
        {
            var summary = new ReliabilityInventorySummary { Included = true };
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                HasPath = true,
                MediaTypes = new[] { MediaType.Video, MediaType.Audio }
            }) ?? Array.Empty<BaseItem>();

            foreach (var item in items
                         .Where(MediaInfoReliabilityShadowStore.AppliesTo)
                         .GroupBy(candidate => candidate.InternalId)
                         .Select(group => group.First()))
            {
                summary.TotalStrmItems++;
                if (Plugin.LibraryApi?.IsLibraryInScope(item) != true)
                {
                    summary.OutOfScopeStrmItems++;
                    continue;
                }

                summary.InScopeStrmItems++;
                var assessment = MediaInfoIntegrityService.Assess(item);
                var coreComplete = assessment?.CoreMediaInfoComplete == true;
                var shadowValid = assessment?.ShadowSnapshotValid == true;
                var recoverable = assessment?.Recoverable == true;

                if (coreComplete) summary.CoreMediaInfoComplete++;
                if (shadowValid) summary.ValidV3Shadow++;
                if (coreComplete && !shadowValid) summary.CompleteButMissingShadow++;
                if (!coreComplete && recoverable) summary.IncompleteButRecoverable++;
                if (recoverable || coreComplete && shadowValid) summary.ProtectedByAnyLocalRecoverySource++;

                if (!coreComplete && !recoverable)
                {
                    summary.PlaybackProbeRisk++;
                    if (summary.PlaybackProbeRiskItemIds.Count < 20)
                        summary.PlaybackProbeRiskItemIds.Add(item.InternalId.ToString());
                }
            }
            return summary;
        }

        private static string SafeHost(string baseUrl)
        {
            return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
                ? uri.GetLeftPart(UriPartial.Authority)
                : null;
        }
    }
}
