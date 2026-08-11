using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace StrmAssistant.MediaEnhance
{
    public sealed class OpticalWriteBackPlan
    {
        public bool Valid { get; set; }
        public string Error { get; set; }
        public int ExistingStreamCount { get; set; }
        public int ExistingExternalStreamCount { get; set; }
        public int ProbeEmbeddedStreamCount { get; set; }
        public int ResultStreamCount { get; set; }
        public int ExistingChapterCount { get; set; }
        public int ProbeChapterCount { get; set; }
        public int PreservedMarkerChapterCount { get; set; }
        public int ResultChapterCount { get; set; }
        public bool WillUpdateRuntime { get; set; }
        public bool WillUpdateBitrate { get; set; }
        public bool WillUpdateDimensions { get; set; }
    }

    public sealed class OpticalWriteBackResult
    {
        public bool Success { get; set; }
        public bool RolledBack { get; set; }
        public string Error { get; set; }
        public OpticalWriteBackPlan Plan { get; set; }
        public int SavedStreamCount { get; set; }
        public int SavedChapterCount { get; set; }
    }

    /// <summary>
    /// Converts a validated read-only optical probe into Emby media streams/chapters.
    /// This engine is only called by the explicit admin confirmation endpoint.
    /// </summary>
    public sealed class OpticalMediaWriteBack
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IJsonSerializer _jsonSerializer;

        public OpticalMediaWriteBack(ILibraryManager libraryManager, IItemRepository itemRepository,
            IJsonSerializer jsonSerializer)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _jsonSerializer = jsonSerializer;
        }

        public OpticalWriteBackPlan BuildPlan(Video item, OpticalProbeResult probeResult)
        {
            var plan = new OpticalWriteBackPlan();
            if (item == null)
            {
                plan.Error = "Video item is null.";
                return plan;
            }

            if (probeResult == null || !probeResult.Success)
            {
                plan.Error = probeResult?.Error ?? "Optical probe did not succeed.";
                return plan;
            }

            var mappedStreams = MapEmbeddedStreams(probeResult.Streams);
            if (!mappedStreams.Any(stream => stream.Type == MediaStreamType.Video || stream.Type == MediaStreamType.Audio))
            {
                plan.Error = "Probe result contains no usable video/audio streams. Write-back is blocked.";
                return plan;
            }

            var existingStreams = item.GetMediaStreams() ?? new List<MediaStream>();
            var existingChapters = _itemRepository.GetChapters(item) ?? new List<ChapterInfo>();
            var markerCount = existingChapters.Count(IsMarkerChapter);

            plan.ExistingStreamCount = existingStreams.Count;
            plan.ExistingExternalStreamCount = existingStreams.Count(stream => stream.IsExternal);
            plan.ProbeEmbeddedStreamCount = mappedStreams.Count;
            plan.ResultStreamCount = mappedStreams.Count + plan.ExistingExternalStreamCount;
            plan.ExistingChapterCount = existingChapters.Count;
            plan.ProbeChapterCount = probeResult.Chapters?.Count ?? 0;
            plan.PreservedMarkerChapterCount = markerCount;
            plan.ResultChapterCount = plan.ProbeChapterCount > 0
                ? BuildChapterList(existingChapters, probeResult.Chapters).Count
                : existingChapters.Count;
            plan.WillUpdateRuntime = probeResult.RunTimeTicks.HasValue && probeResult.RunTimeTicks.Value > 0;
            plan.WillUpdateBitrate = probeResult.BitRate.HasValue && probeResult.BitRate.Value > 0;
            plan.WillUpdateDimensions = mappedStreams.Any(stream =>
                stream.Type == MediaStreamType.Video && stream.Width.HasValue && stream.Height.HasValue);
            plan.Valid = true;
            return plan;
        }

        public OpticalWriteBackResult Apply(Video item, OpticalProbeResult probeResult)
        {
            var plan = BuildPlan(item, probeResult);
            var result = new OpticalWriteBackResult { Plan = plan };
            if (!plan.Valid)
            {
                result.Error = plan.Error;
                return result;
            }

            var oldStreams = CloneList(item.GetMediaStreams() ?? new List<MediaStream>());
            var oldChapters = CloneList(_itemRepository.GetChapters(item) ?? new List<ChapterInfo>());
            var oldRunTimeTicks = item.RunTimeTicks;
            var oldTotalBitrate = item.TotalBitrate;
            var oldWidth = item.Width;
            var oldHeight = item.Height;

            var newStreams = MapEmbeddedStreams(probeResult.Streams);
            var preservedExternalStreams = CloneList(oldStreams.Where(stream => stream.IsExternal).ToList());
            ReindexExternalStreams(newStreams, preservedExternalStreams);
            newStreams.AddRange(preservedExternalStreams);

            var newChapters = probeResult.Chapters != null && probeResult.Chapters.Count > 0
                ? BuildChapterList(oldChapters, probeResult.Chapters)
                : CloneList(oldChapters);

            try
            {
                if (probeResult.RunTimeTicks.HasValue && probeResult.RunTimeTicks.Value > 0)
                    item.RunTimeTicks = probeResult.RunTimeTicks;
                if (probeResult.BitRate.HasValue && probeResult.BitRate.Value > 0)
                    item.TotalBitrate = probeResult.BitRate.Value;

                var mainVideoStream = newStreams
                    .Where(stream => stream.Type == MediaStreamType.Video && stream.Width.HasValue && stream.Height.HasValue)
                    .OrderByDescending(stream => (long)stream.Width.Value * stream.Height.Value)
                    .FirstOrDefault();
                if (mainVideoStream != null)
                {
                    item.Width = mainVideoStream.Width.GetValueOrDefault();
                    item.Height = mainVideoStream.Height.GetValueOrDefault();
                }

                _itemRepository.SaveMediaStreams(item.InternalId, newStreams, CancellationToken.None);
                _itemRepository.SaveChapters(item.InternalId, newChapters);
                _libraryManager.UpdateItems(new List<BaseItem> { item }, null,
                    ItemUpdateType.MetadataImport, false, false, null, CancellationToken.None);

                result.Success = true;
                result.SavedStreamCount = newStreams.Count;
                result.SavedChapterCount = newChapters.Count;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "Optical write-back failed: " + ex.Message;

                try
                {
                    item.RunTimeTicks = oldRunTimeTicks;
                    item.TotalBitrate = oldTotalBitrate;
                    item.Width = oldWidth;
                    item.Height = oldHeight;
                    _itemRepository.SaveMediaStreams(item.InternalId, oldStreams, CancellationToken.None);
                    _itemRepository.SaveChapters(item.InternalId, oldChapters);
                    _libraryManager.UpdateItems(new List<BaseItem> { item }, null,
                        ItemUpdateType.MetadataImport, false, false, null, CancellationToken.None);
                    result.RolledBack = true;
                }
                catch (Exception rollbackEx)
                {
                    result.Error += " Rollback also failed: " + rollbackEx.Message;
                }

                return result;
            }
        }

        private List<MediaStream> MapEmbeddedStreams(IEnumerable<OpticalProbeStreamInfo> streams)
        {
            var result = new List<MediaStream>();
            foreach (var source in streams ?? Enumerable.Empty<OpticalProbeStreamInfo>())
            {
                if (!TryMapType(source.Type, out var type)) continue;

                result.Add(new MediaStream
                {
                    Index = source.Index,
                    Type = type,
                    Codec = source.Codec,
                    Profile = source.Profile,
                    Language = source.Language,
                    Title = source.Title,
                    Width = source.Width,
                    Height = source.Height,
                    BitRate = source.BitRate,
                    Channels = source.Channels,
                    SampleRate = source.SampleRate,
                    ChannelLayout = source.ChannelLayout,
                    PixelFormat = source.PixelFormat,
                    ColorTransfer = source.ColorTransfer,
                    ColorPrimaries = source.ColorPrimaries,
                    ColorSpace = source.ColorSpace,
                    AverageFrameRate = source.AverageFrameRate,
                    IsDefault = source.IsDefault,
                    IsForced = source.IsForced,
                    IsExternal = false
                });
            }

            return result.OrderBy(stream => stream.Index).ToList();
        }

        private static bool TryMapType(string type, out MediaStreamType mediaStreamType)
        {
            if (string.Equals(type, "video", StringComparison.OrdinalIgnoreCase))
            {
                mediaStreamType = MediaStreamType.Video;
                return true;
            }

            if (string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase))
            {
                mediaStreamType = MediaStreamType.Audio;
                return true;
            }

            if (string.Equals(type, "subtitle", StringComparison.OrdinalIgnoreCase))
            {
                mediaStreamType = MediaStreamType.Subtitle;
                return true;
            }

            mediaStreamType = default;
            return false;
        }

        private static void ReindexExternalStreams(List<MediaStream> embedded, List<MediaStream> external)
        {
            var nextIndex = embedded.Count == 0 ? 0 : embedded.Max(stream => stream.Index) + 1;
            foreach (var stream in external.OrderBy(stream => stream.Index))
            {
                stream.Index = nextIndex++;
            }
        }

        private static List<ChapterInfo> BuildChapterList(List<ChapterInfo> existing,
            IEnumerable<OpticalProbeChapterInfo> probeChapters)
        {
            var chapters = probeChapters
                .Where(chapter => chapter.StartSeconds.HasValue && chapter.StartSeconds.Value >= 0)
                .Select((chapter, index) => new ChapterInfo
                {
                    StartPositionTicks = SecondsToTicks(chapter.StartSeconds.Value),
                    Name = string.IsNullOrWhiteSpace(chapter.Title)
                        ? "Chapter " + (index + 1).ToString("00")
                        : chapter.Title.Trim()
                })
                .ToList();

            chapters.AddRange(existing.Where(IsMarkerChapter));
            return chapters
                .GroupBy(chapter => new { chapter.StartPositionTicks, chapter.Name })
                .Select(group => group.First())
                .OrderBy(chapter => chapter.StartPositionTicks)
                .ToList();
        }

        private static bool IsMarkerChapter(ChapterInfo chapter)
        {
            if (chapter == null) return false;

            try
            {
                var property = chapter.GetType().GetProperty("MarkerType",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var value = property?.GetValue(chapter);
                if (value != null)
                {
                    var text = value.ToString();
                    if (!string.IsNullOrWhiteSpace(text) &&
                        !string.Equals(text, "None", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(text, "Unknown", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(text, "0", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch
            {
                // Fall through to the StrmAssistant marker suffix check.
            }

            return chapter.Name?.EndsWith("#SA", StringComparison.OrdinalIgnoreCase) == true;
        }

        private static long SecondsToTicks(double seconds)
        {
            if (seconds <= 0) return 0;
            var ticks = seconds * TimeSpan.TicksPerSecond;
            return ticks > long.MaxValue ? long.MaxValue : Convert.ToInt64(ticks);
        }

        private List<T> CloneList<T>(List<T> source)
        {
            if (source == null || source.Count == 0) return new List<T>();
            var json = _jsonSerializer.SerializeToString(source);
            return _jsonSerializer.DeserializeFromString<List<T>>(json) ?? new List<T>();
        }
    }
}
