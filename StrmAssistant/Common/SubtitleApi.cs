using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    public class SubtitleApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IFileSystem _fileSystem;

        private readonly object _subtitleResolver;
        private readonly MethodInfo _getExternalSubtitleStreams;
        private readonly object _audioTrackResolver;
        private readonly MethodInfo _getExternalTracks;
        private readonly object _ffProbeSubtitleInfo;
        private readonly MethodInfo _updateExternalSubtitleStream;

        private static readonly Version ExternalAudioMinVersion = new Version("4.9.1.80");

        private static readonly HashSet<string> ProbeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".sub", ".smi", ".sami", ".mpl" };

        public SubtitleApi(ILibraryManager libraryManager, IFileSystem fileSystem, IMediaProbeManager mediaProbeManager,
            ILocalizationManager localizationManager, IItemRepository itemRepository)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _fileSystem = fileSystem;

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var subtitleResolverType = embyProviders.GetType("Emby.Providers.MediaInfo.SubtitleResolver");
                var subtitleResolverConstructor = subtitleResolverType?.GetConstructor(new[]
                {
                    typeof(ILocalizationManager), typeof(IFileSystem), typeof(ILibraryManager)
                });
                _subtitleResolver = subtitleResolverConstructor?.Invoke(new object[]
                {
                    localizationManager, fileSystem, libraryManager
                });
                _getExternalSubtitleStreams = subtitleResolverType?.GetMethod("GetExternalSubtitleStreams");

                if (Plugin.Instance.ApplicationHost.ApplicationVersion >= ExternalAudioMinVersion)
                {
                    var audioTrackResolverType = embyProviders.GetType("Emby.Providers.MediaInfo.AudioTrackResolver");
                    var audioTrackResolverConstructor = audioTrackResolverType?.GetConstructor(new[]
                    {
                        typeof(ILocalizationManager), typeof(IFileSystem), typeof(ILibraryManager)
                    });
                    _audioTrackResolver = audioTrackResolverConstructor?.Invoke(new object[]
                    {
                        localizationManager, fileSystem, libraryManager
                    });

                    var baseTrackResolverType = embyProviders.GetType("Emby.Providers.MediaInfo.BaseTrackResolver");
                    _getExternalTracks = baseTrackResolverType?.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                        .FirstOrDefault(method => method.Name == "GetExternalTracks" &&
                                                  method.GetParameters().Length == 6);
                }

                var ffProbeSubtitleInfoType = embyProviders.GetType("Emby.Providers.MediaInfo.FFProbeSubtitleInfo");
                var ffProbeSubtitleInfoConstructor = ffProbeSubtitleInfoType?.GetConstructor(new[]
                {
                    typeof(IMediaProbeManager)
                });
                _ffProbeSubtitleInfo = ffProbeSubtitleInfoConstructor?.Invoke(new object[] { mediaProbeManager });
                _updateExternalSubtitleStream = ffProbeSubtitleInfoType?.GetMethod("UpdateExternalSubtitleStream");
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    _logger.Debug(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            if (_subtitleResolver is null || _getExternalSubtitleStreams is null ||
                _ffProbeSubtitleInfo is null || _updateExternalSubtitleStream is null)
            {
                _logger.Warn($"{nameof(SubtitleApi)} Init Failed");
            }

            if (Plugin.Instance.ApplicationHost.ApplicationVersion >= ExternalAudioMinVersion &&
                (_audioTrackResolver is null || _getExternalTracks is null))
            {
                _logger.Warn("ExternalAudioTrack - Resolver unavailable on this Emby build.");
            }
        }

        public bool ExternalAudioSupported =>
            Plugin.Instance.ApplicationHost.ApplicationVersion >= ExternalAudioMinVersion &&
            _audioTrackResolver != null && _getExternalTracks != null;

        private bool ExternalAudioEnabled => ExternalAudioSupported &&
            Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.EnableExternalAudioTrackScan;

        private List<MediaStream> GetExternalSubtitleStreams(BaseItem item, int startIndex,
            IDirectoryService directoryService, bool clearCache)
        {
            var namingOptions = _libraryManager.GetNamingOptions();

            var result = _getExternalSubtitleStreams?.Invoke(_subtitleResolver,
                new object[] { item, startIndex, directoryService, namingOptions, clearCache });

            if (result is List<MediaStream> list) return list;
            if (result is IEnumerable<MediaStream> enumerable) return enumerable.ToList();
            return new List<MediaStream>();
        }

        private List<MediaStream> GetExternalAudioStreams(BaseItem item, int startIndex,
            IDirectoryService directoryService, bool clearCache)
        {
            if (!ExternalAudioEnabled || item == null || string.IsNullOrWhiteSpace(item.Path))
                return new List<MediaStream>();

            try
            {
                var libraryOptions = _libraryManager.GetLibraryOptions(item);
                var namingOptions = _libraryManager.GetNamingOptions();
                var result = _getExternalTracks.Invoke(_audioTrackResolver,
                    new object[] { item, startIndex, directoryService, libraryOptions, namingOptions, clearCache });

                var streams = result as IEnumerable<MediaStream>;
                if (streams == null) return new List<MediaStream>();

                return streams
                    .Where(stream => stream != null && stream.Type == MediaStreamType.Audio &&
                                     !string.IsNullOrWhiteSpace(stream.Path))
                    .Select(stream =>
                    {
                        stream.IsExternal = true;
                        stream.Protocol = MediaProtocol.File;
                        return stream;
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.Warn("ExternalAudioTrack - Resolve failed: {0}", item.Path);
                _logger.Warn(ex.Message);
                if (Plugin.Instance.DebugMode) _logger.Debug(ex.StackTrace);
                return new List<MediaStream>();
            }
        }

        private Task<bool> UpdateExternalStream(BaseItem item,
            MediaStream stream, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            return (Task<bool>)_updateExternalSubtitleStream.Invoke(_ffProbeSubtitleInfo,
                new object[] { item, stream, options, libraryOptions, cancellationToken });
        }

        public MetadataRefreshOptions GetExternalSubtitleRefreshOptions()
        {
            return new MetadataRefreshOptions(new DirectoryService(_logger, _fileSystem))
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllMetadata = false,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages = false,
                EnableThumbnailImageExtraction = false,
                EnableSubtitleDownloading = false
            };
        }

        public bool HasExternalSubtitleChanged(BaseItem item, IDirectoryService directoryService, bool clearCache)
        {
            if (item == null) return false;

            try
            {
                var currentSubtitleSet = new HashSet<string>(
                    item.GetMediaStreams()
                        .Where(stream => stream.IsExternal && stream.Type == MediaStreamType.Subtitle &&
                                         !string.IsNullOrWhiteSpace(stream.Path))
                        .Select(stream => NormalizePath(stream.Path)),
                    StringComparer.OrdinalIgnoreCase);

                var newSubtitleSet = new HashSet<string>(
                    GetExternalSubtitleStreams(item, 0, directoryService, clearCache)
                        .Where(stream => !string.IsNullOrWhiteSpace(stream.Path))
                        .Select(stream => NormalizePath(stream.Path)),
                    StringComparer.OrdinalIgnoreCase);

                if (!currentSubtitleSet.SetEquals(newSubtitleSet)) return true;

                if (!ExternalAudioEnabled) return false;

                var currentAudioSet = new HashSet<string>(
                    item.GetMediaStreams()
                        .Where(stream => stream.IsExternal && stream.Type == MediaStreamType.Audio &&
                                         !string.IsNullOrWhiteSpace(stream.Path))
                        .Select(stream => NormalizePath(stream.Path)),
                    StringComparer.OrdinalIgnoreCase);

                var newAudioSet = new HashSet<string>(
                    GetExternalAudioStreams(item, 0, directoryService, clearCache)
                        .Where(stream => !string.IsNullOrWhiteSpace(stream.Path))
                        .Select(stream => NormalizePath(stream.Path)),
                    StringComparer.OrdinalIgnoreCase);

                return !currentAudioSet.SetEquals(newAudioSet);
            }
            catch (Exception ex)
            {
                _logger.Warn("ExternalTrack - Change detection failed: {0}", item.Path);
                _logger.Warn(ex.Message);
                if (Plugin.Instance.DebugMode) _logger.Debug(ex.StackTrace);
                return false;
            }
        }

        public async Task UpdateExternalSubtitles(BaseItem item, MetadataRefreshOptions refreshOptions, bool clearCache,
            bool persistMediaInfo)
        {
            var directoryService = refreshOptions.DirectoryService;
            var currentStreams = item.GetMediaStreams()
                .FindAll(stream =>
                    !(stream.IsExternal && stream.Protocol == MediaProtocol.File &&
                      (stream.Type == MediaStreamType.Subtitle ||
                       ExternalAudioEnabled && stream.Type == MediaStreamType.Audio)));
            var startIndex = currentStreams.Count == 0 ? 0 : currentStreams.Max(stream => stream.Index) + 1;

            var externalSubtitleStreams = GetExternalSubtitleStreams(item, startIndex, directoryService, clearCache);
            startIndex += externalSubtitleStreams.Count;
            var externalAudioStreams = GetExternalAudioStreams(item, startIndex, directoryService, clearCache);

            foreach (var subtitleStream in externalSubtitleStreams)
            {
                var extension = Path.GetExtension(subtitleStream.Path);
                if (!string.IsNullOrEmpty(extension) && ProbeExtensions.Contains(extension))
                {
                    var result = await UpdateExternalStream(item, subtitleStream, refreshOptions,
                        CancellationToken.None).ConfigureAwait(false);

                    if (!result)
                        _logger.Warn("No result when probing external subtitle file: {0}", subtitleStream.Path);
                }

                _logger.Info("ExternalSubtitle - Subtitle Processed: " + subtitleStream.Path);
            }

            foreach (var audioStream in externalAudioStreams)
            {
                var result = await UpdateExternalStream(item, audioStream, refreshOptions,
                    CancellationToken.None).ConfigureAwait(false);

                if (!result)
                    _logger.Warn("ExternalAudioTrack - No result when probing: {0}", audioStream.Path);

                _logger.Info("ExternalAudioTrack - Audio Processed: " + audioStream.Path);
            }

            currentStreams.AddRange(externalSubtitleStreams);
            currentStreams.AddRange(externalAudioStreams);
            _itemRepository.SaveMediaStreams(item.InternalId, currentStreams, CancellationToken.None);

            if (persistMediaInfo && Plugin.LibraryApi.IsLibraryInScope(item))
            {
                _ = Plugin.MediaInfoApi.SerializeMediaInfo(item.InternalId, directoryService, true,
                    externalAudioStreams.Count > 0 ? "External Track Update" : "External Subtitle Update")
                    .ConfigureAwait(false);
            }
        }

        private static string NormalizePath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim();
        }
    }
}
