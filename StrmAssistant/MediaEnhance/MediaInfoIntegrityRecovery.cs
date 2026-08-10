using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Querying;
using StrmAssistant.Compatibility;
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
    /// Repairs MediaInfo after provider refreshes that leave runtime/streams incomplete. Two local
    /// sources are supported: the user's optional persisted MediaInfo JSON/.bak and the plugin-owned
    /// STRM reliability shadow store. Neither recovery path invokes ffprobe or the remote media URL.
    /// Shadow writes are queued so refresh/playback callers do not serialize files inline.
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
            if (item == null) return;

            if (MediaInfoReliabilityShadowStore.AppliesTo(item) &&
                MediaInfoIntegrityService.IsCoreMediaInfoComplete(item))
            {
                MediaInfoReliabilityShadowPatches.QueueCapture(item.InternalId);
                return;
            }

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
            if (MediaInfoIntegrityService.IsCoreMediaInfoComplete(item)) return false;

            if (MediaInfoReliabilityShadowStore.AppliesTo(item) &&
                MediaInfoReliabilityShadowStore.Exists(item))
                return true;

            var options = Plugin.Instance.GetPluginOptions()?.MediaInfoExtractOptions;
            return PersistenceEnabledFor(item, options) &&
                   Plugin.LibraryApi.IsLibraryInScope(item) &&
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
            return MediaInfoIntegrityService.SnapshotExists(item) || MediaInfoReliabilityShadowStore.Exists(item);
        }

        internal static Task<bool> RecoverAsync(BaseItem item, string source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item == null || MediaInfoIntegrityService.IsCoreMediaInfoComplete(item))
                return Task.FromResult(true);

            var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
            var canUsePersisted = PersistenceEnabledFor(item, options) &&
                                  Plugin.LibraryApi?.IsLibraryInScope(item) == true &&
                                  MediaInfoIntegrityService.SnapshotExists(item);
            if (canUsePersisted && MediaInfoIntegrityService.HydrateCore(item, source + " Persisted"))
            {
                var fresh = Plugin.Instance.ApplicationHost.Resolve<ILibraryManager>()?.GetItemById(item.InternalId) ?? item;
                if (MediaInfoReliabilityShadowStore.AppliesTo(fresh))
                    MediaInfoReliabilityShadowPatches.QueueCapture(fresh.InternalId);
                return Task.FromResult(true);
            }

            return Task.FromResult(MediaInfoReliabilityShadowStore.Restore(item, source + " Shadow"));
        }
    }

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
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                HasPath = true,
                MediaTypes = options?.PersistMusicMediaInfo == true
                    ? new[] { MediaType.Video, MediaType.Audio }
                    : new[] { MediaType.Video }
            }) ?? Array.Empty<BaseItem>();

            var candidates = items
                .Where(MediaInfoIntegrityMonitor.ShouldRecover)
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
