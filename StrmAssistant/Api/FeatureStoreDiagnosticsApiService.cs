using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using StrmAssistant.Experience;
using StrmAssistant.Metadata;
using System;
using System.Collections.Generic;

namespace StrmAssistant.Api
{
    public sealed class FeatureStoreDiagnosticsResult
    {
        public string GeneratedUtc { get; set; }
        public ChineseImageLanguageOptions ChineseImageLanguage { get; set; }
        public ChineseImageLanguageCapabilityStatus ChineseImageLanguagePatch { get; set; }
        public ChineseMetadataConversionOptions ChineseMetadataConversion { get; set; }
        public ChineseMetadataConversionCapabilityStatus ChineseMetadataConversionPatch { get; set; }
        public MultiVersionRuntimeOptions MultiVersionDisplay { get; set; }
        public MultiVersionDisplayCapabilityStatus MultiVersionDisplayPatch { get; set; }
        public List<string> ChineseImagePriorityLanguages { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/Diagnostics/FeatureStores", "GET",
        Summary = "Report standalone runtime feature stores and their patch state")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetFeatureStoreDiagnostics : IReturn<FeatureStoreDiagnosticsResult>
    {
    }

    /// <summary>
    /// Read-only aggregation for feature settings that intentionally live outside the main
    /// GenericEdit option model. It does not save settings or touch media/user data.
    /// </summary>
    public sealed class FeatureStoreDiagnosticsApiService : BaseApiService
    {
        public object Get(GetFeatureStoreDiagnostics request)
        {
            var imageOptions = ChineseImageLanguageRuntimeSettings.GetSnapshot();
            var conversionOptions = ChineseMetadataConversionRuntimeSettings.GetSnapshot();
            var multiVersionOptions = MultiVersionRuntimeSettings.GetSnapshot();

            var result = new FeatureStoreDiagnosticsResult
            {
                GeneratedUtc = DateTimeOffset.UtcNow.ToString("O"),
                ChineseImageLanguage = imageOptions,
                ChineseImageLanguagePatch = ChineseImageLanguageModState.Status,
                ChineseMetadataConversion = conversionOptions,
                ChineseMetadataConversionPatch = ChineseMetadataConversionModState.Status,
                MultiVersionDisplay = multiVersionOptions,
                MultiVersionDisplayPatch = MultiVersionDisplayModState.Status,
                ChineseImagePriorityLanguages = new List<string>(
                    ChineseImageLanguageRuntimeSettings.GetPriorityLanguages(imageOptions))
            };

            if (imageOptions.Enabled && result.ChineseImageLanguagePatch?.Patched != true)
                result.Warnings.Add("Chinese image-language priority is enabled but its ProviderManager patch is inactive.");
            if (conversionOptions.Enabled && result.ChineseMetadataConversionPatch?.PatchedProviders == 0)
                result.Warnings.Add("Chinese metadata conversion is enabled but no MovieDb metadata provider patch is active.");
            if (multiVersionOptions.Enabled && result.MultiVersionDisplayPatch?.Patched != true)
                result.Warnings.Add("Multi-version display enhancement is enabled but Video.GetMediaSources is not patched.");

            return result;
        }
    }
}
