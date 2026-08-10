using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using StrmAssistant.Compatibility;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.MediaEnhance
{
    public sealed class StrmMediaInfoRepairResult
    {
        public bool Success { get; set; }
        public bool LocalRecoveryAttempted { get; set; }
        public bool LocalRecoverySucceeded { get; set; }
        public bool RemoteRebuildAttempted { get; set; }
        public bool RemoteRebuildSucceeded { get; set; }
        public bool PersistedSnapshotWritten { get; set; }
        public bool ShadowCaptureQueued { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string ItemPath { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Repairs one STRM item in two explicit stages:
    /// 1) validated local persistence/shadow recovery (no media probe);
    /// 2) only when explicitly allowed, a single Emby provider refresh with metadata/image fetchers disabled.
    /// This keeps first-time network probing out of normal playback while reusing Emby's supported
    /// media-info refresh pipeline rather than running a second independent ffprobe implementation.
    /// </summary>
    public sealed class StrmMediaInfoRepairService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;

        public StrmMediaInfoRepairService(ILibraryManager libraryManager, IProviderManager providerManager)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
        }

        public async Task<StrmMediaInfoRepairResult> RepairAsync(BaseItem item, bool allowRemoteRebuild,
            string source, CancellationToken cancellationToken)
        {
            var result = new StrmMediaInfoRepairResult
            {
                ItemId = item?.InternalId.ToString(),
                ItemName = item?.Name,
                ItemPath = item?.Path
            };

            if (item == null)
            {
                result.Error = "Item was not found.";
                return result;
            }
            if (!MediaInfoReliabilityShadowStore.AppliesTo(item))
            {
                result.Error = "This repair path is restricted to STRM/shortcut items.";
                return result;
            }
            if (Plugin.LibraryApi?.IsLibraryInScope(item) != true)
            {
                result.Error = "Item is outside the configured MediaInfo library scope.";
                return result;
            }
            if (MediaExtractionFilter.ShouldSkip(item, out var blacklistReason))
            {
                result.Error = "Item is blocked by the MediaInfo extraction blacklist: " + blacklistReason;
                return result;
            }

            var fresh = _libraryManager.GetItemById(item.InternalId) ?? item;
            if (MediaInfoIntegrityService.IsCoreMediaInfoComplete(fresh))
            {
                MediaInfoReliabilityShadowPatches.QueueCapture(fresh.InternalId);
                result.ShadowCaptureQueued = true;
                result.Success = true;
                return result;
            }

            if (MediaInfoIntegrityMonitor.ShouldRecover(fresh))
            {
                result.LocalRecoveryAttempted = true;
                try
                {
                    result.LocalRecoverySucceeded = await MediaInfoIntegrityMonitor
                        .RecoverAsync(fresh, source + " LocalRecovery", cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.Error = "Local MediaInfo recovery failed: " + ex.GetBaseException().Message;
                }

                fresh = _libraryManager.GetItemById(item.InternalId) ?? fresh;
                if (result.LocalRecoverySucceeded && MediaInfoIntegrityService.IsCoreMediaInfoComplete(fresh))
                {
                    result.Success = true;
                    return result;
                }
            }

            if (!allowRemoteRebuild)
            {
                if (string.IsNullOrWhiteSpace(result.Error))
                    result.Error = "No validated local recovery source restored core MediaInfo; remote rebuild was not authorized.";
                return result;
            }

            result.RemoteRebuildAttempted = true;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mountedPath = await Plugin.LibraryApi.GetStrmMountPath(fresh.Path).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(mountedPath))
                {
                    result.Error = "STRM target could not be mounted/resolved for an explicit MediaInfo rebuild.";
                    return result;
                }

                var refreshOptions = Plugin.MediaInfoApi.GetMediaInfoRefreshOptions();
                var collectionFolders = (BaseItem[])_libraryManager.GetCollectionFolders(fresh);
                var libraryOptions = _libraryManager.GetLibraryOptions(fresh);
                var isolatedOptions = Common.LibraryApi.CopyLibraryOptions(libraryOptions);
                isolatedOptions.DisabledLocalMetadataReaders = new[] { "Nfo" };
                isolatedOptions.MetadataSavers = Array.Empty<string>();
                foreach (var typeOptions in isolatedOptions.TypeOptions ?? Array.Empty<MediaBrowser.Model.Configuration.TypeOptions>())
                {
                    typeOptions.MetadataFetchers = Array.Empty<string>();
                    typeOptions.ImageFetchers = Array.Empty<string>();
                }

                fresh.DateLastRefreshed = new DateTimeOffset();
                await _providerManager.RefreshSingleItem(fresh, refreshOptions, collectionFolders, isolatedOptions,
                        cancellationToken)
                    .ConfigureAwait(false);

                fresh = _libraryManager.GetItemById(fresh.InternalId) ?? fresh;
                result.RemoteRebuildSucceeded = MediaInfoIntegrityService.IsCoreMediaInfoComplete(fresh);
                if (!result.RemoteRebuildSucceeded)
                {
                    result.Error = "Emby completed the explicit refresh, but core internal A/V MediaInfo is still incomplete.";
                    return result;
                }

                var mediaOptions = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
                if (MediaInfoIntegrityMonitor.PersistenceEnabledFor(fresh, mediaOptions))
                {
                    result.PersistedSnapshotWritten = await Plugin.MediaInfoApi
                        .SerializeMediaInfo(fresh.InternalId, refreshOptions.DirectoryService, true,
                            source + " RebuildPersist")
                        .ConfigureAwait(false);
                }

                MediaInfoReliabilityShadowPatches.QueueCapture(fresh.InternalId);
                result.ShadowCaptureQueued = true;
                result.Success = true;
                result.Error = null;
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Error = "Explicit STRM MediaInfo rebuild failed: " + ex.GetBaseException().Message;
                return result;
            }
        }
    }
}
