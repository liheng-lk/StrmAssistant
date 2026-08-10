using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Common;
using StrmAssistant.Compatibility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace StrmAssistant.MediaEnhance
{
    public sealed class MediaInfoIntegrityAssessment
    {
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string ItemPath { get; set; }
        public bool IsShortcut { get; set; }
        public bool IsFileProtocol { get; set; }
        public bool PersistenceEnabled { get; set; }
        public bool LibraryInScope { get; set; }
        public bool CoreMediaInfoComplete { get; set; }
        public long? RunTimeTicks { get; set; }
        public long Size { get; set; }
        public string Container { get; set; }
        public long TotalBitrate { get; set; }
        public int MediaStreamCount { get; set; }
        public int VideoStreamCount { get; set; }
        public int AudioStreamCount { get; set; }
        public int InternalVideoStreamCount { get; set; }
        public int InternalAudioStreamCount { get; set; }
        public string PrimarySnapshotPath { get; set; }
        public bool PrimarySnapshotExists { get; set; }
        public bool PrimarySnapshotValid { get; set; }
        public string PrimarySnapshotError { get; set; }
        public string BackupSnapshotPath { get; set; }
        public bool BackupSnapshotExists { get; set; }
        public bool BackupSnapshotValid { get; set; }
        public string BackupSnapshotError { get; set; }
        public string ShadowSnapshotPath { get; set; }
        public bool ShadowSnapshotExists { get; set; }
        public bool ShadowSnapshotValid { get; set; }
        public bool RecoverableFromPersistedSnapshot { get; set; }
        public bool RecoverableFromShadow { get; set; }
        public bool Recoverable { get; set; }
        public bool PlaybackProbeRisk { get; set; }
        public string RecommendedAction { get; set; }
    }

    public static class MediaInfoIntegrityService
    {
        private static readonly object HydrationSync = new object();

        public static MediaInfoIntegrityAssessment Assess(BaseItem item)
        {
            var result = new MediaInfoIntegrityAssessment
            {
                ItemId = item?.InternalId.ToString(),
                ItemName = item?.Name,
                ItemPath = item?.Path,
                IsShortcut = item?.IsShortcut == true,
                IsFileProtocol = item?.IsFileProtocol == true
            };

            if (item == null)
            {
                result.RecommendedAction = "Item was not found.";
                return result;
            }

            var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
            result.PersistenceEnabled = MediaInfoIntegrityMonitor.PersistenceEnabledFor(item, options);
            result.LibraryInScope = Plugin.LibraryApi?.IsLibraryInScope(item) == true;

            var streams = SafeStreams(item);
            result.RunTimeTicks = item.RunTimeTicks;
            result.Size = item.Size;
            result.Container = item.Container;
            result.TotalBitrate = item.TotalBitrate;
            result.MediaStreamCount = streams.Count;
            result.VideoStreamCount = streams.Count(v => v.Type == MediaStreamType.Video);
            result.AudioStreamCount = streams.Count(v => v.Type == MediaStreamType.Audio);
            result.InternalVideoStreamCount = streams.Count(v => v.Type == MediaStreamType.Video && !v.IsExternal);
            result.InternalAudioStreamCount = streams.Count(v => v.Type == MediaStreamType.Audio && !v.IsExternal);
            result.CoreMediaInfoComplete = IsCoreMediaInfoComplete(item, streams);

            try
            {
                result.PrimarySnapshotPath = MediaInfoApi.GetMediaInfoJsonPath(item);
                result.BackupSnapshotPath = MediaInfoPersistenceReliabilityPatches.BackupPath(result.PrimarySnapshotPath);
                result.PrimarySnapshotExists = File.Exists(result.PrimarySnapshotPath);
                result.BackupSnapshotExists = File.Exists(result.BackupSnapshotPath);
                result.PrimarySnapshotValid = TryLoadValidSnapshot(item, result.PrimarySnapshotPath, out _, out var primaryError);
                result.PrimarySnapshotError = primaryError;
                result.BackupSnapshotValid = TryLoadValidSnapshot(item, result.BackupSnapshotPath, out _, out var backupError);
                result.BackupSnapshotError = backupError;
            }
            catch (Exception ex)
            {
                result.PrimarySnapshotError = ex.Message;
            }

            try
            {
                result.ShadowSnapshotPath = MediaInfoReliabilityShadowStore.GetPath(item);
                result.ShadowSnapshotExists = !string.IsNullOrWhiteSpace(result.ShadowSnapshotPath) &&
                                              (File.Exists(result.ShadowSnapshotPath) ||
                                               File.Exists(result.ShadowSnapshotPath + ".bak"));
                result.ShadowSnapshotValid = MediaInfoReliabilityShadowStore.Exists(item);
            }
            catch
            {
                result.ShadowSnapshotValid = false;
            }

            result.RecoverableFromPersistedSnapshot = !result.CoreMediaInfoComplete && result.PersistenceEnabled &&
                                                       result.LibraryInScope &&
                                                       (result.PrimarySnapshotValid || result.BackupSnapshotValid);
            result.RecoverableFromShadow = !result.CoreMediaInfoComplete && result.ShadowSnapshotValid;
            result.Recoverable = result.RecoverableFromPersistedSnapshot || result.RecoverableFromShadow;
            result.PlaybackProbeRisk = !result.CoreMediaInfoComplete && item.IsShortcut && !result.Recoverable;

            if (result.CoreMediaInfoComplete)
                result.RecommendedAction = MediaInfoReliabilityShadowStore.AppliesTo(item)
                    ? "Core MediaInfo is complete. The STRM shadow cache can be refreshed without probing the remote media."
                    : "Core MediaInfo is complete; playback should not require a recovery probe.";
            else if (result.RecoverableFromShadow)
                result.RecommendedAction = "Restore core MediaInfo from the validated STRM shadow cache before playback.";
            else if (result.RecoverableFromPersistedSnapshot)
                result.RecommendedAction = "Restore core MediaInfo from the validated persisted MediaInfo snapshot.";
            else if (!result.PersistenceEnabled && MediaInfoReliabilityShadowStore.AppliesTo(item))
                result.RecommendedAction = "No validated shadow exists yet. Run one successful MediaInfo extraction/refresh; the plugin will then keep a private STRM core shadow even with persistence disabled.";
            else if (!result.PersistenceEnabled)
                result.RecommendedAction = "Enable MediaInfo persistence for this media type.";
            else if (!result.LibraryInScope)
                result.RecommendedAction = "Add the library to the MediaInfo extraction scope.";
            else
                result.RecommendedAction = "No valid recovery snapshot exists; run one explicit MediaInfo extraction to rebuild core streams.";

            return result;
        }

        public static bool IsCoreMediaInfoComplete(BaseItem item)
        {
            return item != null && IsCoreMediaInfoComplete(item, SafeStreams(item));
        }

        private static bool IsCoreMediaInfoComplete(BaseItem item, IReadOnlyCollection<MediaStream> streams)
        {
            if (item?.RunTimeTicks.HasValue != true || item.RunTimeTicks.Value <= 0 || streams == null)
                return false;

            if (item is Audio)
                return streams.Any(v => v.Type == MediaStreamType.Audio && !v.IsExternal);
            if (item is Video)
                return streams.Any(v => v.Type == MediaStreamType.Video && !v.IsExternal);
            return streams.Any(v => !v.IsExternal &&
                                    (v.Type == MediaStreamType.Video || v.Type == MediaStreamType.Audio));
        }

        public static bool SnapshotExists(BaseItem item)
        {
            if (item == null) return false;
            try
            {
                var primary = MediaInfoApi.GetMediaInfoJsonPath(item);
                return File.Exists(primary) || File.Exists(MediaInfoPersistenceReliabilityPatches.BackupPath(primary));
            }
            catch { return false; }
        }

        public static bool IsSnapshotValid(BaseItem item, string path)
        {
            return TryLoadValidSnapshot(item, path, out _, out _);
        }

        public static bool RepairPrimaryFromBackupIfNeeded(BaseItem item)
        {
            if (item == null) return false;
            try
            {
                var primary = MediaInfoApi.GetMediaInfoJsonPath(item);
                if (TryLoadValidSnapshot(item, primary, out _, out _)) return true;
                var backup = MediaInfoPersistenceReliabilityPatches.BackupPath(primary);
                if (!TryLoadValidSnapshot(item, backup, out _, out _)) return false;
                var parent = Path.GetDirectoryName(primary);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                File.Copy(backup, primary, true);
                return TryLoadValidSnapshot(item, primary, out _, out _);
            }
            catch { return false; }
        }

        public static bool RefreshValidatedBackup(BaseItem item)
        {
            if (item == null) return false;
            try
            {
                var primary = MediaInfoApi.GetMediaInfoJsonPath(item);
                if (!TryLoadValidSnapshot(item, primary, out _, out _)) return false;
                var backup = MediaInfoPersistenceReliabilityPatches.BackupPath(primary);
                var parent = Path.GetDirectoryName(backup);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                File.Copy(primary, backup, true);
                return true;
            }
            catch { return false; }
        }

        public static bool HydrateCore(BaseItem item, string source)
        {
            if (item == null || Plugin.Instance == null) return false;
            lock (HydrationSync)
            {
                try
                {
                    var libraryManager = Plugin.Instance.ApplicationHost.Resolve<ILibraryManager>();
                    var itemRepository = Plugin.Instance.ApplicationHost.Resolve<IItemRepository>();
                    if (libraryManager == null || itemRepository == null) return false;

                    var workItem = libraryManager.GetItemById(item.InternalId);
                    if (workItem == null) return false;
                    if (IsCoreMediaInfoComplete(workItem)) return true;

                    var primary = MediaInfoApi.GetMediaInfoJsonPath(workItem);
                    MediaInfoApi.MediaSourceWithChapters snapshot;
                    if (!TryLoadValidSnapshot(workItem, primary, out snapshot, out _))
                    {
                        var backup = MediaInfoPersistenceReliabilityPatches.BackupPath(primary);
                        if (!TryLoadValidSnapshot(workItem, backup, out snapshot, out _)) return false;
                        var parent = Path.GetDirectoryName(primary);
                        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                        File.Copy(backup, primary, true);
                    }

                    var mediaSource = snapshot?.MediaSourceInfo;
                    if (mediaSource?.RunTimeTicks.HasValue != true) return false;
                    var streams = mediaSource.MediaStreams?.ToList() ?? new List<MediaStream>();
                    if (!SnapshotStreamsMatchItem(workItem, streams)) return false;

                    foreach (var subtitle in streams.Where(v => v.IsExternal &&
                                                                 v.Type == MediaStreamType.Subtitle &&
                                                                 v.Protocol == MediaProtocol.File &&
                                                                 !string.IsNullOrWhiteSpace(v.Path)))
                    {
                        if (!Path.IsPathRooted(subtitle.Path) && !string.IsNullOrWhiteSpace(workItem.ContainingFolderPath))
                            subtitle.Path = Path.Combine(workItem.ContainingFolderPath, Path.GetFileName(subtitle.Path));
                    }

                    itemRepository.SaveMediaStreams(workItem.InternalId, streams, CancellationToken.None);
                    workItem.Size = mediaSource.Size.GetValueOrDefault();
                    workItem.RunTimeTicks = mediaSource.RunTimeTicks;
                    workItem.Container = mediaSource.Container;
                    workItem.TotalBitrate = mediaSource.Bitrate.GetValueOrDefault();

                    var video = streams.Where(v => v.Type == MediaStreamType.Video && !v.IsExternal &&
                                                   v.Width.HasValue && v.Height.HasValue)
                        .OrderByDescending(v => (long)v.Width.Value * v.Height.Value)
                        .FirstOrDefault();
                    if (video != null)
                    {
                        workItem.Width = video.Width.GetValueOrDefault();
                        workItem.Height = video.Height.GetValueOrDefault();
                    }

                    libraryManager.UpdateItems(new List<BaseItem> { workItem }, null,
                        ItemUpdateType.MetadataImport, false, false, null, CancellationToken.None);

                    var hydrated = IsCoreMediaInfoComplete(libraryManager.GetItemById(workItem.InternalId));
                    if (hydrated)
                    {
                        Plugin.Instance.Logger.Info("MediaInfo core hydration success ({0}): {1}", source, workItem.Path);
                        RefreshValidatedBackup(workItem);
                    }
                    return hydrated;
                }
                catch (Exception ex)
                {
                    Plugin.Instance?.Logger?.Warn("MediaInfo core hydration failed ({0}): {1}", source, ex.Message);
                    return false;
                }
            }
        }

        private static bool TryLoadValidSnapshot(BaseItem item, string path,
            out MediaInfoApi.MediaSourceWithChapters snapshot, out string error)
        {
            snapshot = null;
            error = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "Snapshot does not exist.";
                return false;
            }

            try
            {
                var info = new FileInfo(path);
                if (info.Length <= 2)
                {
                    error = "Snapshot is empty.";
                    return false;
                }

                var serializer = Plugin.Instance?.ApplicationHost?.Resolve<IJsonSerializer>();
                if (serializer == null)
                {
                    error = "IJsonSerializer is unavailable.";
                    return false;
                }

                var list = serializer.DeserializeFromFileAsync<List<MediaInfoApi.MediaSourceWithChapters>>(path)
                    .GetAwaiter().GetResult();
                snapshot = list?.FirstOrDefault(v => v?.MediaSourceInfo != null);
                if (snapshot?.MediaSourceInfo?.RunTimeTicks.HasValue != true ||
                    snapshot.MediaSourceInfo.RunTimeTicks.Value <= 0)
                {
                    error = "Snapshot has no valid runtime.";
                    snapshot = null;
                    return false;
                }

                var streams = snapshot.MediaSourceInfo.MediaStreams?.ToList() ?? new List<MediaStream>();
                if (!SnapshotStreamsMatchItem(item, streams))
                {
                    error = "Snapshot does not contain the expected internal core media stream.";
                    snapshot = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                snapshot = null;
                return false;
            }
        }

        private static bool SnapshotStreamsMatchItem(BaseItem item, IReadOnlyCollection<MediaStream> streams)
        {
            if (streams == null) return false;
            if (item is Audio) return streams.Any(v => v.Type == MediaStreamType.Audio && !v.IsExternal);
            if (item is Video) return streams.Any(v => v.Type == MediaStreamType.Video && !v.IsExternal);
            return streams.Any(v => !v.IsExternal &&
                                    (v.Type == MediaStreamType.Video || v.Type == MediaStreamType.Audio));
        }

        private static List<MediaStream> SafeStreams(BaseItem item)
        {
            try { return item?.GetMediaStreams()?.ToList() ?? new List<MediaStream>(); }
            catch { return new List<MediaStream>(); }
        }
    }
}
