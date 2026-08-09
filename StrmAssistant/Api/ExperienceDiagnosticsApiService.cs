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
                SeriesSeasonCollectionApiAvailable = true,
                SeriesSeasonCollectionEndpoint = "/StrmAssistant/SeriesCollections/{SeriesId}?UserId={UserId}"
            };

            if (result.NotificationEnhanceEnabled && result.NativeLibraryNotificationPatch?.Patched != true)
                result.Notes.Add("Notification enhancement is enabled but the native NewLibraryContent patch is not active.");
            result.Notes.Add("Series collection aggregation is read-only: direct Series membership is unioned with direct Season membership; no BoxSet is modified.");
            result.Notes.Add("Copied-user notification cleanup remains intentionally disabled until the exact Emby copy-user request sequence can be identified without affecting ordinary new-user creation.");
            return result;
        }
    }
}
