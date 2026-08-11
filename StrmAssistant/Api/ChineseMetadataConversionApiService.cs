using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using StrmAssistant.Metadata;
using System.Collections.Generic;

namespace StrmAssistant.Api
{
    public sealed class ChineseMetadataConversionStatus
    {
        public ChineseMetadataConversionOptions Options { get; set; }
        public string SettingsPath { get; set; }
        public ChineseMetadataConversionCapabilityStatus RuntimePatch { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/Metadata/ChineseConversion", "GET",
        Summary = "Get MovieDb Traditional-to-Simplified result conversion settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetChineseMetadataConversionSettings : IReturn<ChineseMetadataConversionStatus>
    {
    }

    [Route("/StrmAssistant/Metadata/ChineseConversion", "POST",
        Summary = "Update MovieDb Traditional-to-Simplified result conversion settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class SaveChineseMetadataConversionSettings : IReturn<ChineseMetadataConversionStatus>
    {
        public bool Enabled { get; set; }
        public bool ConvertName { get; set; } = true;
        public bool ConvertOverview { get; set; } = true;
        public bool ConvertTagline { get; set; } = true;
        public bool ConvertPersonName { get; set; } = true;
        public bool OnlyForSimplifiedChineseRequests { get; set; } = true;
    }

    public sealed class ChineseMetadataConversionApiService : BaseApiService
    {
        public object Get(GetChineseMetadataConversionSettings request)
        {
            return BuildStatus();
        }

        public object Post(SaveChineseMetadataConversionSettings request)
        {
            ChineseMetadataConversionRuntimeSettings.Save(new ChineseMetadataConversionOptions
            {
                Enabled = request?.Enabled == true,
                ConvertName = request?.ConvertName != false,
                ConvertOverview = request?.ConvertOverview != false,
                ConvertTagline = request?.ConvertTagline != false,
                ConvertPersonName = request?.ConvertPersonName != false,
                OnlyForSimplifiedChineseRequests = request?.OnlyForSimplifiedChineseRequests != false
            });
            return BuildStatus();
        }

        private static ChineseMetadataConversionStatus BuildStatus()
        {
            var options = ChineseMetadataConversionRuntimeSettings.GetSnapshot();
            var status = new ChineseMetadataConversionStatus
            {
                Options = options,
                SettingsPath = ChineseMetadataConversionRuntimeSettings.SettingsPath,
                RuntimePatch = ChineseMetadataConversionModState.Status
            };
            if (options.Enabled && status.RuntimePatch?.PatchedProviders == 0)
                status.Warnings.Add("Chinese metadata conversion is enabled but no compatible MovieDb GetMetadata provider was patched.");
            return status;
        }
    }
}
