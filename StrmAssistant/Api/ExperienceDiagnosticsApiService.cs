using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using System;
using System.Collections.Generic;

namespace StrmAssistant.Api
{
    public sealed class ExperienceDiagnosticsResult
    {
        public string GeneratedUtc { get; set; }
        public bool NotificationEnhanceEnabled { get; set; }
        public LibraryNotificationDescriptionCapabilityStatus NativeLibraryNotificationPatch { get; set; }
        public RemovedEpisodeNotificationCapabilityStatus RemovedEpisodeNotificationBridge { get; set; }
        public bool CopiedUserNotificationCleanupEnabled { get; set; }
        public CopiedUserNotificationCleanupCapabilityStatus CopiedUserNotificationCleanup { get; set; }
        public bool LogLinesNewestFirstEnabled { get; set; }
        public LogLinesNewestFirstCapabilityStatus LogLinesNewestFirst { get; set; }
        public bool SeriesSeasonCollectionApiAvailable { get; set; }
        public string SeriesSeasonCollectionEndpoint { get; set; }
        public List<string> Notes { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/Diagnostics/Experience", "GET",
        Summary = "Get read-only experience-enhancement runtime diagnostics")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetExperienceDiagnostics : IReturn<ExperienceDiagnosticsResult> { }

    public sealed class ExperienceDiagnosticsApiService : BaseApiService
    {
        public object Get(GetExperienceDiagnostics request)
        {
            var options = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            var result = new ExperienceDiagnosticsResult
            {
                GeneratedUtc = DateTimeOffset.UtcNow.ToString("O"),
                NotificationEnhanceEnabled = options?.EnableNotificationEnhance == true,
                NativeLibraryNotificationPatch = LibraryNotificationDescriptionModState.Status,
                RemovedEpisodeNotificationBridge = RemovedEpisodeNotificationModState.Status,
                CopiedUserNotificationCleanupEnabled = options?.EnableNotificationEnhance == true &&
                                                        options.ClearCopiedUserNotificationSettings,
                CopiedUserNotificationCleanup = CopiedUserNotificationCleanupModState.Status,
                LogLinesNewestFirstEnabled = options?.DisplayLogLinesNewestFirst == true,
                LogLinesNewestFirst = LogLinesNewestFirstModState.Status,
                SeriesSeasonCollectionApiAvailable = true,
                SeriesSeasonCollectionEndpoint = "/StrmAssistant/SeriesCollections/{SeriesId}?UserId={UserId}"
            };

            if (result.NotificationEnhanceEnabled && result.NativeLibraryNotificationPatch?.Patched != true)
                result.Notes.Add("Notification enhancement is enabled but the native NewLibraryContent patch is not active.");
            if (result.NotificationEnhanceEnabled && result.RemovedEpisodeNotificationBridge?.Patched != true)
                result.Notes.Add("Notification enhancement is enabled but the removed-Episode ActivityLog ItemId bridge is not active; delete notifications will keep native description text.");
            if (result.CopiedUserNotificationCleanupEnabled && result.CopiedUserNotificationCleanup?.CloneCreateUserTargetFound != true)
                result.Notes.Add("Copied-user notification cleanup is enabled, but this Emby runtime does not expose the clone-user CreateUser overload. Normal user creation is left untouched.");
            if (result.CopiedUserNotificationCleanupEnabled && result.CopiedUserNotificationCleanup?.Patched == true &&
                (result.CopiedUserNotificationCleanup.DiscoveredNotificationSettingKeys == null ||
                 result.CopiedUserNotificationCleanup.DiscoveredNotificationSettingKeys.Count == 0))
                result.Notes.Add("The clone-user path is patched, but no unambiguous notification/notifier user-setting key was discovered. Clone cleanup will safely no-op until the runtime exposes one.");
            if (result.LogLinesNewestFirstEnabled && result.LogLinesNewestFirst?.Patched != true)
                result.Notes.Add("Newest-first log lines are enabled but the runtime /System/Logs/{Name}/Lines handler was not patched. Check RequestTypeFound/TargetFound/AsyncTargetUnsupported.");
            if (result.LogLinesNewestFirstEnabled && result.LogLinesNewestFirst?.Patched == true &&
                result.LogLinesNewestFirst.LastTransformSucceeded == false &&
                !string.IsNullOrWhiteSpace(result.LogLinesNewestFirst.LastTransformError))
                result.Notes.Add("The log-lines handler is patched, but the last response could not be reversed: " + result.LogLinesNewestFirst.LastTransformError);
            result.Notes.Add("Series collection aggregation is read-only: direct Series membership is unioned with direct Season membership; no BoxSet is modified.");
            return result;
        }
    }
}
