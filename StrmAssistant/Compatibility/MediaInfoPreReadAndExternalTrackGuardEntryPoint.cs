using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using StrmAssistant.Common;
using StrmAssistant.MediaEnhance;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class MediaInfoPreReadGuardStatus
    {
        public int MediaSourceTargetsPatched { get; set; }
        public bool ExternalTrackTargetPatched { get; set; }
        public long PreReadRestoreAttempts { get; set; }
        public long PreReadRestoreSucceeded { get; set; }
        public long PreReadRestoreFailed { get; set; }
        public long ExternalTrackWritesBlocked { get; set; }
        public long ExternalTrackBaselineRecovered { get; set; }
        public long ExternalTrackPostWriteRepairs { get; set; }
        public string LastItemPath { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class MediaInfoPreReadGuardState
    {
        public static MediaInfoPreReadGuardStatus Status { get; internal set; } = new MediaInfoPreReadGuardStatus();
    }

    /// <summary>
    /// Prevents two runtime failure modes which are especially painful for STRM/cloud media:
    /// 1) a provider/library refresh removes database MediaStreams and the first playback then pays for a new probe;
    /// 2) an external subtitle/audio reconciliation observes an incomplete stream baseline and replaces the repository
    ///    with only external tracks.
    ///
    /// The guard never probes media. It only restores an already persisted StrmAssistant snapshot and refuses an
    /// unsafe external-track write. All patches are runtime-discovered so an Emby signature change disables only the
    /// affected guard instead of preventing the plugin from loading.
    /// </summary>
    public sealed class MediaInfoPreReadAndExternalTrackGuardEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.mediainfo-preread-guard";
        private Harmony _harmony;

        public void Run()
        {
            var status = new MediaInfoPreReadGuardStatus();
            MediaInfoPreReadGuardState.Status = status;

            try
            {
                _harmony = new Harmony(HarmonyId);

                var mediaSourceManager = Plugin.Instance?.ApplicationHost?.Resolve<IMediaSourceManager>();
                if (mediaSourceManager != null)
                {
                    var targets = mediaSourceManager.GetType()
                        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(method =>
                            (string.Equals(method.Name, "GetPlaybackMediaSources", StringComparison.Ordinal) ||
                             string.Equals(method.Name, "GetStaticMediaSources", StringComparison.Ordinal)) &&
                            method.GetParameters().Any(parameter => typeof(BaseItem).IsAssignableFrom(parameter.ParameterType)))
                        .Distinct()
                        .ToArray();

                    var prefix = new HarmonyMethod(typeof(MediaInfoPreReadGuardPatches).GetMethod(
                        nameof(MediaInfoPreReadGuardPatches.MediaSourceReadPrefix), BindingFlags.Public | BindingFlags.Static));
                    foreach (var target in targets)
                    {
                        try
                        {
                            _harmony.Patch(target, prefix: prefix);
                            status.MediaSourceTargetsPatched++;
                        }
                        catch (Exception ex)
                        {
                            Plugin.Instance?.Logger?.Warn("MediaInfo pre-read guard could not patch {0}: {1}", target, ex.Message);
                        }
                    }
                }

                var externalTrackTarget = typeof(SubtitleApi).GetMethod(
                    nameof(SubtitleApi.UpdateExternalSubtitles), BindingFlags.Instance | BindingFlags.Public);
                if (externalTrackTarget != null)
                {
                    _harmony.Patch(externalTrackTarget,
                        prefix: new HarmonyMethod(typeof(MediaInfoPreReadGuardPatches).GetMethod(
                            nameof(MediaInfoPreReadGuardPatches.ExternalTrackPrefix), BindingFlags.Public | BindingFlags.Static)),
                        postfix: new HarmonyMethod(typeof(MediaInfoPreReadGuardPatches).GetMethod(
                            nameof(MediaInfoPreReadGuardPatches.ExternalTrackPostfix), BindingFlags.Public | BindingFlags.Static)));
                    status.ExternalTrackTargetPatched = true;
                }

                if (status.MediaSourceTargetsPatched == 0)
                    status.Error = "No compatible IMediaSourceManager GetPlaybackMediaSources/GetStaticMediaSources target was found.";
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Error("MediaInfo pre-read/external-track guard initialization failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
        }
    }

    public sealed class ExternalTrackGuardState
    {
        public long ItemId { get; set; }
        public int BaselineInternalAvCount { get; set; }
        public List<MediaStream> PreservedBaselineStreams { get; set; } = new List<MediaStream>();
        public bool SkippedOriginal { get; set; }
    }

    public static class MediaInfoPreReadGuardPatches
    {
        private static readonly AsyncLocal<int> RecoveryDepth = new AsyncLocal<int>();
        private static readonly object StatusSync = new object();

        public static void MediaSourceReadPrefix(object[] __args)
        {
            if (RecoveryDepth.Value > 0 || __args == null) return;

            var itemIndex = -1;
            BaseItem item = null;
            for (var index = 0; index < __args.Length; index++)
            {
                if (__args[index] is BaseItem candidate)
                {
                    itemIndex = index;
                    item = candidate;
                    break;
                }
            }

            if (item == null || HasInternalAv(item)) return;
            if (!MediaInfoIntegrityMonitor.SnapshotExists(item)) return;
            if (!MediaInfoIntegrityMonitor.PersistenceEnabledFor(item,
                    Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions)) return;

            Increment(status => status.PreReadRestoreAttempts++);
            SetLastItem(item.Path);

            try
            {
                RecoveryDepth.Value++;
                if (TryRestoreSnapshot(item, "PreRead MediaSource Guard"))
                {
                    Increment(status => status.PreReadRestoreSucceeded++);
                    var fresh = ResolveItem(item.InternalId);
                    if (fresh != null && itemIndex >= 0) __args[itemIndex] = fresh;
                }
                else
                {
                    Increment(status => status.PreReadRestoreFailed++);
                }
            }
            catch (Exception ex)
            {
                Increment(status => status.PreReadRestoreFailed++);
                SetLastError(ex.GetBaseException().Message);
                Plugin.Instance?.Logger?.Warn("MediaInfo pre-read restore failed for {0}: {1}", item.Path, ex.Message);
            }
            finally
            {
                RecoveryDepth.Value = Math.Max(0, RecoveryDepth.Value - 1);
            }
        }

        public static bool ExternalTrackPrefix(object[] __args, ref Task __result, out ExternalTrackGuardState __state)
        {
            __state = new ExternalTrackGuardState();
            if (__args == null) return true;

            var itemIndex = -1;
            BaseItem item = null;
            for (var index = 0; index < __args.Length; index++)
            {
                if (__args[index] is BaseItem candidate)
                {
                    itemIndex = index;
                    item = candidate;
                    break;
                }
            }

            if (item == null) return true;
            var fresh = ResolveItem(item.InternalId) ?? item;
            var baseline = SafeStreams(fresh);

            if (CountInternalAv(baseline) == 0 && MediaInfoIntegrityMonitor.SnapshotExists(fresh))
            {
                try
                {
                    if (TryRestoreSnapshot(fresh, "ExternalTrack Baseline Guard"))
                    {
                        fresh = ResolveItem(item.InternalId) ?? fresh;
                        baseline = SafeStreams(fresh);
                        Increment(status => status.ExternalTrackBaselineRecovered++);
                    }
                }
                catch (Exception ex)
                {
                    SetLastError(ex.GetBaseException().Message);
                }
            }

            var internalCount = CountInternalAv(baseline);
            if (internalCount == 0)
            {
                // External-track discovery must never become the authority for internal video/audio streams.
                // Wait for native extraction or snapshot recovery and let a later scan reconcile the sidecars.
                __state.SkippedOriginal = true;
                __result = Task.CompletedTask;
                Increment(status => status.ExternalTrackWritesBlocked++);
                SetLastItem(fresh.Path);
                Plugin.Instance?.Logger?.Warn(
                    "ExternalTrack guard blocked SaveMediaStreams because no internal A/V baseline is available: {0}",
                    fresh.Path);
                return false;
            }

            __state.ItemId = fresh.InternalId;
            __state.BaselineInternalAvCount = internalCount;
            __state.PreservedBaselineStreams = baseline.Where(stream => !IsManagedExternalTrack(stream)).ToList();
            if (itemIndex >= 0) __args[itemIndex] = fresh;
            return true;
        }

        public static void ExternalTrackPostfix(ref Task __result, ExternalTrackGuardState __state)
        {
            if (__result == null || __state == null || __state.SkippedOriginal || __state.ItemId <= 0) return;
            __result = VerifyExternalTrackWriteAsync(__result, __state);
        }

        private static async Task VerifyExternalTrackWriteAsync(Task original, ExternalTrackGuardState state)
        {
            await original.ConfigureAwait(false);

            var fresh = ResolveItem(state.ItemId);
            if (fresh == null) return;
            var current = SafeStreams(fresh);
            if (CountInternalAv(current) > 0) return;

            // A refresh race occurred between the prefix and SubtitleApi.SaveMediaStreams. Restore the exact
            // non-managed baseline and keep the newly discovered external file tracks, then persist the repaired state.
            try
            {
                var repaired = state.PreservedBaselineStreams
                    .Concat(current.Where(IsManagedExternalTrack))
                    .ToList();
                if (CountInternalAv(repaired) == 0) return;

                var repository = Plugin.Instance?.ApplicationHost?.Resolve<IItemRepository>();
                repository?.SaveMediaStreams(state.ItemId, repaired, CancellationToken.None);
                Increment(status => status.ExternalTrackPostWriteRepairs++);
                SetLastItem(fresh.Path);
                Plugin.Instance?.Logger?.Warn(
                    "ExternalTrack guard repaired an unsafe stream replacement after sidecar reconciliation: {0}",
                    fresh.Path);

                if (MediaInfoIntegrityMonitor.PersistenceEnabledFor(fresh,
                        Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions))
                {
                    var directoryService = Plugin.MediaInfoApi.GetMediaInfoRefreshOptions().DirectoryService;
                    await Plugin.MediaInfoApi.SerializeMediaInfo(state.ItemId, directoryService, true,
                        "ExternalTrack Guard Repair").ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                SetLastError(ex.GetBaseException().Message);
                Plugin.Instance?.Logger?.Error("ExternalTrack post-write repair failed: " + ex.Message);
            }
        }

        private static bool TryRestoreSnapshot(BaseItem item, string source)
        {
            if (item == null || Plugin.MediaInfoApi == null) return false;

            var primary = MediaInfoApi.GetMediaInfoJsonPath(item);
            var backup = MediaInfoPersistenceReliabilityPatches.BackupPath(primary);
            if (!File.Exists(primary) && File.Exists(backup))
            {
                var parent = Path.GetDirectoryName(primary);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                File.Copy(backup, primary, true);
            }
            if (!File.Exists(primary)) return false;

            var directoryService = Plugin.MediaInfoApi.GetMediaInfoRefreshOptions().DirectoryService;
            var ignoreFileChange = item.IsShortcut || !item.IsFileProtocol ||
                                   string.Equals(Path.GetExtension(item.Path), ".strm", StringComparison.OrdinalIgnoreCase);
            return Plugin.MediaInfoApi.DeserializeMediaInfo(item, directoryService, source, ignoreFileChange)
                .GetAwaiter().GetResult();
        }

        private static BaseItem ResolveItem(long itemId)
        {
            try
            {
                return Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>()?.GetItemById(itemId);
            }
            catch
            {
                return null;
            }
        }

        private static List<MediaStream> SafeStreams(BaseItem item)
        {
            try { return item?.GetMediaStreams()?.Where(stream => stream != null).ToList() ?? new List<MediaStream>(); }
            catch { return new List<MediaStream>(); }
        }

        private static bool HasInternalAv(BaseItem item)
        {
            return CountInternalAv(SafeStreams(item)) > 0 && item?.RunTimeTicks.HasValue == true;
        }

        private static int CountInternalAv(IEnumerable<MediaStream> streams)
        {
            return streams?.Count(stream => !stream.IsExternal &&
                                            (stream.Type == MediaStreamType.Video || stream.Type == MediaStreamType.Audio)) ?? 0;
        }

        private static bool IsManagedExternalTrack(MediaStream stream)
        {
            return stream != null && stream.IsExternal && stream.Protocol == MediaProtocol.File &&
                   (stream.Type == MediaStreamType.Subtitle || stream.Type == MediaStreamType.Audio);
        }

        private static void Increment(Action<MediaInfoPreReadGuardStatus> action)
        {
            lock (StatusSync)
            {
                var status = MediaInfoPreReadGuardState.Status;
                if (status != null) action(status);
            }
        }

        private static void SetLastItem(string path)
        {
            lock (StatusSync)
            {
                if (MediaInfoPreReadGuardState.Status != null)
                    MediaInfoPreReadGuardState.Status.LastItemPath = path;
            }
        }

        private static void SetLastError(string error)
        {
            lock (StatusSync)
            {
                if (MediaInfoPreReadGuardState.Status != null)
                    MediaInfoPreReadGuardState.Status.LastError = error;
            }
        }
    }
}
