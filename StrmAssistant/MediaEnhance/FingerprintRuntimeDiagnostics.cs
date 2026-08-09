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
        public string Note { get; set; }
    }

    /// <summary>
    /// Read-only reflection diagnostics around the already-initialized FingerprintApi.
    /// This intentionally does not instantiate private Emby types or modify fingerprint files.
    /// </summary>
    public sealed class FingerprintRuntimeDiagnostics
    {
        public FingerprintRuntimeCapabilityResult Inspect(FingerprintApi fingerprintApi)
        {
            var result = new FingerprintRuntimeCapabilityResult
            {
                FingerprintApiAvailable = fingerprintApi != null,
                EmbyApplicationVersion = Plugin.Instance?.ApplicationHost?.ApplicationVersion?.ToString(),
                DistributedFingerprintRoutingEnabled = false,
                Note = "Diagnostics only. Distributed fingerprint routing is intentionally disabled until the native runtime contract and Chromaprint worker behavior are verified."
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
            }
            catch (Exception ex)
            {
                result.Note = "Fingerprint runtime inspection failed: " + ex.Message;
            }

            return result;
        }
    }
}