using StrmAssistant.Common;
using System;
using System.Reflection;

namespace StrmAssistant.MediaEnhance
{
    public sealed class FingerprintRuntimeCapabilityResult
    {
        public bool FingerprintApiAvailable { get; set; }
        public bool NativeManagerAvailable { get; set; }
        public string NativeManagerType { get; set; }
        public string NativeManagerAssembly { get; set; }
        public string NativeManagerAssemblyVersion { get; set; }
        public bool CreateTitleFingerprintAvailable { get; set; }
        public string CreateTitleFingerprintSignature { get; set; }
        public bool GetAllFingerprintFilesForSeasonAvailable { get; set; }
        public string GetAllFingerprintFilesForSeasonSignature { get; set; }
        public bool UpdateSequencesForSeasonAvailable { get; set; }
        public string UpdateSequencesForSeasonSignature { get; set; }
        public bool TimeoutPatchAvailable { get; set; }
        public string EmbyApplicationVersion { get; set; }
        public bool DistributedFingerprintRoutingEnabled { get; set; }
        public string DistributedFingerprintExecutable { get; set; }
        public bool DistributedFingerprintFallbackToEmby { get; set; }
        public bool DistributedFingerprintForStrm { get; set; }
        public bool DistributedFingerprintExecutableConfigured { get; set; }
        public string Note { get; set; }
    }

    /// <summary>
    /// Read-only reflection diagnostics around the already-initialized FingerprintApi.
    /// It reports routing configuration but never generates or modifies a real fingerprint.
    /// </summary>
    public sealed class FingerprintRuntimeDiagnostics
    {
        public FingerprintRuntimeCapabilityResult Inspect(FingerprintApi fingerprintApi)
        {
            var pluginOptions = Plugin.Instance?.GetPluginOptions();
            var introOptions = pluginOptions?.IntroSkipOptions;
            var mediaOptions = pluginOptions?.MediaInfoExtractOptions;
            var executable = mediaOptions?.DistributedFfmpegExecutablePath?.Trim().Trim('"');
            var routingEnabled = introOptions?.EnableDistributedFingerprintRouting == true;

            var result = new FingerprintRuntimeCapabilityResult
            {
                FingerprintApiAvailable = fingerprintApi != null,
                EmbyApplicationVersion = Plugin.Instance?.ApplicationHost?.ApplicationVersion?.ToString(),
                DistributedFingerprintRoutingEnabled = routingEnabled,
                DistributedFingerprintExecutable = executable,
                DistributedFingerprintExecutableConfigured = !string.IsNullOrWhiteSpace(executable),
                DistributedFingerprintFallbackToEmby = introOptions?.DistributedFingerprintFallbackToEmby != false,
                DistributedFingerprintForStrm = introOptions?.EnableDistributedFingerprintForStrm == true,
                Note = routingEnabled
                    ? "Distributed routing is configured to create an isolated native AudioFingerprintManager whose ffmpeg path is overridden through interface proxies. Emby's global ffmpeg configuration is not modified. Runtime worker/path/Chromaprint verification is still required."
                    : "Distributed fingerprint routing is disabled. Native Emby fingerprint execution remains active."
            };

            if (fingerprintApi == null) return result;

            try
            {
                var apiType = fingerprintApi.GetType();
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;

                var manager = apiType.GetField("_audioFingerprintManager", flags)?.GetValue(fingerprintApi);
                var createTitleFingerprint = apiType.GetField("_createTitleFingerprint", flags)?.GetValue(fingerprintApi) as MethodInfo;
                var getAllFingerprintFilesForSeason = apiType.GetField("_getAllFingerprintFilesForSeason", flags)?.GetValue(fingerprintApi) as MethodInfo;
                var updateSequencesForSeason = apiType.GetField("_updateSequencesForSeason", flags)?.GetValue(fingerprintApi) as MethodInfo;
                var timeoutField = apiType.GetField("_timeoutMs", flags)?.GetValue(fingerprintApi) as FieldInfo;

                result.NativeManagerAvailable = manager != null;
                if (manager != null)
                {
                    var managerType = manager.GetType();
                    result.NativeManagerType = managerType.FullName;
                    result.NativeManagerAssembly = managerType.Assembly.GetName().Name;
                    result.NativeManagerAssemblyVersion = managerType.Assembly.GetName().Version?.ToString();
                }

                result.CreateTitleFingerprintAvailable = createTitleFingerprint != null;
                result.CreateTitleFingerprintSignature = createTitleFingerprint?.ToString();
                result.GetAllFingerprintFilesForSeasonAvailable = getAllFingerprintFilesForSeason != null;
                result.GetAllFingerprintFilesForSeasonSignature = getAllFingerprintFilesForSeason?.ToString();
                result.UpdateSequencesForSeasonAvailable = updateSequencesForSeason != null;
                result.UpdateSequencesForSeasonSignature = updateSequencesForSeason?.ToString();
                result.TimeoutPatchAvailable = timeoutField != null;

                if (routingEnabled && string.IsNullOrWhiteSpace(executable))
                {
                    result.Note += " No distributed ffmpeg executable is currently configured, so runtime routing will either fall back to native Emby or refuse the task according to the fallback setting.";
                }
            }
            catch (Exception ex)
            {
                result.Note = "Fingerprint runtime inspection failed: " + ex.Message;
            }

            return result;
        }
    }
}
