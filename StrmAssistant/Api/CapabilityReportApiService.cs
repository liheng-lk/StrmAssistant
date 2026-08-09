using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using StrmAssistant.MediaEnhance;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    public sealed class CapabilityOptionSummary
    {
        public int MasterConcurrency { get; set; }
        public int Tier2Concurrency { get; set; }
        public bool CatchupMode { get; set; }
        public bool DistributedMediaInfo { get; set; }
        public bool DistributedChapterImages { get; set; }
        public bool DistributedFingerprint { get; set; }
        public bool DistributedFingerprintForStrm { get; set; }
        public int DefaultFingerprintMinutes { get; set; }
        public string FingerprintDurationOverrides { get; set; }
        public bool OpticalProbe { get; set; }
        public bool OpticalWriteBack { get; set; }
        public bool CustomImageCapture { get; set; }
        public bool SharedMediaInfoSync { get; set; }
        public bool SharedMediaInfoSyncRequiresMapping { get; set; }
        public string MediaInfoSharedRoot { get; set; }
        public string MediaInfoSyncPathMappings { get; set; }
        public bool DeepDelete { get; set; }
        public bool DeepDeleteDryRun { get; set; }
        public bool NotificationEnhance { get; set; }
    }

    public sealed class CapabilityReport
    {
        public string GeneratedUtc { get; set; }
        public string EmbyVersion { get; set; }
        public string PluginAssemblyVersion { get; set; }
        public string PluginAssemblyName { get; set; }
        public bool ModSupported { get; set; }
        public CapabilityOptionSummary Options { get; set; }
        public DistributedExtractHealthResult DistributedTools { get; set; }
        public FingerprintRuntimeCapabilityResult Fingerprint { get; set; }
        public OpticalProbeHealthResponse OpticalProbe { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/Diagnostics/Capabilities", "GET",
        Summary = "Generate a unified read-only Strm Assistant capability report")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetCapabilityReport : IReturn<CapabilityReport>
    {
        public bool RunChromaprintTest { get; set; }
        public bool RunVulkanTest { get; set; }
    }

    /// <summary>
    /// Aggregates existing read-only diagnostics into one report intended for full-feature testing.
    /// The optional active tests use synthetic media only and never mutate library items.
    /// </summary>
    public sealed class CapabilityReportApiService : BaseApiService
    {
        private readonly DistributedExtractDiagnostics _distributedDiagnostics = new DistributedExtractDiagnostics();
        private readonly FingerprintRuntimeDiagnostics _fingerprintDiagnostics = new FingerprintRuntimeDiagnostics();
        private readonly OpticalMediaProbe _opticalProbe;

        public CapabilityReportApiService(IJsonSerializer jsonSerializer)
        {
            _opticalProbe = new OpticalMediaProbe(jsonSerializer);
        }

        public async Task<object> Get(GetCapabilityReport request)
        {
            var plugin = Plugin.Instance;
            var pluginOptions = plugin?.GetPluginOptions();
            var mediaOptions = pluginOptions?.MediaInfoExtractOptions;
            var introOptions = pluginOptions?.IntroSkipOptions;
            var generalOptions = pluginOptions?.GeneralOptions;
            var experienceOptions = pluginOptions?.ExperienceEnhanceOptions;

            var distributedTask = _distributedDiagnostics.CheckAsync(mediaOptions,
                request?.RunVulkanTest == true, request?.RunChromaprintTest == true, CancellationToken.None);
            var opticalTask = _opticalProbe.CheckHealthAsync(mediaOptions, CancellationToken.None);

            await Task.WhenAll(distributedTask, opticalTask).ConfigureAwait(false);

            var optical = await opticalTask.ConfigureAwait(false);
            var assembly = typeof(Plugin).GetTypeInfo().Assembly.GetName();
            var report = new CapabilityReport
            {
                GeneratedUtc = DateTimeOffset.UtcNow.ToString("O"),
                EmbyVersion = plugin?.ApplicationHost?.ApplicationVersion?.ToString(),
                PluginAssemblyName = assembly.Name,
                PluginAssemblyVersion = assembly.Version?.ToString(),
                ModSupported = plugin?.IsModSupported == true,
                Options = new CapabilityOptionSummary
                {
                    MasterConcurrency = generalOptions?.MaxConcurrentCount ?? 0,
                    Tier2Concurrency = generalOptions?.Tier2MaxConcurrentCount ?? 0,
                    CatchupMode = generalOptions?.CatchupMode == true,
                    DistributedMediaInfo = mediaOptions?.EnableDistributedExtractRouting == true,
                    DistributedChapterImages = mediaOptions?.EnableDistributedChapterImageRouting == true,
                    DistributedFingerprint = introOptions?.EnableDistributedFingerprintRouting == true,
                    DistributedFingerprintForStrm = introOptions?.EnableDistributedFingerprintForStrm == true,
                    DefaultFingerprintMinutes = introOptions?.IntroDetectionFingerprintMinutes ?? 0,
                    FingerprintDurationOverrides = introOptions?.FingerprintDurationOverrides,
                    OpticalProbe = mediaOptions?.EnableOpticalMediaProbe == true,
                    OpticalWriteBack = mediaOptions?.EnableOpticalMediaWriteBack == true,
                    CustomImageCapture = mediaOptions?.EnableCustomImageCapture == true,
                    SharedMediaInfoSync = mediaOptions?.EnableMediaInfoSharedSync == true,
                    SharedMediaInfoSyncRequiresMapping = mediaOptions?.MediaInfoSyncRequireMappedPath != false,
                    MediaInfoSharedRoot = mediaOptions?.MediaInfoJsonRootFolder,
                    MediaInfoSyncPathMappings = mediaOptions?.MediaInfoSyncPathMappings,
                    DeepDelete = experienceOptions?.EnableDeepDelete == true,
                    DeepDeleteDryRun = experienceOptions?.DeepDeleteDryRun != false,
                    NotificationEnhance = experienceOptions?.EnableNotificationEnhance == true
                },
                DistributedTools = await distributedTask.ConfigureAwait(false),
                Fingerprint = _fingerprintDiagnostics.Inspect(Plugin.FingerprintApi),
                OpticalProbe = new OpticalProbeHealthResponse
                {
                    Success = optical.Success,
                    Enabled = mediaOptions?.EnableOpticalMediaProbe == true,
                    Executable = optical.Executable,
                    Version = optical.Version,
                    HasBlurayProtocol = optical.HasBlurayProtocol,
                    Error = optical.Error
                }
            };

            if (report.Options.DistributedFingerprint &&
                report.DistributedTools?.Ffmpeg?.ChromaprintTestPassed == false)
                report.Warnings.Add("Distributed fingerprint routing is enabled but the active Chromaprint test failed.");

            if (report.Options.OpticalProbe && report.OpticalProbe?.HasBlurayProtocol != true)
                report.Warnings.Add("Optical probing is enabled but the configured ffprobe did not report the bluray protocol.");

            if (report.Options.SharedMediaInfoSync &&
                string.IsNullOrWhiteSpace(report.Options.MediaInfoSharedRoot))
                report.Warnings.Add("Shared MediaInfo sync is enabled but MediaInfoJsonRootFolder is empty.");

            if (report.Options.SharedMediaInfoSync && report.Options.SharedMediaInfoSyncRequiresMapping &&
                string.IsNullOrWhiteSpace(report.Options.MediaInfoSyncPathMappings))
                report.Warnings.Add("Shared MediaInfo sync requires portable path mappings, but no mapping rules are configured.");

            if (report.Options.DeepDelete && !report.Options.DeepDeleteDryRun)
                report.Warnings.Add("Deep Delete is enabled with Dry Run disabled. Keep real file-deletion tests on disposable media first.");

            return report;
        }
    }
}
