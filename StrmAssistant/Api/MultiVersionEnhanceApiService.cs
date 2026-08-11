using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using StrmAssistant.Experience;
using System.Collections.Generic;

namespace StrmAssistant.Api
{
    public sealed class MultiVersionEnhanceStatus
    {
        public MultiVersionRuntimeOptions Options { get; set; }
        public string SettingsPath { get; set; }
        public MultiVersionDisplayCapabilityStatus RuntimePatch { get; set; }
        public MultiVersionUserDataIsolationCapabilityStatus UserDataIsolationPatch { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/MultiVersion/Settings", "GET",
        Summary = "Get multi-version display enhancement settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetMultiVersionEnhanceSettings : IReturn<MultiVersionEnhanceStatus>
    {
    }

    [Route("/StrmAssistant/MultiVersion/Settings", "POST",
        Summary = "Update multi-version display enhancement settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class SaveMultiVersionEnhanceSettings : IReturn<MultiVersionEnhanceStatus>
    {
        public bool Enabled { get; set; }
        public bool RenameSources { get; set; } = true;
        public bool SortHighestQualityFirst { get; set; }
        public bool IncludeFileName { get; set; } = true;
        public bool IncludeContainer { get; set; }
        public string Separator { get; set; } = " · ";
        public bool IsolateUserDataPerVersion { get; set; }
    }

    public sealed class MultiVersionEnhanceApiService : BaseApiService
    {
        public object Get(GetMultiVersionEnhanceSettings request)
        {
            return BuildStatus();
        }

        public object Post(SaveMultiVersionEnhanceSettings request)
        {
            MultiVersionRuntimeSettings.Save(new MultiVersionRuntimeOptions
            {
                Enabled = request?.Enabled == true,
                RenameSources = request?.RenameSources != false,
                SortHighestQualityFirst = request?.SortHighestQualityFirst == true,
                IncludeFileName = request?.IncludeFileName != false,
                IncludeContainer = request?.IncludeContainer == true,
                Separator = request?.Separator,
                IsolateUserDataPerVersion = request?.IsolateUserDataPerVersion == true
            });
            return BuildStatus();
        }

        private static MultiVersionEnhanceStatus BuildStatus()
        {
            var status = new MultiVersionEnhanceStatus
            {
                Options = MultiVersionRuntimeSettings.GetSnapshot(),
                SettingsPath = MultiVersionRuntimeSettings.SettingsPath,
                RuntimePatch = MultiVersionDisplayModState.Status,
                UserDataIsolationPatch = MultiVersionUserDataIsolationModState.Status
            };

            if (status.Options.Enabled && status.RuntimePatch?.Patched != true)
                status.Warnings.Add("Multi-version display enhancement is enabled but Video.GetMediaSources was not patched on this Emby build.");
            if (status.Options.SortHighestQualityFirst)
                status.Warnings.Add("Quality sorting changes the returned media-source order and may change which version appears first/default in clients.");
            if (status.Options.IsolateUserDataPerVersion && status.UserDataIsolationPatch?.Patched != true)
                status.Warnings.Add("Per-version UserData isolation is enabled but Video.GetUserDataKeyInternal was not patched on this Emby build.");
            if (status.Options.IsolateUserDataPerVersion)
                status.Warnings.Add("Per-version UserData isolation uses new runtime keys and does not automatically migrate existing shared progress/favorites. Keep it disabled until final runtime contract testing is complete.");
            return status;
        }
    }
}
