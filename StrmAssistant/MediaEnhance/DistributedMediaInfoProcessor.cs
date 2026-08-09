using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.MediaEnhance
{
    public sealed class DistributedMediaInfoProcessResult
    {
        public bool Success { get; set; }
        public bool Skipped { get; set; }
        public bool UsedRffmpegBackend { get; set; }
        public bool RolledBack { get; set; }
        public string Error { get; set; }
        public string SkipReason { get; set; }
        public string Executable { get; set; }
        public string InputPath { get; set; }
        public string StandardError { get; set; }
        public int SavedStreamCount { get; set; }
        public int SavedChapterCount { get; set; }
    }

    /// <summary>
    /// Optional MediaInfo path that invokes a configured ffprobe/rffmpeg-compatible wrapper directly.
    /// It is intentionally isolated from Emby's global encoder configuration. Existing QueueManager
    /// concurrency still governs callers; this class only processes one media item at a time.
    /// </summary>
    public sealed class DistributedMediaInfoProcessor
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IJsonSerializer _jsonSerializer;

        public DistributedMediaInfoProcessor(ILibraryManager libraryManager, IItemRepository itemRepository,
            IJsonSerializer jsonSerializer)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _jsonSerializer = jsonSerializer;
        }

        public async Task<DistributedMediaInfoProcessResult> ProcessAsync(BaseItem item, string inputPath,
            MediaInfoExtractOptions options, CancellationToken cancellationToken)
        {
            var result = new DistributedMediaInfoProcessResult { InputPath = inputPath };
            if (!CanProcess(item, inputPath, options, out var skipReason))
            {
                result.Skipped = true;
                result.SkipReason = skipReason;
                return result;
            }

            var executable = options.DistributedFfprobeExecutablePath?.Trim().Trim('"');
            result.Executable = executable;

            var args = string.Join(" ", new[]
            {
                "-v error",
                "-hide_banner",
                "-print_format json",
                "-show_format",
                "-show_streams",
                "-show_chapters",
                QuoteArgument(inputPath)
            });

            ProcessResult process;
            try
            {
                process = await RunProcessAsync(executable, args,
                        Math.Max(30, Math.Min(options.DistributedExtractTimeoutSeconds, 3600)), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result.Error = cancellationToken.IsCancellationRequested
                    ? "Distributed MediaInfo extraction was cancelled."
                    : "Distributed MediaInfo extraction timed out.";
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }

            result.StandardError = Truncate(process.StandardError, 12000);
            result.UsedRffmpegBackend = LooksLikeRffmpeg(process.StandardOutput, process.StandardError);
            if (process.ExitCode != 0)
            {
                result.Error = BuildProcessError(process);
                return result;
            }

            if (string.IsNullOrWhiteSpace(process.StandardOutput))
            {
                result.Error = "Distributed ffprobe returned no JSON output.";
                return result;
            }

            FfProbeDocument document;
            try
            {
                document = _jsonSerializer.DeserializeFromString<FfProbeDocument>(process.StandardOutput);
            }
            catch (Exception ex)
            {
                result.Error = "Distributed ffprobe JSON parse failed: " + ex.Message;
                return result;
            }

            if (document == null)
            {
                result.Error = "Distributed ffprobe JSON could not be deserialized.";
                return result;
            }

            var parsedStreams = MapStreams(document.streams).ToList();
            if (!parsedStreams.Any(stream =>
                    stream.Type == MediaStreamType.Video || stream.Type == MediaStreamType.Audio))
            {
                result.Error = "Distributed ffprobe returned no usable video/audio streams.";
                return result;
            }

            var oldStreams = CloneList(item.GetMediaStreams() ?? new List<MediaStream>());
            var oldChapters = CloneList(_itemRepository.GetChapters(item) ?? new List<ChapterInfo>());
            var oldRunTimeTicks = item.RunTimeTicks;
            var oldSize = item.Size;
            var oldContainer = item.Container;
            var oldTotalBitrate = item.TotalBitrate;
            var oldWidth = item.Width;
            var oldHeight = item.Height;
            var oldDateLastRefreshed = item.DateLastRefreshed;

            var externalStreams = CloneList(oldStreams.Where(stream => stream.IsExternal).ToList());
            ReindexExternalStreams(parsedStreams, externalStreams);
            parsedStreams.AddRange(externalStreams);

            var parsedChapters = item is Video && document.chapters != null && document.chapters.Count > 0
                ? BuildChapterList(oldChapters, document.chapters)
                : CloneList(oldChapters);

            try
            {
                ApplyItemFields(item, document, parsedStreams);
                item.DateLastRefreshed = DateTimeOffset.UtcNow;

                _itemRepository.SaveMediaStreams(item.InternalId, parsedStreams, CancellationToken.None);
                if (item is Video && document.chapters != null && document.chapters.Count > 0)
                    _itemRepository.SaveChapters(item.InternalId, parsedChapters);

                _libraryManager.UpdateItems(new List<BaseItem> { item }, null,
                    ItemUpdateType.MetadataImport, false, false, null, CancellationToken.None);

                result.Success = true;
                result.SavedStreamCount = parsedStreams.Count;
                result.SavedChapterCount = parsedChapters.Count;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "Distributed MediaInfo write-back failed: " + ex.Message;

                try
                {
                    item.RunTimeTicks = oldRunTimeTicks;
                    item.Size = oldSize;
                    item.Container = oldContainer;
                    item.TotalBitrate = oldTotalBitrate;
                    item.Width = oldWidth;
                    item.Height = oldHeight;
                    item.DateLastRefreshed = oldDateLastRefreshed;
                    _itemRepository.SaveMediaStreams(item.InternalId, oldStreams, CancellationToken.None);
                    if (item is Video) _itemRepository.SaveChapters(item.InternalId, oldChapters);
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

        private static bool CanProcess(BaseItem item, string inputPath, MediaInfoExtractOptions options,
            out string reason)
        {
            reason = null;
            if (item == null)
            {
                reason = "Item is null.";
                return false;
            }

            if (options == null || !options.EnableDistributedExtractRouting)
            {
                reason = "Distributed MediaInfo routing is disabled.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(options.DistributedFfprobeExecutablePath))
            {
                reason = "Distributed ffprobe path is empty. Configure an explicit ffprobe/rffmpeg wrapper before enabling routing.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(inputPath))
            {
                reason = "Input media path is empty.";
                return false;
            }

            if (!(item is Video) && !(item is MediaBrowser.Controller.Entities.Audio.Audio))
            {
                reason = "Only Video and Audio items are supported.";
                return false;
            }

            if (item is Video video && OpticalMediaProbe.GetMediaKind(video) != OpticalMediaKind.Unsupported)
            {
                reason = "Optical media uses the dedicated ISO/BDMV pipeline.";
                return false;
            }

            if (item.IsShortcut && !options.EnableDistributedExtractForStrm)
            {
                reason = "STRM distributed routing is disabled because mounted paths may not exist on remote workers.";
                return false;
            }

            return true;
        }

        private static IEnumerable<MediaStream> MapStreams(IEnumerable<FfProbeStream> streams)
        {
            foreach (var source in streams ?? Enumerable.Empty<FfProbeStream>())
            {
                if (!TryMapType(source, out var type)) continue;

                yield return new MediaStream
                {
                    Index = source.index,
                    Type = type,
                    Codec = source.codec_name,
                    Profile = source.profile,
                    Language = source.tags?.language,
                    Title = source.tags?.title,
                    Width = source.width,
                    Height = source.height,
                    BitRate = ParseInt(source.bit_rate),
                    Channels = source.channels,
                    SampleRate = ParseInt(source.sample_rate),
                    ChannelLayout = source.channel_layout,
                    PixelFormat = source.pix_fmt,
                    ColorTransfer = source.color_transfer,
                    ColorPrimaries = source.color_primaries,
                    ColorSpace = source.color_space,
                    AverageFrameRate = ParseFrameRate(source.avg_frame_rate),
                    IsDefault = source.disposition?.@default == 1,
                    IsForced = source.disposition?.forced == 1,
                    IsExternal = false
                };
            }
        }

        private static bool TryMapType(FfProbeStream source, out MediaStreamType type)
        {
            if (source?.disposition?.attached_pic == 1)
            {
                type = MediaStreamType.EmbeddedImage;
                return true;
            }

            if (string.Equals(source?.codec_type, "video", StringComparison.OrdinalIgnoreCase))
            {
                type = MediaStreamType.Video;
                return true;
            }

            if (string.Equals(source?.codec_type, "audio", StringComparison.OrdinalIgnoreCase))
            {
                type = MediaStreamType.Audio;
                return true;
            }

            if (string.Equals(source?.codec_type, "subtitle", StringComparison.OrdinalIgnoreCase))
            {
                type = MediaStreamType.Subtitle;
                return true;
            }

            type = default;
            return false;
        }

        private static void ApplyItemFields(BaseItem item, FfProbeDocument document, List<MediaStream> streams)
        {
            var duration = ParseDouble(document.format?.duration);
            if (duration.HasValue && duration.Value > 0)
                item.RunTimeTicks = SecondsToTicks(duration.Value);

            var size = ParseLong(document.format?.size);
            if (size.HasValue && size.Value >= 0) item.Size = size.Value;

            var bitrate = ParseInt(document.format?.bit_rate);
            if (bitrate.HasValue && bitrate.Value > 0) item.TotalBitrate = bitrate.Value;

            if (!string.IsNullOrWhiteSpace(document.format?.format_name))
                item.Container = document.format.format_name.Split(',').FirstOrDefault()?.Trim();

            var mainVideo = streams
                .Where(stream => stream.Type == MediaStreamType.Video && stream.Width.HasValue && stream.Height.HasValue)
                .OrderByDescending(stream => (long)stream.Width.Value * stream.Height.Value)
                .FirstOrDefault();
            if (mainVideo != null)
            {
                item.Width = mainVideo.Width.GetValueOrDefault();
                item.Height = mainVideo.Height.GetValueOrDefault();
            }
        }

        private static List<ChapterInfo> BuildChapterList(List<ChapterInfo> existing,
            IEnumerable<FfProbeChapter> sourceChapters)
        {
            var chapters = sourceChapters
                .Select(chapter => new
                {
                    Chapter = chapter,
                    Start = ParseDouble(chapter.start_time)
                })
                .Where(value => value.Start.HasValue && value.Start.Value >= 0)
                .Select((value, index) => new ChapterInfo
                {
                    StartPositionTicks = SecondsToTicks(value.Start.Value),
                    Name = string.IsNullOrWhiteSpace(value.Chapter.tags?.title)
                        ? "Chapter " + (index + 1).ToString("00")
                        : value.Chapter.tags.title.Trim()
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
                // Fall through to plugin-owned marker suffix.
            }

            return chapter.Name?.EndsWith("#SA", StringComparison.OrdinalIgnoreCase) == true;
        }

        private static void ReindexExternalStreams(List<MediaStream> embedded, List<MediaStream> external)
        {
            var nextIndex = embedded.Count == 0 ? 0 : embedded.Max(stream => stream.Index) + 1;
            foreach (var stream in external.OrderBy(stream => stream.Index)) stream.Index = nextIndex++;
        }

        private List<T> CloneList<T>(List<T> source)
        {
            if (source == null || source.Count == 0) return new List<T>();
            var json = _jsonSerializer.SerializeToString(source);
            return _jsonSerializer.DeserializeFromString<List<T>>(json) ?? new List<T>();
        }

        private static bool LooksLikeRffmpeg(string stdout, string stderr)
        {
            var value = (stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty);
            return Contains(value, "starting rffmpeg") || Contains(value, "database in use:") ||
                   Contains(value, "running command host=") || Contains(value, "finished rffmpeg");
        }

        private static bool Contains(string value, string needle)
        {
            return !string.IsNullOrEmpty(value) &&
                   value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int? ParseInt(string value)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var direct)) return direct;
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return null;
            return parsed > int.MaxValue ? int.MaxValue : parsed < int.MinValue ? int.MinValue : (int)parsed;
        }

        private static long? ParseLong(string value)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (long?)null;
        }

        private static double? ParseDouble(string value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (double?)null;
        }

        private static float? ParseFrameRate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var parts = value.Split('/');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
                Math.Abs(denominator) > double.Epsilon)
                return (float)(numerator / denominator);

            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var direct)
                ? direct
                : (float?)null;
        }

        private static long SecondsToTicks(double seconds)
        {
            var ticks = seconds * TimeSpan.TicksPerSecond;
            return ticks > long.MaxValue ? long.MaxValue : ticks < 0 ? 0 : Convert.ToInt64(ticks);
        }

        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string BuildProcessError(ProcessResult result)
        {
            var detail = ((result.StandardError ?? string.Empty) + "\n" + (result.StandardOutput ?? string.Empty))
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
            return string.IsNullOrWhiteSpace(detail)
                ? "Distributed ffprobe exited with code " + result.ExitCode + "."
                : "Distributed ffprobe exited with code " + result.ExitCode + ": " + detail;
        }

        private static string Truncate(string value, int maxLength)
        {
            return string.IsNullOrEmpty(value) || value.Length <= maxLength
                ? value
                : value.Substring(0, maxLength) + "…";
        }

        private static async Task<ProcessResult> RunProcessAsync(string executable, string arguments,
            int timeoutSeconds, CancellationToken cancellationToken)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true
            };

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, __) => completion.TrySetResult(true);
            if (!process.Start()) throw new InvalidOperationException("Unable to start distributed ffprobe: " + executable);

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var registration = timeout.Token.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch
                {
                    // best effort only
                }

                completion.TrySetCanceled();
            });

            if (process.HasExited) completion.TrySetResult(true);
            await completion.Task.ConfigureAwait(false);

            return new ProcessResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = await stdoutTask.ConfigureAwait(false),
                StandardError = await stderrTask.ConfigureAwait(false)
            };
        }

        private sealed class ProcessResult
        {
            public int ExitCode { get; set; }
            public string StandardOutput { get; set; }
            public string StandardError { get; set; }
        }

        private sealed class FfProbeDocument
        {
            public List<FfProbeStream> streams { get; set; }
            public List<FfProbeChapter> chapters { get; set; }
            public FfProbeFormat format { get; set; }
        }

        private sealed class FfProbeFormat
        {
            public string format_name { get; set; }
            public string duration { get; set; }
            public string size { get; set; }
            public string bit_rate { get; set; }
        }

        private sealed class FfProbeStream
        {
            public int index { get; set; }
            public string codec_name { get; set; }
            public string codec_type { get; set; }
            public string profile { get; set; }
            public int? width { get; set; }
            public int? height { get; set; }
            public string bit_rate { get; set; }
            public string sample_rate { get; set; }
            public int? channels { get; set; }
            public string channel_layout { get; set; }
            public string pix_fmt { get; set; }
            public string color_transfer { get; set; }
            public string color_primaries { get; set; }
            public string color_space { get; set; }
            public string avg_frame_rate { get; set; }
            public FfProbeDisposition disposition { get; set; }
            public FfProbeTags tags { get; set; }
        }

        private sealed class FfProbeDisposition
        {
            public int @default { get; set; }
            public int forced { get; set; }
            public int attached_pic { get; set; }
        }

        private sealed class FfProbeChapter
        {
            public string start_time { get; set; }
            public FfProbeTags tags { get; set; }
        }

        private sealed class FfProbeTags
        {
            public string language { get; set; }
            public string title { get; set; }
        }
    }
}
