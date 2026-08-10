using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using StrmAssistant.Common;
using StrmAssistant.Compatibility;
using StrmAssistant.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.MediaEnhance
{
    /// <summary>
    /// Repairs persisted MediaInfo after provider refreshes that leave streams/runtime missing.
    /// This is deliberately recovery-only: it never runs ffprobe and therefore cannot make first
    /// playback slower. Missing snapshots are left for the normal extraction/catch-up workflow.
    /// </summary>
    public sealed class MediaInfoIntegrityMonitor : IServerEntryPoint
    {
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly ConcurrentDictionary<long, byte> _inFlight = new ConcurrentDictionary<long, byte>();
        private bool _started;

        public MediaInfoIntegrityMonitor(IProviderManager providerManager, IFileSystem fileSystem)
        {
            _providerManager = providerManager;
            _fileSystem = fileSystem;
        }

        public void Run()
        {
            if (_started) return;
            _started = true;
            _providerManager.RefreshCompleted += OnRefreshCompleted;
        }

        public void Dispose()
        {
            if (!_started) return;
            _started = false;
            _providerManager.RefreshCompleted -= OnRefreshCompleted;
        }

        private async void OnRefreshCompleted(object sender, GenericEventArgs<RefreshProgressInfo> e)
        {
            var item = e?.Argument?.Item;
            if (!ShouldRecover(item)) return;
            if (!_inFlight.TryAdd(item.InternalId, 0)) return;

            try
            {
                await RecoverAsync(item, "RefreshCompleted IntegrityRepair", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("MediaInfo integrity repair after refresh failed: " + ex.Message);
            }
            finally
            {
                _inFlight.TryRemove(item.InternalId, out _);
            }
        }

        private static bool ShouldRecover(BaseItem item)
        {
            if (item == null || Plugin.Instance == null || Plugin.LibraryApi == null || Plugin.MediaInfoApi == null)
                return false;

            var options = Plugin.Instance.GetPluginOptions()?.MediaInfoExtractOptions;
            if (!PersistenceEnabledFor(item, options)) return false;
            if (!Plugin.LibraryApi.IsLibraryInScope(item)) return false;
            return !Plugin.LibraryApi.HasMediaInfo(item) && SnapshotExists(item);
        }

        internal static bool PersistenceEnabledFor(BaseItem item, MediaInfoExtractOptions options)
        {
            if (options == null ||
                options.PersistMediaInfoMode == MediaInfoExtractOptions.PersistMediaInfoOption.None.ToString())
                return false;

            if (item is Audio) return options.PersistMusicMediaInfo;
            return item is Video;
        }

        internal static bool SnapshotExists(BaseItem item)
        {
            try
            {
                var primary = MediaInfoApi.GetMediaInfoJsonPath(item);
                return File.Exists(primary) || File.Exists(MediaInfoPersistenceReliabilityPatches.BackupPath(primary));
            }
            catch
            {
                return false;
            }
        }

        internal static async Task<bool> RecoverAsync(BaseItem item, string source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item == null || Plugin.LibraryApi.HasMediaInfo(item)) return true;

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
            var ignoreFileChange = item.IsShortcut || !item.IsFileProtocol;
            return await Plugin.MediaInfoApi.DeserializeMediaInfo(item, directoryService, source, ignoreFileChange)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Full recovery pass after a library scan. Emby discovers ILibraryPostScanTask implementations
    /// automatically. Only items with an existing primary/backup persistence snapshot are touched.
    /// </summary>
    public sealed class MediaInfoIntegrityPostScanTask : ILibraryPostScanTask
    {
        private readonly ILibraryManager _libraryManager;

        public MediaInfoIntegrityPostScanTask(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (Plugin.Instance == null || Plugin.LibraryApi == null || Plugin.MediaInfoApi == null)
                return;

            var options = Plugin.Instance.GetPluginOptions()?.MediaInfoExtractOptions;
            if (options == null ||
                options.PersistMediaInfoMode == MediaInfoExtractOptions.PersistMediaInfoOption.None.ToString())
                return;

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                HasPath = true,
                MediaTypes = options.PersistMusicMediaInfo
                    ? new[] { MediaType.Video, MediaType.Audio }
                    : new[] { MediaType.Video }
            }) ?? Array.Empty<BaseItem>();

            var candidates = items
                .Where(item => MediaInfoIntegrityMonitor.PersistenceEnabledFor(item, options))
                .Where(item => Plugin.LibraryApi.IsLibraryInScope(item))
                .Where(item => !Plugin.LibraryApi.HasMediaInfo(item))
                .Where(MediaInfoIntegrityMonitor.SnapshotExists)
                .GroupBy(item => item.InternalId)
                .Select(group => group.First())
                .ToList();

            if (candidates.Count == 0)
            {
                progress?.Report(100);
                return;
            }

            var repaired = 0;
            for (var index = 0; index < candidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = candidates[index];
                try
                {
                    if (await MediaInfoIntegrityMonitor
                            .RecoverAsync(item, "PostScan IntegrityRepair", cancellationToken)
                            .ConfigureAwait(false))
                        repaired++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Plugin.Instance.Logger.Warn("MediaInfo post-scan repair failed for {0}: {1}", item.Path, ex.Message);
                }

                progress?.Report((index + 1) * 100d / candidates.Count);
            }

            Plugin.Instance.Logger.Info("MediaInfo post-scan integrity repair: {0}/{1} snapshots restored.",
                repaired, candidates.Count);
        }
    }
}
