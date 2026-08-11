using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using StrmAssistant.Experience;
using System.Collections.Generic;

namespace StrmAssistant.Api
{
    public sealed class UiSortStatus
    {
        public UiSortRuntimeOptions Options { get; set; }
        public string SettingsPath { get; set; }
        public NaturalTitleSortCapabilityStatus NaturalTitleSortRuntime { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/UI/Sort", "GET", Summary = "Get UI sorting enhancement settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetUiSortSettings : IReturn<UiSortStatus> { }

    [Route("/StrmAssistant/UI/Sort", "POST", Summary = "Update UI sorting enhancement settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class SaveUiSortSettings : IReturn<UiSortStatus>
    {
        public bool Enabled { get; set; }
        public bool NaturalTitleSort { get; set; }
        public bool ReverseSeasons { get; set; }
        public bool ReverseEpisodes { get; set; }
        public bool CollectionDateDescending { get; set; }
    }

    public sealed class UiSortApiService : BaseApiService
    {
        public object Get(GetUiSortSettings request) => BuildStatus();

        public object Post(SaveUiSortSettings request)
        {
            UiSortRuntimeSettings.Save(new UiSortRuntimeOptions
            {
                Enabled = request?.Enabled == true,
                NaturalTitleSort = request?.NaturalTitleSort == true,
                ReverseSeasons = request?.ReverseSeasons == true,
                ReverseEpisodes = request?.ReverseEpisodes == true,
                CollectionDateDescending = request?.CollectionDateDescending == true
            });
            return BuildStatus();
        }

        private static UiSortStatus BuildStatus()
        {
            var options = UiSortRuntimeSettings.GetSnapshot();
            var result = new UiSortStatus
            {
                Options = options,
                SettingsPath = UiSortRuntimeSettings.SettingsPath,
                NaturalTitleSortRuntime = NaturalTitleSortModState.Status
            };

            if (options.Enabled && options.NaturalTitleSort && result.NaturalTitleSortRuntime?.TargetsPatched <= 0)
                result.Warnings.Add("Natural title sorting is enabled but BaseItem.CreateSortName was not patched on this runtime.");
            if (options.ReverseSeasons)
                result.Warnings.Add("ReverseSeasons is stored but not active yet; the version-specific query-order adapter is still under development.");
            if (options.ReverseEpisodes)
                result.Warnings.Add("ReverseEpisodes is stored but not active yet; the version-specific query-order adapter is still under development.");
            if (options.CollectionDateDescending)
                result.Warnings.Add("CollectionDateDescending is stored but not active yet; the collection child-order adapter is still under development.");
            return result;
        }
    }
}
