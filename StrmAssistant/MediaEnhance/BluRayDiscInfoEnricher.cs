using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StrmAssistant.MediaEnhance
{
    public sealed class BluRayDiscEnrichmentSummary
    {
        public bool Attempted { get; set; }
        public bool Applied { get; set; }
        public string PlaylistName { get; set; }
        public List<string> PlayableFiles { get; set; } = new List<string>();
        public int DiscStreamCount { get; set; }
        public int DiscChapterCount { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Uses Emby's own Blu-ray examiner for BDMV folders. For multi-M2TS playlists,
    /// BDInfo is authoritative for stream language/layout and chapter positions while
    /// ffprobe remains the fallback for video dimensions/bitrate when BDInfo omits them.
    /// </summary>
    public sealed class BluRayDiscInfoEnricher
    {
        private readonly IBlurayExaminer _blurayExaminer;

        public BluRayDiscInfoEnricher(IBlurayExaminer blurayExaminer)
        {
            _blurayExaminer = blurayExaminer;
        }

        public BluRayDiscEnrichmentSummary Enrich(Video item, OpticalProbeResult probeResult)
        {
            var summary = new BluRayDiscEnrichmentSummary();
            if (item == null || probeResult == null || !probeResult.Success || _blurayExaminer == null)
                return summary;

            if (OpticalMediaProbe.GetMediaKind(item) != OpticalMediaKind.BluRayDirectory)
                return summary;

            summary.Attempted = true;
            var path = ResolveDiscRoot(item.Path);
            if (string.IsNullOrWhiteSpace(path))
            {
                summary.Error = "Unable to resolve BDMV disc root.";
                return summary;
            }

            try
            {
                var discInfo = _blurayExaminer.GetDiscInfo(path);
                if (discInfo == null)
                {
                    summary.Error = "Emby Blu-ray examiner returned no disc information.";
                    return summary;
                }

                summary.PlaylistName = discInfo.PlaylistName;
                summary.PlayableFiles = discInfo.Files?.Where(file => !string.IsNullOrWhiteSpace(file)).ToList()
                                        ?? new List<string>();
                summary.DiscStreamCount = discInfo.MediaStreams?.Length ?? 0;
                summary.DiscChapterCount = discInfo.Chapters?.Length ?? 0;

                // Emby itself only replaces ffprobe streams with BDInfo for multi-file playlists.
                if (summary.PlayableFiles.Count <= 1 || summary.DiscStreamCount == 0)
                    return summary;

                var ffprobeVideo = probeResult.Streams?
                    .FirstOrDefault(stream => string.Equals(stream.Type, "video", StringComparison.OrdinalIgnoreCase));

                var enrichedStreams = discInfo.MediaStreams
                    .Select(ToProbeStream)
                    .ToList();

                var enrichedVideo = enrichedStreams
                    .FirstOrDefault(stream => string.Equals(stream.Type, "video", StringComparison.OrdinalIgnoreCase));
                if (enrichedVideo != null && ffprobeVideo != null)
                {
                    if (!enrichedVideo.Width.HasValue || enrichedVideo.Width.Value == 0)
                        enrichedVideo.Width = ffprobeVideo.Width;
                    if (!enrichedVideo.Height.HasValue || enrichedVideo.Height.Value == 0)
                        enrichedVideo.Height = ffprobeVideo.Height;
                    if (!enrichedVideo.BitRate.HasValue || enrichedVideo.BitRate.Value == 0)
                        enrichedVideo.BitRate = ffprobeVideo.BitRate;
                    if (!enrichedVideo.AverageFrameRate.HasValue)
                        enrichedVideo.AverageFrameRate = ffprobeVideo.AverageFrameRate;
                    if (string.IsNullOrWhiteSpace(enrichedVideo.PixelFormat))
                        enrichedVideo.PixelFormat = ffprobeVideo.PixelFormat;
                    if (string.IsNullOrWhiteSpace(enrichedVideo.ColorTransfer))
                        enrichedVideo.ColorTransfer = ffprobeVideo.ColorTransfer;
                    if (string.IsNullOrWhiteSpace(enrichedVideo.ColorPrimaries))
                        enrichedVideo.ColorPrimaries = ffprobeVideo.ColorPrimaries;
                    if (string.IsNullOrWhiteSpace(enrichedVideo.ColorSpace))
                        enrichedVideo.ColorSpace = ffprobeVideo.ColorSpace;
                }

                probeResult.Streams = enrichedStreams;

                if (discInfo.RunTimeTicks.HasValue && discInfo.RunTimeTicks.Value > 0)
                    probeResult.RunTimeTicks = discInfo.RunTimeTicks;

                if (discInfo.Chapters != null && discInfo.Chapters.Length > 0)
                {
                    probeResult.Chapters = discInfo.Chapters
                        .Where(seconds => seconds >= 0 && !double.IsNaN(seconds) && !double.IsInfinity(seconds))
                        .Select((seconds, index) => new OpticalProbeChapterInfo
                        {
                            Index = index,
                            StartSeconds = seconds,
                            Title = "Chapter " + (index + 1).ToString("00")
                        })
                        .ToList();
                }

                probeResult.Success = probeResult.Streams.Count > 0 || probeResult.Chapters.Count > 0 ||
                                      probeResult.RunTimeTicks.HasValue;
                summary.Applied = true;
            }
            catch (Exception ex)
            {
                summary.Error = ex.Message;
            }

            return summary;
        }

        private static OpticalProbeStreamInfo ToProbeStream(MediaStream stream)
        {
            return new OpticalProbeStreamInfo
            {
                Index = stream.Index,
                Type = stream.Type.ToString().ToLowerInvariant(),
                Codec = stream.Codec,
                Profile = stream.Profile,
                Language = stream.Language,
                Title = stream.Title,
                Width = stream.Width,
                Height = stream.Height,
                BitRate = stream.BitRate,
                Channels = stream.Channels,
                SampleRate = stream.SampleRate,
                ChannelLayout = stream.ChannelLayout,
                PixelFormat = stream.PixelFormat,
                ColorTransfer = stream.ColorTransfer,
                ColorPrimaries = stream.ColorPrimaries,
                ColorSpace = stream.ColorSpace,
                AverageFrameRate = stream.AverageFrameRate,
                IsDefault = stream.IsDefault,
                IsForced = stream.IsForced
            };
        }

        private static string ResolveDiscRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(Path.GetFileName(trimmed), "BDMV", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(trimmed)
                : trimmed;
        }
    }
}
