using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using StrmAssistant.Metadata;
using System.Collections.Generic;

namespace StrmAssistant.Api
{
    public sealed class ChineseImageLanguageStatus
    {
        public ChineseImageLanguageOptions Options { get; set; }
        public string SettingsPath { get; set; }
        public ChineseImageLanguageCapabilityStatus RuntimePatch { get; set; }
        public List<string> PriorityLanguages { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/Metadata/ChineseImageLanguage", "GET",
        Summary = "Get Simplified/Traditional Chinese poster and logo priority settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetChineseImageLanguageSettings : IReturn<ChineseImageLanguageStatus>
    {
    }

    [Route("/StrmAssistant/Metadata/ChineseImageLanguage", "POST",
        Summary = "Update Simplified/Traditional Chinese poster and logo priority settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class SaveChineseImageLanguageSettings : IReturn<ChineseImageLanguageStatus>
    {
        public bool Enabled { get; set; }
        public string PreferredLanguage { get; set; } = "zh-CN";
        public string FallbackLanguages { get; set; } = "zh,zh-HK,zh-TW";
        public bool ApplyPrimary { get; set; } = true;
        public bool ApplyLogo { get; set; } = true;
    }

    public sealed class ChineseImageLanguageApiService : BaseApiService
    {
        public object Get(GetChineseImageLanguageSettings request)
        {
            return BuildStatus();
        }

        public object Post(SaveChineseImageLanguageSettings request)
        {
            ChineseImageLanguageRuntimeSettings.Save(new ChineseImageLanguageOptions
            {
                Enabled = request?.Enabled == true,
                PreferredLanguage = request?.PreferredLanguage,
                FallbackLanguages = request?.FallbackLanguages,
                ApplyPrimary = request?.ApplyPrimary != false,
                ApplyLogo = request?.ApplyLogo != false
            });
            return BuildStatus();
        }

        private static ChineseImageLanguageStatus BuildStatus()
        {
            var options = ChineseImageLanguageRuntimeSettings.GetSnapshot();
            var status = new ChineseImageLanguageStatus
            {
                Options = options,
                SettingsPath = ChineseImageLanguageRuntimeSettings.SettingsPath,
                RuntimePatch = ChineseImageLanguageModState.Status,
                PriorityLanguages = new List<string>(ChineseImageLanguageRuntimeSettings.GetPriorityLanguages(options))
            };
            if (options.Enabled && status.RuntimePatch?.Patched != true)
                status.Warnings.Add("Chinese image-language priority is enabled but ProviderManager.GetAvailableRemoteImages was not patched.");
            return status;
        }
    }
}
