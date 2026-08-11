using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmAssistant.MediaEnhance;
using StrmAssistant.Options;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Common
{
    public class FingerprintApi
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly IItemRepository _itemRepository;
        private readonly ILogger _logger;
        private readonly IApplicationPaths _applicationPaths;
        private readonly IFfmpegManager _ffmpegManager;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IMediaMountManager _mediaMountManager;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IServerApplicationHost _serverApplicationHost;

        private readonly object _audioFingerprintManager;
        private readonly ConstructorInfo _audioFingerprintManagerConstructor;
        private readonly MethodInfo _createTitleFingerprint;
        private readonly MethodInfo _getAllFingerprintFilesForSeason;
        private readonly MethodInfo _updateSequencesForSeason;
        private readonly FieldInfo _timeoutMs;

        private readonly object _distributedManagerLock = new object();
        private object _distributedAudioFingerprintManager;
        private string _distributedFingerprintExecutable;

        public static List<string> LibraryPathsInScope;

        public FingerprintApi(ILibraryManager libraryManager, IFileSystem fileSystem,
            IApplicationPaths applicationPaths, IFfmpegManager ffmpegManager, IMediaEncoder mediaEncoder,
            IMediaMountManager mediaMountManager, IJsonSerializer jsonSerializer, IItemRepository itemRepository,
            IServerApplicationHost serverApplicationHost)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _itemRepository = itemRepository;
            _applicationPaths = applicationPaths;
            _ffmpegManager = ffmpegManager;
            _mediaEncoder = mediaEncoder;
            _mediaMountManager = mediaMountManager;
            _jsonSerializer = jsonSerializer;
            _serverApplicationHost = serverApplicationHost;

            UpdateLibraryPathsInScope();

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var audioFingerprintManager = embyProviders.GetType("Emby.Providers.Markers.AudioFingerprintManager");
                _audioFingerprintManagerConstructor = audioFingerprintManager?.GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[]
                    {
                        typeof(IFileSystem), typeof(ILogger), typeof(IApplicationPaths), typeof(IFfmpegManager),
                        typeof(IMediaEncoder), typeof(IMediaMountManager), typeof(IJsonSerializer),
                        typeof(IServerApplicationHost)
                    }, null);
                _audioFingerprintManager = _audioFingerprintManagerConstructor?.Invoke(new object[]
                {
                    fileSystem, _logger, applicationPaths, ffmpegManager, mediaEncoder, mediaMountManager,
                    jsonSerializer, serverApplicationHost
                });
                _createTitleFingerprint = audioFingerprintManager?.GetMethod("CreateTitleFingerprint",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[]
                    {
                        typeof(Episode), typeof(LibraryOptions), typeof(IDirectoryService),
                        typeof(CancellationToken)
                    }, null);
                _getAllFingerprintFilesForSeason = audioFingerprintManager?.GetMethod("GetAllFingerprintFilesForSeason",
                    BindingFlags.Public | BindingFlags.Instance);
                _updateSequencesForSeason = audioFingerprintManager?.GetMethod("UpdateSequencesForSeason",
                    BindingFlags.Public | BindingFlags.Instance);
                _timeoutMs = audioFingerprintManager?.GetField("TimeoutMs",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                PatchTimeout(Plugin.Instance.GetPluginOptions().GeneralOptions.MaxConcurrentCount);
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    _logger.Debug(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            if (_audioFingerprintManager is null || _audioFingerprintManagerConstructor is null ||
                _createTitleFingerprint is null || _getAllFingerprintFilesForSeason is null ||
                _updateSequencesForSeason is null || _timeoutMs is null)
            {
                _logger.Warn($"{nameof(FingerprintApi)} Init Failed");
            }
        }

        public async Task<Tuple<string, bool>> CreateTitleFingerprint(Episode item,
            IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            if (MediaExtractionFilter.ShouldSkip(item, out var reason))
            {
                _logger.Info("IntroFingerprintExtract - Skipped by extraction blacklist: {0} ({1})", item.Path,
                    reason);
                return Tuple.Create(string.Empty, false);
            }

            var libraryOptions = _libraryManager.GetLibraryOptions(item);
            libraryOptions.IntroDetectionFingerprintLength = GetFingerprintMinutes(item);
            var manager = SelectFingerprintManager(item, out var distributed);
            if (manager == null)
            {
                _logger.Warn("IntroFingerprintExtract - No usable fingerprint manager for {0}", item.Path);
                return Tuple.Create(string.Empty, false);
            }

            try
            {
                return await InvokeCreateTitleFingerprint(manager, item, libraryOptions, directoryService,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (distributed && ShouldFallbackToNative())
            {
                _logger.Warn("IntroFingerprintExtract - Distributed fingerprint failed for {0}; falling back to Emby native ffmpeg. {1}",
                    item.Path, ex.Message);
                return await InvokeCreateTitleFingerprint(_audioFingerprintManager, item, libraryOptions,
                        directoryService, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        public Task<Tuple<string, bool>> CreateTitleFingerprint(Episode item, CancellationToken cancellationToken)
        {
            var directoryService = new DirectoryService(_logger, _fileSystem);
            return CreateTitleFingerprint(item, directoryService, cancellationToken);
        }

        private Task<Tuple<string, bool>> InvokeCreateTitleFingerprint(object manager, Episode item,
            LibraryOptions libraryOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            if (manager == null || _createTitleFingerprint == null)
                return Task.FromResult(Tuple.Create(string.Empty, false));

            try
            {
                return (Task<Tuple<string, bool>>)_createTitleFingerprint.Invoke(manager,
                    new object[] { item, libraryOptions, directoryService, cancellationToken });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        private async Task<object> GetAllFingerprintFilesForSeason(object manager, Season season, Episode[] episodes,
            LibraryOptions libraryOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            try
            {
                var invoked = _getAllFingerprintFilesForSeason.Invoke(manager,
                    new object[] { season, episodes, libraryOptions, directoryService, cancellationToken });
                if (!(invoked is Task task))
                    throw new InvalidOperationException("GetAllFingerprintFilesForSeason did not return Task.");

                await task.ConfigureAwait(false);
                return task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(task);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        private void UpdateSequencesForSeason(object manager, Season season, object seasonFingerprintInfo,
            Episode episode, LibraryOptions libraryOptions, IDirectoryService directoryService,
            CancellationToken cancellationToken)
        {
            try
            {
                var parameters = _updateSequencesForSeason.GetParameters();
                var args = parameters.Length >= 6
                    ? new object[]
                    {
                        season, seasonFingerprintInfo, episode, libraryOptions, directoryService, cancellationToken
                    }
                    : new object[]
                    {
                        season, seasonFingerprintInfo, episode, libraryOptions, directoryService
                    };

                _updateSequencesForSeason.Invoke(manager, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        public void PatchTimeout(int maxConcurrentCount)
        {
            var newTimeout = Math.Max(1, maxConcurrentCount) *
                             Convert.ToInt32(TimeSpan.FromMinutes(10.0).TotalMilliseconds);
            PatchTimeoutForManager(_audioFingerprintManager, newTimeout);

            lock (_distributedManagerLock)
            {
                PatchTimeoutForManager(_distributedAudioFingerprintManager, newTimeout);
            }
        }

        private void PatchTimeoutForManager(object manager, int timeoutMs)
        {
            if (manager == null || _timeoutMs == null) return;
            try
            {
                _timeoutMs.SetValue(manager, timeoutMs);
            }
            catch (Exception ex)
            {
                if (Plugin.Instance.DebugMode)
                    _logger.Debug("Fingerprint timeout patch failed: " + ex.Message);
            }
        }

        private object SelectFingerprintManager(Episode item, out bool distributed)
        {
            distributed = false;
            var introOptions = Plugin.Instance?.GetPluginOptions()?.IntroSkipOptions;
            if (introOptions?.EnableDistributedFingerprintRouting != true)
                return _audioFingerprintManager;

            if (item?.IsShortcut == true && introOptions.EnableDistributedFingerprintForStrm != true)
            {
                if (Plugin.Instance.DebugMode)
                    _logger.Debug("IntroFingerprintExtract - STRM keeps native fingerprint route: " + item.Path);
                return _audioFingerprintManager;
            }

            var executable = GetDistributedFingerprintExecutable();
            if (string.IsNullOrWhiteSpace(executable))
            {
                _logger.Warn("IntroFingerprintExtract - Distributed fingerprint routing is enabled but no distributed ffmpeg path is configured.");
                return introOptions.DistributedFingerprintFallbackToEmby ? _audioFingerprintManager : null;
            }

            try
            {
                var manager = GetOrCreateDistributedManager(executable);
                distributed = manager != null;
                if (manager != null) return manager;
            }
            catch (Exception ex)
            {
                _logger.Warn("IntroFingerprintExtract - Unable to create distributed fingerprint manager: " + ex.Message);
            }

            return introOptions.DistributedFingerprintFallbackToEmby ? _audioFingerprintManager : null;
        }

        private object SelectFingerprintManager(IEnumerable<Episode> episodes, out bool distributed)
        {
            distributed = false;
            var introOptions = Plugin.Instance?.GetPluginOptions()?.IntroSkipOptions;
            if (introOptions?.EnableDistributedFingerprintRouting != true)
                return _audioFingerprintManager;

            var episodeArray = episodes?.Where(e => e != null).ToArray() ?? Array.Empty<Episode>();
            if (episodeArray.Any(e => e.IsShortcut) && introOptions.EnableDistributedFingerprintForStrm != true)
            {
                if (Plugin.Instance.DebugMode)
                    _logger.Debug("IntroFingerprintExtract - Season contains STRM; keeping native fingerprint route.");
                return _audioFingerprintManager;
            }

            var executable = GetDistributedFingerprintExecutable();
            if (string.IsNullOrWhiteSpace(executable))
            {
                _logger.Warn("IntroFingerprintExtract - Distributed fingerprint routing is enabled but no distributed ffmpeg path is configured.");
                return introOptions.DistributedFingerprintFallbackToEmby ? _audioFingerprintManager : null;
            }

            try
            {
                var manager = GetOrCreateDistributedManager(executable);
                distributed = manager != null;
                if (manager != null) return manager;
            }
            catch (Exception ex)
            {
                _logger.Warn("IntroFingerprintExtract - Unable to create distributed fingerprint manager: " + ex.Message);
            }

            return introOptions.DistributedFingerprintFallbackToEmby ? _audioFingerprintManager : null;
        }

        private object GetOrCreateDistributedManager(string executable)
        {
            lock (_distributedManagerLock)
            {
                if (_distributedAudioFingerprintManager != null &&
                    string.Equals(_distributedFingerprintExecutable, executable,
                        StringComparison.OrdinalIgnoreCase))
                    return _distributedAudioFingerprintManager;

                if (_audioFingerprintManagerConstructor == null || _ffmpegManager == null || _mediaEncoder == null)
                    return null;

                var ffmpegProxy = DistributedFfmpegPathProxy.CreateManagerProxy(_ffmpegManager, executable);
                var encoderProxy = DistributedFfmpegPathProxy.CreateMediaEncoderProxy(_mediaEncoder, ffmpegProxy,
                    executable);

                var manager = _audioFingerprintManagerConstructor.Invoke(new object[]
                {
                    _fileSystem, _logger, _applicationPaths, ffmpegProxy, encoderProxy, _mediaMountManager,
                    _jsonSerializer, _serverApplicationHost
                });

                if (manager == null) return null;

                var maxConcurrentCount = Plugin.Instance?.GetPluginOptions()?.GeneralOptions.MaxConcurrentCount ?? 1;
                var newTimeout = Math.Max(1, maxConcurrentCount) *
                                 Convert.ToInt32(TimeSpan.FromMinutes(10.0).TotalMilliseconds);
                PatchTimeoutForManager(manager, newTimeout);

                _distributedAudioFingerprintManager = manager;
                _distributedFingerprintExecutable = executable;
                _logger.Info("IntroFingerprintExtract - Isolated distributed fingerprint manager initialized: {0}",
                    executable);
                return manager;
            }
        }

        private static string GetDistributedFingerprintExecutable()
        {
            var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
            return string.IsNullOrWhiteSpace(options?.DistributedFfmpegExecutablePath)
                ? null
                : options.DistributedFfmpegExecutablePath.Trim().Trim('"');
        }

        private static bool ShouldFallbackToNative()
        {
            return Plugin.Instance?.GetPluginOptions()?.IntroSkipOptions?.DistributedFingerprintFallbackToEmby != false;
        }

        public int GetFingerprintMinutes(BaseItem item)
        {
            var options = Plugin.Instance?.GetPluginOptions()?.IntroSkipOptions;
            var fallback = ClampFingerprintMinutes(options?.IntroDetectionFingerprintMinutes ?? 10);
            var overrides = ParseFingerprintDurationOverrides(options?.FingerprintDurationOverrides);
            if (overrides.Count == 0 || item == null) return fallback;

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (item is CollectionFolder folder)
            {
                keys.Add(folder.Name ?? string.Empty);
                keys.Add(folder.InternalId.ToString());
            }
            else
            {
                try
                {
                    foreach (var collectionFolder in _libraryManager.GetCollectionFolders(item))
                    {
                        if (collectionFolder == null) continue;
                        keys.Add(collectionFolder.Name ?? string.Empty);
                        keys.Add(collectionFolder.InternalId.ToString());
                    }
                }
                catch
                {
                    // The global fallback remains authoritative when a collection folder cannot be resolved.
                }
            }

            foreach (var key in keys.Where(k => !string.IsNullOrWhiteSpace(k)))
            {
                if (overrides.TryGetValue(key.Trim(), out var minutes)) return minutes;
            }

            return fallback;
        }

        private int GetMinimumConfiguredFingerprintMinutes()
        {
            var options = Plugin.Instance?.GetPluginOptions()?.IntroSkipOptions;
            var values = ParseFingerprintDurationOverrides(options?.FingerprintDurationOverrides).Values.ToList();
            values.Add(ClampFingerprintMinutes(options?.IntroDetectionFingerprintMinutes ?? 10));
            return values.Min();
        }

        private static Dictionary<string, int> ParseFingerprintDurationOverrides(string value)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(value)) return result;

            foreach (var rawLine in value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                var separator = line.IndexOf("=>", StringComparison.Ordinal);
                var separatorLength = 2;
                if (separator < 0)
                {
                    separator = line.IndexOf('=');
                    separatorLength = 1;
                }

                if (separator <= 0) continue;
                var key = line.Substring(0, separator).Trim().Trim('"');
                var minutesText = line.Substring(separator + separatorLength).Trim();
                if (string.IsNullOrWhiteSpace(key) || !int.TryParse(minutesText, out var minutes)) continue;
                result[key] = ClampFingerprintMinutes(minutes);
            }

            return result;
        }

        private static int ClampFingerprintMinutes(int value)
        {
            return Math.Max(2, Math.Min(value, 20));
        }

        public bool IsLibraryInScope(BaseItem item)
        {
            return !string.IsNullOrEmpty(item.Path) && LibraryPathsInScope.Any(l => item.Path.StartsWith(l));
        }

        public void UpdateLibraryPathsInScope()
        {
            var validLibraryIds = GetValidLibraryIds(Plugin.Instance.GetPluginOptions()
                .IntroSkipOptions.MarkerEnabledLibraryScope);

            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => f.LibraryOptions.EnableMarkerDetection &&
                            (f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null) &&
                            (!validLibraryIds.Any() || validLibraryIds.All(id => id == "-1") ||
                             validLibraryIds.Contains(f.Id)))
                .ToList();

            LibraryPathsInScope = libraries.SelectMany(l => l.Locations)
                .Select(ls => ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? ls
                    : ls + Path.DirectorySeparatorChar)
                .ToList();
        }

        public long[] GetAllFavoriteSeasons()
        {
            var favorites = LibraryApi.AllUsers.Select(e => e.Key)
                .SelectMany(u => _libraryManager.GetItemList(new InternalItemsQuery
                {
                    User = u,
                    IsFavorite = true,
                    IncludeItemTypes = new[] { nameof(Series), nameof(Episode) },
                    PathStartsWithAny = LibraryPathsInScope.ToArray()
                }))
                .GroupBy(i => i.InternalId)
                .Select(g => g.First())
                .ToList();

            var expanded = Plugin.LibraryApi.ExpandFavorites(favorites, false, null, false).OfType<Episode>();

            var result = MediaExtractionFilter.Apply(expanded)
                .GroupBy(e => e.ParentId).Select(g => g.Key).ToArray();

            return result;
        }

        public List<Episode> FetchFingerprintQueueItems(List<BaseItem> items)
        {
            var libraryIds = Plugin.Instance.GetPluginOptions().IntroSkipOptions.LibraryScope?
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToArray();

            var includeFavorites = libraryIds?.Contains("-1") == true;

            var resultItems = new List<Episode>();
            var incomingItems = MediaExtractionFilter.Apply(items.OfType<Episode>()).ToList();

            if (IsCatchupTaskSelected(GeneralOptions.CatchupTask.Fingerprint) && LibraryPathsInScope.Any())
            {
                if (includeFavorites)
                {
                    resultItems = MediaExtractionFilter.Apply(
                            Plugin.LibraryApi.ExpandFavorites(items, true, null, false).OfType<Episode>())
                        .ToList();
                }

                if (libraryIds is null || !libraryIds.Any() || libraryIds.Any(id => id != "-1"))
                {
                    var filteredItems = incomingItems
                        .Where(i => LibraryPathsInScope.Any(p => i.ContainingFolderPath.StartsWith(p)))
                        .ToList();
                    resultItems = resultItems.Concat(filteredItems).ToList();
                }
            }

            var isModSupported = Plugin.Instance.IsModSupported;
            resultItems = MediaExtractionFilter.Apply(resultItems)
                .Where(i => isModSupported || !i.IsShortcut).GroupBy(i => i.InternalId)
                .Select(g => g.First()).ToList();

            var unprocessedItems = FilterUnprocessed(resultItems);

            return unprocessedItems;
        }

        private List<Episode> FilterUnprocessed(List<Episode> items)
        {
            var enableImageCapture = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.EnableImageCapture;

            var results = new List<Episode>();

            foreach (var item in items)
            {
                if (MediaExtractionFilter.ShouldSkip(item, out var reason))
                {
                    _logger.Info("IntroFingerprintExtract - Skipped by extraction blacklist: {0} ({1})", item.Path,
                        reason);
                    continue;
                }

                if (Plugin.LibraryApi.IsExtractNeeded(item, enableImageCapture))
                {
                    results.Add(item);
                }
                else if (IsExtractNeeded(item))
                {
                    results.Add(item);
                }
            }

            _logger.Info("IntroFingerprintExtract - Number of items: " + results.Count);

            return results;
        }

        public bool IsExtractNeeded(BaseItem item)
        {
            if (MediaExtractionFilter.ShouldSkip(item, out _)) return false;

            return !Plugin.ChapterApi.HasIntro(item) &&
                   string.IsNullOrEmpty(_itemRepository.GetIntroDetectionFailureResult(item.InternalId));
        }

        public List<Episode> FetchIntroPreExtractTaskItems()
        {
            var markerEnabledLibraryScope = Plugin.Instance.GetPluginOptions().IntroSkipOptions.MarkerEnabledLibraryScope;

            var itemsFingerprintQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                HasPath = true,
                HasAudioStream = false,
            };

            if (!string.IsNullOrEmpty(markerEnabledLibraryScope) && markerEnabledLibraryScope.Contains("-1"))
            {
                itemsFingerprintQuery.ParentIds = GetAllFavoriteSeasons().DefaultIfEmpty(-1).ToArray();
            }
            else
            {
                if (LibraryPathsInScope.Any())
                {
                    itemsFingerprintQuery.PathStartsWithAny = LibraryPathsInScope.ToArray();
                }
            }

            var isModSupported = Plugin.Instance.IsModSupported;
            var items = _libraryManager.GetItemList(itemsFingerprintQuery).Where(i => isModSupported || !i.IsShortcut)
                .OfType<Episode>().ToList();

            return MediaExtractionFilter.Apply(items).ToList();
        }

        public List<Episode> FetchIntroFingerprintTaskItems()
        {
            var libraryIds = Plugin.Instance.GetPluginOptions()
                .IntroSkipOptions.MarkerEnabledLibraryScope.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
            var librariesWithMarkerDetection = _libraryManager.GetVirtualFolders()
                .Where(f => (f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null) &&
                            f.LibraryOptions.EnableMarkerDetection)
                .ToList();
            var librariesSelected = librariesWithMarkerDetection.Where(f => libraryIds.Contains(f.Id)).ToList();

            _logger.Info("IntroFingerprintExtract - LibraryScope: " + (!librariesWithMarkerDetection.Any()
                ? "NONE"
                : string.Join(", ",
                    (libraryIds.Contains("-1")
                        ? new[] { Resources.Favorites }.Concat(librariesSelected.Select(l => l.Name))
                        : librariesSelected.Select(l => l.Name)).DefaultIfEmpty("ALL"))));

            var minimumFingerprintMinutes = GetMinimumConfiguredFingerprintMinutes();
            _logger.Info("Intro Detection Minimum Fingerprint Length (Minutes): " + minimumFingerprintMinutes);

            var itemsFingerprintQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                WithoutChapterMarkers = new[] { MarkerType.IntroStart },
                MinRunTimeTicks = TimeSpan.FromMinutes(minimumFingerprintMinutes).Ticks,
                HasIntroDetectionFailure = false,
                HasAudioStream = true
            };

            if (libraryIds.All(i => i == "-1"))
            {
                itemsFingerprintQuery.ParentIds = GetAllFavoriteSeasons().DefaultIfEmpty(-1).ToArray();
            }
            else
            {
                if (LibraryPathsInScope.Any())
                {
                    itemsFingerprintQuery.PathStartsWithAny = LibraryPathsInScope.ToArray();
                }
            }

            var isModSupported = Plugin.Instance.IsModSupported;
            var items = _libraryManager.GetItemList(itemsFingerprintQuery)
                .Where(i => isModSupported || !i.IsShortcut)
                .OfType<Episode>()
                .Where(e => e.RunTimeTicks.GetValueOrDefault() >= TimeSpan.FromMinutes(GetFingerprintMinutes(e)).Ticks)
                .ToList();

            return MediaExtractionFilter.Apply(items).ToList();
        }

        public void UpdateLibraryIntroDetectionFingerprintLength()
        {
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null)
                .ToList();

            foreach (var library in libraries)
            {
                var options = library.LibraryOptions;
                if (!long.TryParse(library.ItemId, out var itemId)) continue;

                var collectionFolder = _libraryManager.GetItemById(itemId) as CollectionFolder;
                var desiredLength = collectionFolder != null
                    ? GetFingerprintMinutes(collectionFolder)
                    : ResolveVirtualFolderFingerprintMinutes(library.Name, library.ItemId);

                if (options.IntroDetectionFingerprintLength == desiredLength) continue;
                options.IntroDetectionFingerprintLength = desiredLength;
                CollectionFolder.SaveLibraryOptions(itemId, options);
                _logger.Info("IntroFingerprintExtract - Library fingerprint length updated: {0} = {1} minutes",
                    library.Name, desiredLength);
            }
        }

        private int ResolveVirtualFolderFingerprintMinutes(string name, string id)
        {
            var options = Plugin.Instance?.GetPluginOptions()?.IntroSkipOptions;
            var fallback = ClampFingerprintMinutes(options?.IntroDetectionFingerprintMinutes ?? 10);
            var overrides = ParseFingerprintDurationOverrides(options?.FingerprintDurationOverrides);
            if (!string.IsNullOrWhiteSpace(name) && overrides.TryGetValue(name.Trim(), out var byName)) return byName;
            if (!string.IsNullOrWhiteSpace(id) && overrides.TryGetValue(id.Trim(), out var byId)) return byId;
            return fallback;
        }

#nullable enable
        public async Task UpdateIntroMarkerForSeason(Season season, CancellationToken cancellationToken,
            IProgress<double>? progress = null)
        {
            var introDetectionFingerprintMinutes = GetFingerprintMinutes(season);

            var libraryOptions = _libraryManager.GetLibraryOptions(season);
            libraryOptions.IntroDetectionFingerprintLength = introDetectionFingerprintMinutes;
            var directoryService = new DirectoryService(_logger, _fileSystem);

            var episodeQuery = new InternalItemsQuery
            {
                GroupByPresentationUniqueKey = false,
                EnableTotalRecordCount = false,
                MinRunTimeTicks = TimeSpan.FromMinutes(introDetectionFingerprintMinutes).Ticks,
                HasIntroDetectionFailure = false,
                HasAudioStream = true
            };
            var allEpisodes = MediaExtractionFilter.Apply(season.GetEpisodes(episodeQuery).Items.OfType<Episode>())
                .ToArray();

            episodeQuery.WithoutChapterMarkers = new[] { MarkerType.IntroStart };
            var episodesWithoutMarkers = MediaExtractionFilter.Apply(
                    season.GetEpisodes(episodeQuery).Items.OfType<Episode>())
                .ToList();

            var manager = SelectFingerprintManager(allEpisodes, out var distributed);
            if (manager == null)
            {
                _logger.Warn("IntroFingerprintExtract - No usable fingerprint manager for season {0}", season.Path);
                progress?.Report(1.0);
                return;
            }

            try
            {
                await UpdateIntroMarkerForSeasonWithManager(manager, season, allEpisodes, episodesWithoutMarkers,
                        libraryOptions, directoryService, cancellationToken, progress)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (distributed && ShouldFallbackToNative())
            {
                _logger.Warn("IntroFingerprintExtract - Distributed season fingerprint workflow failed for {0}; restarting with Emby native ffmpeg. {1}",
                    season.Path, ex.Message);
                await UpdateIntroMarkerForSeasonWithManager(_audioFingerprintManager, season, allEpisodes,
                        episodesWithoutMarkers, libraryOptions, directoryService, cancellationToken, progress)
                    .ConfigureAwait(false);
            }
        }

        private async Task UpdateIntroMarkerForSeasonWithManager(object manager, Season season,
            Episode[] allEpisodes, IList<Episode> episodesWithoutMarkers, LibraryOptions libraryOptions,
            IDirectoryService directoryService, CancellationToken cancellationToken, IProgress<double>? progress)
        {
            var seasonFingerprintInfo = await GetAllFingerprintFilesForSeason(manager, season,
                    allEpisodes, libraryOptions, directoryService, cancellationToken)
                .ConfigureAwait(false);

            double total = episodesWithoutMarkers.Count;
            var index = 0;

            foreach (var episode in episodesWithoutMarkers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateSequencesForSeason(manager, season, seasonFingerprintInfo, episode, libraryOptions,
                    directoryService, cancellationToken);

                index++;
                progress?.Report(total == 0 ? 1.0 : index / total);
            }

            progress?.Report(1.0);
        }
#nullable restore
    }
}
