using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Querying;
using StrmAssistant.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.MediaEnhance
{
    /// <summary>
    /// Repairs persisted MediaInfo after provider refreshes that leave runtime/streams incomplete.
    /// Recovery reads only a validated local JSON/.bak snapshot and never invokes ffprobe.
    /// </summary>
    public sealed class MediaInfoIntegrityMonitor : IServerEntryPoint
    {
        private readonly IProviderManager _providerManager;
        private readonly ConcurrentDictionary<long, byte> _inFlight = new ConcurrentDictionary<long, byte>();
        private bool _started;

        public MediaInfoIntegrityMonitor(IProviderManager providerManager)
        {
            _providerManager = providerManager;
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

        internal static bool ShouldRecover(BaseItem item)
        {
            if (item == null || Plugin.Instance == null || Plugin.LibraryApi == null || Plugin.MediaInfoApi == null)
                return false;

            var options = Plugin.Instance.GetPluginOptions()?.MediaInfoExtractOptions;
            if (!PersistenceEnabledFor(item, options)) return false;
            if (!Plugin.LibraryApi.IsLibraryInScope(item)) return false;
            return !MediaInfoIntegrityService.IsCoreMediaInfoComplete(item) &&
                   MediaInfoIntegrityService.SnapshotExists(item);
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
            return MediaInfoIntegrityService.SnapshotExists(item);
        }

        internal static Task<bool> RecoverAsync(BaseItem item, string source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item == null || MediaInfoIntegrityService.IsCoreMediaInfoComplete(item))
                return Task.FromResult(true);
            return Task.FromResult(MediaInfoIntegrityService.HydrateCore(item, source));
        }
    }

    /// <summary>
    /// Background startup warm-up. It repairs only incomplete items that already have a persisted
    /// snapshot; it never performs media probing and therefore does not block Emby startup.
    /// </summary>
    public sealed class MediaInfoIntegrityStartupEntryPoint : IServerEntryPoint
    {
        private readonly ILibraryManager _libraryManager;
        private CancellationTokenSource _cts;
        private Task _worker;

        public MediaInfoIntegrityStartupEntryPoint(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public void Run()
        {
            if (_worker != null) return;
            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => WarmAsync(_cts.Token));
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
        }

        private async Task WarmAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
                var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
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
                    .Where(item => !MediaInfoIntegrityService.IsCoreMediaInfoComplete(item))
                    .Where(MediaInfoIntegrityService.SnapshotExists)
                    .GroupBy(item => item.InternalId)
                    .Select(group => group.First())
                    .ToList();

                var repaired = 0;
                foreach (var item in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (MediaInfoIntegrityService.HydrateCore(item, "Startup IntegrityWarmup")) repaired++;
                    // Yield so a large library can never monopolize the server thread pool.
                    await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                }

                if (candidates.Count > 0)
                    Plugin.Instance.Logger.Info("MediaInfo startup integrity warm-up: {0}/{1} snapshots restored.",
                        repaired, candidates.Count);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("MediaInfo startup integrity warm-up failed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Full recovery pass after a library scan. Only items with an existing persisted snapshot are
    /// touched. No ffprobe or remote network request is ever launched by this task.
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
                .Where(item => !MediaInfoIntegrityService.IsCoreMediaInfoComplete(item))
                .Where(MediaInfoIntegrityService.SnapshotExists)
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
