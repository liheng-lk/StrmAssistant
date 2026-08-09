using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.IntroSkip
{
    public sealed class UnifiedIntroDbAutoApplyEntryPoint : IServerEntryPoint
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly UnifiedIntroDbBridge _bridge;
        private readonly ConcurrentDictionary<long, byte> _pending = new ConcurrentDictionary<long, byte>();
        private CancellationTokenSource _disposeToken = new CancellationTokenSource();

        public UnifiedIntroDbAutoApplyEntryPoint(ILibraryManager libraryManager, IItemRepository itemRepository,
            IHttpClient httpClient, IJsonSerializer jsonSerializer)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _bridge = new UnifiedIntroDbBridge(httpClient, jsonSerializer);
        }

        public void Run()
        {
            _libraryManager.ItemAdded += OnItemAdded;
        }

        public void Dispose()
        {
            _libraryManager.ItemAdded -= OnItemAdded;
            try { _disposeToken.Cancel(); } catch { }
            _disposeToken.Dispose();
        }

        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            if (!(e?.Item is Episode episode)) return;
            var options = UnifiedIntroDbRuntimeSettings.GetSnapshot();
            if (!options.Enabled || !options.AutoApplyOnItemAdded) return;
            if (!_pending.TryAdd(episode.InternalId, 0)) return;
            _ = ProcessAsync(episode.InternalId, _disposeToken.Token);
        }

        private async Task ProcessAsync(long internalId, CancellationToken cancellationToken)
        {
            try
            {
                var options = UnifiedIntroDbRuntimeSettings.GetSnapshot();
                if (!options.Enabled || !options.AutoApplyOnItemAdded) return;

                await Task.Delay(TimeSpan.FromSeconds(options.AutoApplyDelaySeconds), cancellationToken)
                    .ConfigureAwait(false);

                var episode = _libraryManager.GetItemById(internalId) as Episode;
                if (episode == null) return;

                var chapters = _itemRepository.GetChapters(episode) ?? new List<ChapterInfo>();
                var markerTypes = new HashSet<MarkerType>
                {
                    MarkerType.IntroStart,
                    MarkerType.IntroEnd,
                    MarkerType.CreditsStart
                };
                var existingMarkerTypes = new HashSet<MarkerType>(
                    chapters.Where(c => markerTypes.Contains(c.MarkerType)).Select(c => c.MarkerType));

                if (!options.OverwriteExistingMarkers &&
                    existingMarkerTypes.Contains(MarkerType.IntroStart) &&
                    existingMarkerTypes.Contains(MarkerType.IntroEnd) &&
                    (!options.AllowCreditsMarker || existingMarkerTypes.Contains(MarkerType.CreditsStart)))
                    return;

                var document = await _bridge.FetchAsync(episode, cancellationToken).ConfigureAwait(false);
                if (document == null) return;
                if (document.Confidence.HasValue && document.Confidence.Value < options.MinimumConfidence) return;

                if (options.OverwriteExistingMarkers)
                    chapters.RemoveAll(c => markerTypes.Contains(c.MarkerType));

                AddMarkerIfNeeded(chapters, existingMarkerTypes, MarkerType.IntroStart,
                    document.IntroStartSeconds.Value, options.OverwriteExistingMarkers);
                AddMarkerIfNeeded(chapters, existingMarkerTypes, MarkerType.IntroEnd,
                    document.IntroEndSeconds.Value, options.OverwriteExistingMarkers);
                if (options.AllowCreditsMarker && document.CreditsStartSeconds.HasValue)
                    AddMarkerIfNeeded(chapters, existingMarkerTypes, MarkerType.CreditsStart,
                        document.CreditsStartSeconds.Value, options.OverwriteExistingMarkers);

                chapters = chapters
                    .OrderBy(c => c.StartPositionTicks)
                    .ThenBy(c => (int)c.MarkerType)
                    .ToList();
                _itemRepository.SaveChapters(episode.Id, chapters);

                Plugin.Instance?.Logger?.Info(
                    "Unified IntroDb auto-applied markers for {0} S{1:00}E{2:00} ({3}).",
                    episode.Series?.Name ?? episode.Name,
                    episode.ParentIndexNumber ?? 0,
                    episode.IndexNumber ?? 0,
                    document.Source ?? "bridge");
            }
            catch (OperationCanceledException)
            {
                // Server shutdown or plugin unload.
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Unified IntroDb auto-apply failed for item {0}: {1}",
                    internalId, ex.Message);
            }
            finally
            {
                _pending.TryRemove(internalId, out _);
            }
        }

        private static void AddMarkerIfNeeded(List<ChapterInfo> chapters, HashSet<MarkerType> existing,
            MarkerType markerType, double seconds, bool overwrite)
        {
            if (!overwrite && existing.Contains(markerType)) return;
            chapters.Add(new ChapterInfo
            {
                MarkerType = markerType,
                StartPositionTicks = TimeSpan.FromSeconds(seconds).Ticks,
                Name = markerType.ToString()
            });
        }
    }
}
