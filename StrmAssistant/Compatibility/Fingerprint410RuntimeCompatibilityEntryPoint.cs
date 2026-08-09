using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using StrmAssistant.Common;
using StrmAssistant.MediaEnhance;
using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Emby 4.10 changed the concrete Task result returned by
    /// AudioFingerprintManager.GetAllFingerprintFilesForSeason and newer builds add a
    /// CancellationToken to UpdateSequencesForSeason. Patching the old private wrapper methods
    /// is unsafe because their exception filters can make Harmony fail IL generation. Instead,
    /// intercept the public season workflow and invoke Emby's runtime methods directly.
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
                var nativeSeasonMethod = typeof(FingerprintApi).GetField(
                    "_getAllFingerprintFilesForSeason", flags)?.GetValue(Plugin.FingerprintApi) as MethodInfo;
                var nativeUpdateMethod = typeof(FingerprintApi).GetField(
                    "_updateSequencesForSeason", flags)?.GetValue(Plugin.FingerprintApi) as MethodInfo;

                status.SeasonFingerprintTargetFound = nativeSeasonMethod != null;
                status.UpdateSequenceTargetFound = nativeUpdateMethod != null;
                status.NativeSeasonFingerprintParameterCount = nativeSeasonMethod?.GetParameters().Length ?? 0;
                status.NativeUpdateSequenceParameterCount = nativeUpdateMethod?.GetParameters().Length ?? 0;

                if (nativeSeasonMethod == null || nativeUpdateMethod == null)
                {
                    status.Error = "Emby AudioFingerprintManager runtime methods were not found.";
                    return;
                }

                var target = typeof(FingerprintApi).GetMethod(
                    "UpdateIntroMarkerForSeason",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(Season), typeof(CancellationToken), typeof(IProgress<double>) },
                    null);

                if (target == null)
                {
                    status.Error = "FingerprintApi.UpdateIntroMarkerForSeason runtime entry was not found.";
                    return;
                }

                var prefix = typeof(Fingerprint410Patches).GetMethod(
                    nameof(Fingerprint410Patches.UpdateIntroMarkerForSeasonPrefix),
                    BindingFlags.Static | BindingFlags.Public);

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                status.SeasonFingerprintPatched = true;
                status.UpdateSequencePatched = true;
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
        public static bool UpdateIntroMarkerForSeasonPrefix(
            FingerprintApi __instance,
            Season season,
            CancellationToken cancellationToken,
            IProgress<double> progress,
            ref Task __result)
        {
            __result = RunCompatibleSeasonWorkflowAsync(__instance, season, cancellationToken, progress);
            return false;
        }

        private static async Task RunCompatibleSeasonWorkflowAsync(
            FingerprintApi instance,
            Season season,
            CancellationToken cancellationToken,
            IProgress<double> progress)
        {
            if (instance == null || season == null) return;

            var libraryManager = GetPrivateField<ILibraryManager>(instance, "_libraryManager");
            var fileSystem = GetPrivateField<IFileSystem>(instance, "_fileSystem");
            var nativeManager = GetPrivateField<object>(instance, "_audioFingerprintManager");
            var getAllMethod = GetPrivateField<MethodInfo>(instance, "_getAllFingerprintFilesForSeason");
            var updateMethod = GetPrivateField<MethodInfo>(instance, "_updateSequencesForSeason");

            if (libraryManager == null || fileSystem == null || nativeManager == null ||
                getAllMethod == null || updateMethod == null)
                throw new InvalidOperationException("Fingerprint runtime dependencies could not be resolved.");

            var fingerprintMinutes = instance.GetFingerprintMinutes(season);
            var libraryOptions = libraryManager.GetLibraryOptions(season);
            libraryOptions.IntroDetectionFingerprintLength = fingerprintMinutes;
            var directoryService = new DirectoryService(Plugin.Instance.Logger, fileSystem);

            var episodeQuery = new InternalItemsQuery
            {
                GroupByPresentationUniqueKey = false,
                EnableTotalRecordCount = false,
                MinRunTimeTicks = TimeSpan.FromMinutes(fingerprintMinutes).Ticks,
                HasIntroDetectionFailure = false,
                HasAudioStream = true
            };

            var allEpisodes = MediaExtractionFilter.Apply(
                    season.GetEpisodes(episodeQuery).Items.OfType<Episode>())
                .ToArray();

            episodeQuery.WithoutChapterMarkers = new[] { MarkerType.IntroStart };
            var episodesWithoutMarkers = MediaExtractionFilter.Apply(
                    season.GetEpisodes(episodeQuery).Items.OfType<Episode>())
                .ToList();

            var manager = SelectFingerprintManager(instance, allEpisodes, out var distributed) ?? nativeManager;

            try
            {
                await RunWithManagerAsync(manager, season, allEpisodes, episodesWithoutMarkers,
                        libraryOptions, directoryService, getAllMethod, updateMethod, cancellationToken, progress)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (distributed && ShouldFallbackToNative())
            {
                Plugin.Instance?.Logger?.Warn(
                    "IntroFingerprintExtract - 4.10 distributed season workflow failed for {0}; falling back to Emby native ffmpeg. {1}",
                    season.Path, ex.Message);

                await RunWithManagerAsync(nativeManager, season, allEpisodes, episodesWithoutMarkers,
                        libraryOptions, directoryService, getAllMethod, updateMethod, cancellationToken, progress)
                    .ConfigureAwait(false);
            }
        }

        private static async Task RunWithManagerAsync(
            object manager,
            Season season,
            Episode[] allEpisodes,
            IList<Episode> episodesWithoutMarkers,
            LibraryOptions libraryOptions,
            IDirectoryService directoryService,
            MethodInfo getAllMethod,
            MethodInfo updateMethod,
            CancellationToken cancellationToken,
            IProgress<double> progress)
        {
            object invoked;
            try
            {
                invoked = getAllMethod.Invoke(manager,
                    new object[]
                    {
                        season, allEpisodes, libraryOptions, directoryService, cancellationToken
                    });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }

            if (!(invoked is Task task))
                throw new InvalidOperationException("GetAllFingerprintFilesForSeason did not return Task.");

            await task.ConfigureAwait(false);
            var seasonFingerprintInfo = task.GetType()
                .GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(task);

            double total = episodesWithoutMarkers.Count;
            var index = 0;

            foreach (var episode in episodesWithoutMarkers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var parameterCount = updateMethod.GetParameters().Length;
                var args = parameterCount >= 6
                    ? new object[]
                    {
                        season, seasonFingerprintInfo, episode, libraryOptions, directoryService, cancellationToken
                    }
                    : new object[]
                    {
                        season, seasonFingerprintInfo, episode, libraryOptions, directoryService
                    };

                try
                {
                    updateMethod.Invoke(manager, args);
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    throw ex.InnerException;
                }

                index++;
                progress?.Report(total == 0 ? 1.0 : index / total);
            }

            progress?.Report(1.0);
        }

        private static object SelectFingerprintManager(FingerprintApi instance, Episode[] episodes,
            out bool distributed)
        {
            distributed = false;
            try
            {
                var method = typeof(FingerprintApi)
                    .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .FirstOrDefault(candidate =>
                    {
                        if (!string.Equals(candidate.Name, "SelectFingerprintManager", StringComparison.Ordinal))
                            return false;
                        var parameters = candidate.GetParameters();
                        return parameters.Length == 2 &&
                               parameters[0].ParameterType == typeof(IEnumerable<Episode>) &&
                               parameters[1].ParameterType == typeof(bool).MakeByRefType();
                    });

                if (method == null) return null;
                var args = new object[] { episodes, false };
                var manager = method.Invoke(instance, args);
                if (args[1] is bool value) distributed = value;
                return manager;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        private static bool ShouldFallbackToNative()
        {
            return Plugin.Instance?.GetPluginOptions()?.IntroSkipOptions?.DistributedFingerprintFallbackToEmby != false;
        }

        private static T GetPrivateField<T>(object target, string fieldName) where T : class
        {
            return target?.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(target) as T;
        }
    }
}
