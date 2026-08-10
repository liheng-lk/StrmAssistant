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
    }

    public sealed class ItemReliabilityReport
    {
        public string GeneratedUtc { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string ItemPath { get; set; }
        public MediaInfoIntegrityAssessment MediaInfo { get; set; }
        public MediaInfoPreReadGuardStatus MediaInfoPreReadGuard { get; set; }
        public MediaInfoPersistenceReliabilityStatus MediaInfoPersistenceGuard { get; set; }
        public RemoteDeleteReliabilitySummary RemoteDelete { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class MediaInfoReliabilityRepairResult
    {
        public bool Success { get; set; }
        public bool Executed { get; set; }
        public string ItemId { get; set; }
        public MediaInfoIntegrityAssessment Before { get; set; }
        public MediaInfoIntegrityAssessment After { get; set; }
        public string Error { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/Reliability/{Id}", "GET",
        Summary = "Inspect MediaInfo persistence and remote deep-delete reliability for one item")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetItemReliabilityReport : IReturn<ItemReliabilityReport>
    {
        public string Id { get; set; }
        public bool ProbeRemote { get; set; }
    }

    [Route("/StrmAssistant/Reliability/{Id}/MediaInfo/Repair", "POST",
        Summary = "Explicitly restore missing core MediaInfo from a validated persisted snapshot")]
    [Authenticated(Roles = "Admin")]
    public sealed class RepairItemMediaInfo : IReturn<MediaInfoReliabilityRepairResult>
    {
        public string Id { get; set; }
        public bool Confirm { get; set; }
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
            {
                probe = await _remoteDeepDelete.ProbeAsync(remotePlan, CancellationToken.None).ConfigureAwait(false);
            }

            var report = new ItemReliabilityReport
            {
                GeneratedUtc = DateTimeOffset.UtcNow.ToString("O"),
                ItemId = item.InternalId.ToString(),
                ItemName = item.Name,
                ItemPath = item.Path,
                MediaInfo = MediaInfoIntegrityService.Assess(item),
                MediaInfoPreReadGuard = MediaInfoPreReadGuardState.Status,
                MediaInfoPersistenceGuard = MediaInfoPersistenceReliabilityState.Status,
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
                    NativeDeleteBridge = NativeItemDeleteRemoteBridgeState.Status
                }
            };

            if (report.MediaInfo?.PlaybackProbeRisk == true && report.MediaInfo.Recoverable)
                report.Warnings.Add("Core MediaInfo is currently incomplete, but a validated snapshot is available. Playback should be repaired before a new probe is allowed to become necessary.");
            if (report.MediaInfo?.PlaybackProbeRisk == true && !report.MediaInfo.Recoverable)
                report.Warnings.Add("Core MediaInfo is incomplete and no validated snapshot exists. One explicit extraction is still required before future loss can be recovered.");
            if (remoteOptions.Enabled && remotePlan?.Applicable == true && !remotePlan.Allowed)
                report.Warnings.Add("The item resolves to a remote target, but the remote deletion plan is blocked: " + remotePlan.Error);
            if (remoteOptions.Enabled && NativeItemDeleteRemoteBridgeState.Status?.ExplicitDeleteTargetsPatched == 0)
                report.Warnings.Add("Remote Deep Delete is enabled, but no native Emby single-item delete route is currently patched. Use the plugin Deep Delete API until the runtime target is resolved.");
            if (request?.ProbeRemote == true && probe != null && !probe.Success)
                report.Warnings.Add("Remote target probe failed: " + probe.Error);

            return report;
        }

        public object Post(RepairItemMediaInfo request)
        {
            var item = ResolveItem(request?.Id);
            if (item == null)
                return new MediaInfoReliabilityRepairResult
                {
                    Success = false,
                    ItemId = request?.Id,
                    Error = "Media item was not found."
                };

            var result = new MediaInfoReliabilityRepairResult
            {
                ItemId = item.InternalId.ToString(),
                Before = MediaInfoIntegrityService.Assess(item)
            };

            if (request?.Confirm != true)
            {
                result.Warnings.Add("Repair was not executed. Review Before and set Confirm=true.");
                return result;
            }

            if (result.Before.CoreMediaInfoComplete)
            {
                result.Success = true;
                result.Warnings.Add("Core MediaInfo is already complete; no write was necessary.");
                result.After = result.Before;
                return result;
            }

            if (!result.Before.Recoverable)
            {
                result.Error = "No validated primary or backup MediaInfo snapshot is available for this item.";
                result.After = result.Before;
                return result;
            }

            try
            {
                result.Executed = true;
                MediaInfoIntegrityService.RepairPrimaryFromBackupIfNeeded(item);
                result.Success = MediaInfoIntegrityService.HydrateCore(item, "Explicit Reliability Repair");
                var fresh = _libraryManager.GetItemById(item.InternalId) ?? item;
                result.After = MediaInfoIntegrityService.Assess(fresh);
                if (!result.Success && result.After.CoreMediaInfoComplete) result.Success = true;
                if (!result.Success)
                    result.Error = "A validated snapshot existed, but core MediaInfo could not be hydrated into the Emby item repository.";
            }
            catch (Exception ex)
            {
                result.Error = ex.GetBaseException().Message;
                result.After = MediaInfoIntegrityService.Assess(_libraryManager.GetItemById(item.InternalId) ?? item);
            }

            return result;
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
