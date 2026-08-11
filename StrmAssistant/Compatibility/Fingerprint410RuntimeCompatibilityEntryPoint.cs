using MediaBrowser.Controller.Plugins;
using StrmAssistant.Common;
using System;
using System.Reflection;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class Fingerprint410CapabilityStatus
    {
        public bool SeasonFingerprintTargetFound { get; set; }
        public bool SeasonFingerprintPatched { get; set; }
        public bool UpdateSequenceTargetFound { get; set; }
        public bool UpdateSequencePatched { get; set; }
        public int NativeSeasonFingerprintParameterCount { get; set; }
        public int NativeUpdateSequenceParameterCount { get; set; }
        public string Error { get; set; }
    }

    public static class Fingerprint410CompatibilityState
    {
        public static Fingerprint410CapabilityStatus Status { get; internal set; } =
            new Fingerprint410CapabilityStatus();
    }

    /// <summary>
    /// Read-only capability probe for the native adaptive fingerprint wrapper. FingerprintApi
    /// itself now handles Emby 4.10's concrete Task&lt;T&gt; result and the 5/6-parameter
    /// UpdateSequencesForSeason signatures, so no Harmony patch is required here.
    /// </summary>
    public sealed class Fingerprint410RuntimeCompatibilityEntryPoint : IServerEntryPoint
    {
        public void Run()
        {
            var status = new Fingerprint410CapabilityStatus();
            Fingerprint410CompatibilityState.Status = status;

            try
            {
                var instance = Plugin.FingerprintApi;
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var nativeSeasonMethod = typeof(FingerprintApi).GetField(
                    "_getAllFingerprintFilesForSeason", flags)?.GetValue(instance) as MethodInfo;
                var nativeUpdateMethod = typeof(FingerprintApi).GetField(
                    "_updateSequencesForSeason", flags)?.GetValue(instance) as MethodInfo;

                status.SeasonFingerprintTargetFound = nativeSeasonMethod != null;
                status.UpdateSequenceTargetFound = nativeUpdateMethod != null;
                status.NativeSeasonFingerprintParameterCount = nativeSeasonMethod?.GetParameters().Length ?? 0;
                status.NativeUpdateSequenceParameterCount = nativeUpdateMethod?.GetParameters().Length ?? 0;

                var seasonWrapper = typeof(FingerprintApi).GetMethod(
                    "GetAllFingerprintFilesForSeason", flags);
                var updateWrapper = typeof(FingerprintApi).GetMethod(
                    "UpdateSequencesForSeason", flags);

                status.SeasonFingerprintPatched = seasonWrapper != null &&
                                                  seasonWrapper.ReturnType == typeof(Task<object>);
                status.UpdateSequencePatched = updateWrapper != null &&
                                               updateWrapper.GetParameters().Length == 7;

                if (!status.SeasonFingerprintTargetFound || !status.UpdateSequenceTargetFound)
                {
                    status.Error = "Emby AudioFingerprintManager runtime methods were not found.";
                }
                else if (!status.SeasonFingerprintPatched || !status.UpdateSequencePatched)
                {
                    status.Error = "FingerprintApi native adaptive wrappers were not detected.";
                }
                else if (Plugin.Instance?.DebugMode == true)
                {
                    Plugin.Instance.Logger.Debug(
                        "Fingerprint 4.10 native compatibility active: GetAll args={0}, UpdateSequences args={1}",
                        status.NativeSeasonFingerprintParameterCount,
                        status.NativeUpdateSequenceParameterCount);
                }
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Fingerprint 4.10 capability probe failed: " + status.Error);
            }
        }

        public void Dispose()
        {
        }
    }
}
