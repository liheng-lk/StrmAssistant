using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using StrmAssistant.MediaEnhance;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    public class VideoThumbnailApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly DistributedChapterImageGenerator _distributedChapterImageGenerator;

        private readonly object _thumbnailGenerator;
        private readonly MethodInfo _refreshThumbnailImages;

        private static readonly Version AppVer = Plugin.Instance.ApplicationHost.ApplicationVersion;
        private static readonly Version Ver4936 = new Version("4.9.0.36");

        public VideoThumbnailApi(ILibraryManager libraryManager, IFileSystem fileSystem,
            IImageExtractionManager imageExtractionManager, IItemRepository itemRepository,
            IMediaMountManager mediaMountManager, IServerApplicationPaths applicationPaths,
            ILibraryMonitor libraryMonitor, IFfmpegManager ffmpegManager)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _distributedChapterImageGenerator = new DistributedChapterImageGenerator(fileSystem, itemRepository);

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var thumbnailGenerator = embyProviders.GetType("Emby.Providers.MediaInfo.ThumbnailGenerator");
                var thumbnailGeneratorConstructor = thumbnailGenerator.GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[]
                    {
                        typeof(IFileSystem), typeof(ILogger), typeof(IImageExtractionManager),
                        typeof(IItemRepository), typeof(IMediaMountManager), typeof(IServerApplicationPaths),
                        typeof(ILibraryMonitor), typeof(IFfmpegManager)
                    }, null);
                _thumbnailGenerator = thumbnailGeneratorConstructor?.Invoke(new object[]
                {
                    fileSystem, _logger, imageExtractionManager, itemRepository, mediaMountManager,
                    applicationPaths, libraryMonitor, ffmpegManager
                });
                _refreshThumbnailImages = thumbnailGenerator.GetMethod("RefreshThumbnailImages",
                    BindingFlags.Public | BindingFlags.Instance);
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    _logger.Debug(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            if (_thumbnailGenerator is null || _refreshThumbnailImages is null)
            {
                _logger.Warn($"{nameof(VideoThumbnailApi)} Init Failed");
            }
        }

        public async Task<bool> RefreshThumbnailImages(Video item, LibraryOptions libraryOptions,
            IDirectoryService directoryService, List<ChapterInfo> chapters, bool extractImages, bool saveChapters,
            CancellationToken cancellationToken)
        {
            if (MediaExtractionFilter.ShouldSkip(item, out var reason))
            {
                _logger.Info("VideoThumbnailExtract - Skipped by extraction blacklist: {0} ({1})", item.Path, reason);
                return false;
            }

            var options = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions;
            if (extractImages && options.EnableDistributedChapterImageRouting)
            {
                try
                {
                    var distributedResult = await _distributedChapterImageGenerator
                        .GenerateMissingAsync(item, chapters, options, cancellationToken)
                        .ConfigureAwait(false);

                    if (distributedResult.Attempted)
                    {
                        _logger.Info(
                            "VideoThumbnailExtract - Distributed chapter image pre-generation for {0}: generated={1}, existing={2}, failed={3}, fallback={4}, executable={5}",
                            item.Path, distributedResult.GeneratedCount, distributedResult.ExistingCount,
                            distributedResult.FailedCount, distributedResult.FellBackToNative,
                            distributedResult.Executable);

                        if (!string.IsNullOrWhiteSpace(distributedResult.Error))
                            _logger.Warn("VideoThumbnailExtract - Distributed chapter image detail: {0}",
                                distributedResult.Error);

                        if (!distributedResult.Success && !options.DistributedChapterImageFallbackToEmby)
                            return false;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("VideoThumbnailExtract - Distributed chapter pre-generation failed for {0}",
                        ex, item.Path);
                    if (!options.DistributedChapterImageFallbackToEmby) return false;
                }
            }

            var mediaSource = AppVer >= Ver4936
                ? item.GetMediaSources(false, false, libraryOptions).FirstOrDefault()
                : null;

            var parameters = AppVer >= Ver4936
                ? new object[]
                {
                    item, mediaSource, null, libraryOptions, directoryService, chapters, extractImages,
                    saveChapters, cancellationToken
                }
                : new object[]
                {
                    item, null, libraryOptions, directoryService, chapters, extractImages, saveChapters,
                    cancellationToken
                };

            return await ((Task<bool>)_refreshThumbnailImages.Invoke(_thumbnailGenerator, parameters))
                .ConfigureAwait(false);
        }

        public List<Video> FetchExtractTaskItems()
        {
            var libraryIds = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions
                .LibraryScope.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
            var librariesWithVideoThumbnail = _libraryManager.GetVirtualFolders()
                .Where(f => f.LibraryOptions.EnableChapterImageExtraction)
                .ToList();
            var librariesSelected = librariesWithVideoThumbnail.Where(f => libraryIds.Contains(f.Id)).ToList();

            _logger.Info("VideoThumbnailExtract - LibraryScope: " + (!librariesWithVideoThumbnail.Any()
                ? "NONE"
                : string.Join(", ",
                    (libraryIds.Contains("-1")
                        ? new[] { Resources.Favorites }.Concat(librariesSelected.Select(l => l.Name))
                        : librariesSelected.Select(l => l.Name)).DefaultIfEmpty("ALL"))));

            var includeExtra = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.IncludeExtra;
            _logger.Info("Include Extra: " + includeExtra);

            var librariesWithVideoThumbnailPaths = librariesWithVideoThumbnail.SelectMany(l => l.Locations)
                .Select(ls =>
                    ls.EndsWith(Path.DirectorySeparatorChar.ToString()) ? ls : ls + Path.DirectorySeparatorChar)
                .ToArray();
            var librariesSelectedPaths = librariesSelected.SelectMany(l => l.Locations)
                .Select(ls =>
                    ls.EndsWith(Path.DirectorySeparatorChar.ToString()) ? ls : ls + Path.DirectorySeparatorChar)
                .ToArray();

            var favoritesWithExtra = Array.Empty<BaseItem>();

            if (libraryIds.Contains("-1") && librariesWithVideoThumbnailPaths.Any())
            {
                var favorites = LibraryApi.AllUsers.Select(e => e.Key)
                    .SelectMany(user => _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        User = user,
                        IsFavorite = true,
                        PathStartsWithAny = librariesWithVideoThumbnailPaths
                    })).GroupBy(i => i.InternalId).Select(g => g.First()).ToList();

                var expanded = Plugin.LibraryApi.ExpandFavorites(favorites, false, false, false);

                favoritesWithExtra = expanded
                    .Concat(includeExtra
                        ? expanded.SelectMany(f => f.GetExtras(LibraryApi.IncludeExtraTypes))
                        : Enumerable.Empty<BaseItem>())
                    .Where(i => Plugin.LibraryApi.HasMediaInfo(i) && !i.HasImage(ImageType.Chapter))
                    .ToArray();
            }

            var items = Array.Empty<BaseItem>();
            var extras = Array.Empty<BaseItem>();

            if (!libraryIds.Any() && librariesWithVideoThumbnailPaths.Any() ||
                libraryIds.Any(id => id != "-1") && librariesSelectedPaths.Any())
            {
                var videoThumbnailQuery = new InternalItemsQuery
                {
                    MediaTypes = new[] { MediaType.Video },
                    HasAudioStream = true,
                    HasChapterImages = false,
                    PathStartsWithAny = !libraryIds.Any() ? librariesWithVideoThumbnailPaths : librariesSelectedPaths
                };

                items = _libraryManager.GetItemList(videoThumbnailQuery);

                if (includeExtra)
                {
                    videoThumbnailQuery.ExtraTypes = LibraryApi.IncludeExtraTypes;
                    extras = _libraryManager.GetItemList(videoThumbnailQuery);
                }
            }

            var isModSupported = Plugin.Instance.IsModSupported;
            var combined = favoritesWithExtra.Concat(items).Concat(extras).GroupBy(i => i.InternalId)
                .Select(g => g.First()).Where(i => isModSupported || !i.IsShortcut).OfType<Video>().ToList();

            var filtered = MediaExtractionFilter.Apply(combined).ToList();
            if (filtered.Count != combined.Count)
            {
                _logger.Info("VideoThumbnailExtract - Extraction blacklist skipped {0} item(s).",
                    combined.Count - filtered.Count);
            }

            return filtered;
        }
    }
}
