using HarmonyLib;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using StrmAssistant.Common;
using System;
using System.Reflection;
using System.Threading;
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
    /// Adapts the wrapper around Emby's internal AudioFingerprintManager without replacing
    /// Strm Assistant's native/distributed fingerprint routing. Emby 4.10 changed the concrete
    /// Task result type returned by GetAllFingerprintFilesForSeason and added a CancellationToken
    /// to UpdateSequencesForSeason on newer builds.
    /// </summary>
    public sealed class Fingerprint410RuntimeCompatibilityEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.fingerprint-410";
        private Harmony _harmony;

        public void Run()
        {
            var status = new Fingerprint410CapabilityStatus();
            Fingerprint410CompatibilityState.Status = status;

            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var seasonTarget = typeof(FingerprintApi).GetMethod(
                    "GetAllFingerprintFilesForSeason", flags);
                var updateTarget = typeof(FingerprintApi).GetMethod(
                    "UpdateSequencesForSeason", flags);

                status.SeasonFingerprintTargetFound = seasonTarget != null;
                status.UpdateSequenceTargetFound = updateTarget != null;

                var nativeSeasonMethod = typeof(FingerprintApi).GetField(
                    "_getAllFingerprintFilesForSeason", flags)?.GetValue(Plugin.FingerprintApi) as MethodInfo;
                var nativeUpdateMethod = typeof(FingerprintApi).GetField(
                    "_updateSequencesForSeason", flags)?.GetValue(Plugin.FingerprintApi) as MethodInfo;
                status.NativeSeasonFingerprintParameterCount = nativeSeasonMethod?.GetParameters().Length ?? 0;
                status.NativeUpdateSequenceParameterCount = nativeUpdateMethod?.GetParameters().Length ?? 0;

                _harmony = new Harmony(HarmonyId);

                if (seasonTarget != null)
                {
                    var prefix = typeof(Fingerprint410Patches).GetMethod(
                        nameof(Fingerprint410Patches.GetAllFingerprintFilesForSeasonPrefix),
                        BindingFlags.Static | BindingFlags.Public);
                    _harmony.Patch(seasonTarget, prefix: new HarmonyMethod(prefix));
                    status.SeasonFingerprintPatched = true;
                }

                if (updateTarget != null)
                {
                    var prefix = typeof(Fingerprint410Patches).GetMethod(
                        nameof(Fingerprint410Patches.UpdateSequencesForSeasonPrefix),
                        BindingFlags.Static | BindingFlags.Public);
                    _harmony.Patch(updateTarget, prefix: new HarmonyMethod(prefix));
                    status.UpdateSequencePatched = true;
                }
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Fingerprint 4.10 compatibility unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }
    }

    public static class Fingerprint410Patches
    {
        public static bool GetAllFingerprintFilesForSeasonPrefix(
            FingerprintApi __instance,
            object manager,
            Season season,
            Episode[] episodes,
            LibraryOptions libraryOptions,
            IDirectoryService directoryService,
            CancellationToken cancellationToken,
            ref Task<object> __result)
        {
            try
            {
                var nativeMethod = typeof(FingerprintApi).GetField(
                    "_getAllFingerprintFilesForSeason",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(__instance) as MethodInfo;

                if (nativeMethod == null || manager == null)
                    return true;

                var invoked = nativeMethod.Invoke(manager,
                    new object[] { season, episodes, libraryOptions, directoryService, cancellationToken });

                if (!(invoked is Task task))
                    return true;

                __result = AwaitTaskResultAsync(task);
                return false;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                __result = Task.FromException<object>(ex.InnerException);
                return false;
            }
            catch (Exception ex)
            {
                __result = Task.FromException<object>(ex);
                return false;
            }
        }

        private static async Task<object> AwaitTaskResultAsync(Task task)
        {
            await task.ConfigureAwait(false);
            return task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(task);
        }

        public static bool UpdateSequencesForSeasonPrefix(
            FingerprintApi __instance,
            object manager,
            Season season,
            object seasonFingerprintInfo,
            Episode episode,
            LibraryOptions libraryOptions,
            IDirectoryService directoryService)
        {
            var nativeMethod = typeof(FingerprintApi).GetField(
                "_updateSequencesForSeason",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(__instance) as MethodInfo;

            if (nativeMethod == null || manager == null)
                return true;

            try
            {
                var parameterCount = nativeMethod.GetParameters().Length;
                object[] args;
                if (parameterCount >= 6)
                {
                    args = new object[]
                    {
                        season, seasonFingerprintInfo, episode, libraryOptions, directoryService,
                        CancellationToken.None
                    };
                }
                else
                {
                    args = new object[]
                    {
                        season, seasonFingerprintInfo, episode, libraryOptions, directoryService
                    };
                }

                nativeMethod.Invoke(manager, args);
                return false;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }
}
