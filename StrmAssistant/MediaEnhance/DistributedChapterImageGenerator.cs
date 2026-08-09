using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.MediaEnhance
{
    public sealed class DistributedChapterImageResult
    {
        public bool Attempted { get; set; }
        public bool Success { get; set; }
        public bool FellBackToNative { get; set; }
        public bool OpticalMedia { get; set; }
        public bool NativeFallbackAvailable { get; set; } = true;
        public int ExistingCount { get; set; }
        public int GeneratedCount { get; set; }
        public int FailedCount { get; set; }
        public string Executable { get; set; }
        public string InputPath { get; set; }
        public int? BluRayPlaylist { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Pre-generates Emby chapter JPEGs with a custom/distributed ffmpeg executable.
    /// The caller still invokes Emby's native ThumbnailGenerator afterwards so Emby
    /// remains responsible for chapter persistence, stale-image cleanup and any supported
    /// BIF/thumbnail-set bookkeeping. Blu-ray ISO/BDMV can use the same path when optical
    /// probing is explicitly enabled; DVD ISO remains unsupported.
    /// </summary>
    public sealed class DistributedChapterImageGenerator
    {
        private static readonly long FirstChapterTicks = TimeSpan.FromSeconds(15).Ticks;

        private readonly IFileSystem _fileSystem;
        private readonly IItemRepository _itemRepository;

        public DistributedChapterImageGenerator(IFileSystem fileSystem, IItemRepository itemRepository)
        {
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
        }

        public async Task<DistributedChapterImageResult> GenerateMissingAsync(Video item,
            IList<ChapterInfo> chapters, MediaInfoExtractOptions options, CancellationToken cancellationToken)
        {
            var result = new DistributedChapterImageResult();
            if (item == null || chapters == null || chapters.Count == 0) return result;
            if (options?.EnableDistributedChapterImageRouting != true) return result;

            result.Attempted = true;
            result.Executable = ResolveExecutable(options);

            if (string.IsNullOrWhiteSpace(result.Executable))
            {
                result.Error = "No distributed/custom ffmpeg executable is configured.";
                return result;
            }

            if (item.IsShortcut)
            {
                result.Error = "STRM chapter-image routing is disabled. Keep this task on Emby's native path until worker path parity is explicitly implemented.";
                return result;
            }

            if (!item.IsFileProtocol || string.IsNullOrWhiteSpace(item.Path))
            {
                result.Error = "Distributed chapter-image routing currently requires a file-protocol media path.";
                return result;
            }

            var inputPlan = BuildInputPlan(item, options);
            result.OpticalMedia = inputPlan.OpticalMedia;
            result.NativeFallbackAvailable = !inputPlan.OpticalMedia;
            result.InputPath = inputPlan.InputPath;
            result.BluRayPlaylist = inputPlan.BluRayPlaylist;
            if (!inputPlan.Valid)
            {
                result.Error = inputPlan.Error;
                return result;
            }

            var runtimeTicks = item.RunTimeTicks ?? 0;
            if (runtimeTicks <= 0)
            {
                result.Error = "Runtime is unknown; MediaInfo must be available before chapter images are generated.";
                return result;
            }

            var chapterDirectory = Path.Combine(item.GetInternalMetadataPath(), "chapters");
            _fileSystem.CreateDirectory(chapterDirectory);

            var failedMessages = new List<string>();
            var generatedAny = false;

            foreach (var chapter in chapters.OrderBy(c => c.StartPositionTicks))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (chapter.StartPositionTicks < 0 || chapter.StartPositionTicks >= runtimeTicks) continue;

                var outputPath = GetChapterImagePath(item, chapter.StartPositionTicks, chapterDirectory);
                if (_fileSystem.FileExists(outputPath))
                {
                    result.ExistingCount++;
                    chapter.ImagePath = outputPath;
                    chapter.ImageDateModified = _fileSystem.GetLastWriteTimeUtc(outputPath);
                    continue;
                }

                var captureTicks = chapter.StartPositionTicks == 0
                    ? Math.Min(FirstChapterTicks, runtimeTicks)
                    : chapter.StartPositionTicks;
                var seekSeconds = TimeSpan.FromTicks(captureTicks).TotalSeconds;

                try
                {
                    var processResult = await RunProcessAsync(result.Executable,
                            BuildArguments(inputPlan, seekSeconds, outputPath),
                            Math.Max(10, Math.Min(options.DistributedChapterImageTimeoutSeconds, 600)),
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (processResult.ExitCode != 0)
                    {
                        result.FailedCount++;
                        failedMessages.Add(BuildProcessError(chapter.StartPositionTicks, processResult));
                        if (!options.DistributedChapterImageFallbackToEmby || inputPlan.OpticalMedia) break;
                        continue;
                    }

                    var file = _fileSystem.GetFileInfo(outputPath);
                    if (file?.Exists != true || file.Length <= 0)
                    {
                        result.FailedCount++;
                        failedMessages.Add("ffmpeg reported success but the chapter image is not visible on the Emby host: " + outputPath);
                        if (!options.DistributedChapterImageFallbackToEmby || inputPlan.OpticalMedia) break;
                        continue;
                    }

                    chapter.ImagePath = outputPath;
                    chapter.ImageDateModified = _fileSystem.GetLastWriteTimeUtc(outputPath);
                    result.GeneratedCount++;
                    generatedAny = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.FailedCount++;
                    failedMessages.Add("Chapter " + chapter.StartPositionTicks + " failed: " + ex.Message);
                    if (!options.DistributedChapterImageFallbackToEmby || inputPlan.OpticalMedia) break;
                }
            }

            // Persisting here makes generated images immediately visible to Emby even for
            // optical media that native ThumbnailGenerator may consider ineligible for extraction.
            if (generatedAny)
            {
                _itemRepository.SaveChapters(item.InternalId, chapters.ToList());
            }

            result.FellBackToNative = result.FailedCount > 0 &&
                                      options.DistributedChapterImageFallbackToEmby &&
                                      result.NativeFallbackAvailable;
            result.Success = result.FailedCount == 0 || result.FellBackToNative;
            result.Error = failedMessages.Count == 0 ? null : string.Join(" | ", failedMessages.Take(3));
            return result;
        }

        private static ChapterInputPlan BuildInputPlan(Video item, MediaInfoExtractOptions options)
        {
            var kind = OpticalMediaProbe.GetMediaKind(item);
            var plan = new ChapterInputPlan
            {
                Valid = true,
                InputPath = item.Path,
                Kind = kind
            };

            if (kind == OpticalMediaKind.Unsupported) return plan;

            plan.OpticalMedia = true;
            if (options?.EnableOpticalMediaProbe != true)
            {
                plan.Valid = false;
                plan.Error = "Optical chapter-image generation requires ISO / BDMV media probing to be enabled.";
                return plan;
            }

            switch (kind)
            {
                case OpticalMediaKind.BluRayDirectory:
                    plan.InputPath = "bluray:" + ResolveBluRayDiscRoot(item.Path);
                    plan.BluRayPlaylist = TryResolveBluRayPlaylist(item);
                    break;
                case OpticalMediaKind.BluRayIso:
                    plan.InputPath = "bluray:" + item.Path;
                    break;
                case OpticalMediaKind.GenericIso:
                    // Generic ISO is passed directly to ffmpeg. Runtime support depends on
                    // the ffmpeg build and the image's filesystem/container layout.
                    plan.InputPath = item.Path;
                    break;
                case OpticalMediaKind.DvdIso:
                    plan.Valid = false;
                    plan.Error = "DVD ISO chapter-image generation is not implemented; no compatible DVD input contract has been verified.";
                    break;
            }

            return plan;
        }

        private static int? TryResolveBluRayPlaylist(Video item)
        {
            try
            {
                if (Plugin.Instance?.ApplicationHost == null) return null;
                var enricher = new BluRayDiscInfoEnricher(Plugin.Instance.ApplicationHost);
                var probe = new OpticalProbeResult { Success = true };
                var summary = enricher.Enrich(item, probe);
                return ParsePlaylistNumber(summary?.PlaylistName);
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Debug("DistributedChapterImage - Blu-ray playlist detection failed: " + ex.Message);
                return null;
            }
        }

        private static string ResolveBluRayDiscRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(Path.GetFileName(trimmed), "BDMV", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(trimmed)
                : trimmed;
        }

        private static int? ParsePlaylistNumber(string playlistName)
        {
            if (string.IsNullOrWhiteSpace(playlistName)) return null;
            var fileName = Path.GetFileNameWithoutExtension(playlistName.Trim());
            return int.TryParse(fileName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : (int?)null;
        }

        private static string ResolveExecutable(MediaInfoExtractOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options?.DistributedFfmpegExecutablePath))
                return options.DistributedFfmpegExecutablePath.Trim().Trim('"');

            if (!string.IsNullOrWhiteSpace(options?.ImageCaptureFfmpegExecutablePath))
                return options.ImageCaptureFfmpegExecutablePath.Trim().Trim('"');

            return "ffmpeg";
        }

        private static string GetChapterImagePath(Video item, long chapterPositionTicks, string chapterDirectory)
        {
            var filename = item.DateModified.Ticks.ToString(CultureInfo.InvariantCulture) + "_" +
                           chapterPositionTicks.ToString(CultureInfo.InvariantCulture) + ".jpg";
            return Path.Combine(chapterDirectory, filename);
        }

        private static string BuildArguments(ChapterInputPlan inputPlan, double seekSeconds, string outputPath)
        {
            var builder = new StringBuilder();
            builder.Append("-hide_banner -loglevel error ");

            if (inputPlan.BluRayPlaylist.HasValue)
                builder.Append("-playlist ").Append(inputPlan.BluRayPlaylist.Value).Append(' ');

            if (inputPlan.OpticalMedia)
            {
                builder.Append("-i ").Append(QuoteArgument(inputPlan.InputPath)).Append(' ');
                builder.Append("-ss ").Append(seekSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ');
            }
            else
            {
                builder.Append("-ss ").Append(seekSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ');
                builder.Append("-i ").Append(QuoteArgument(inputPlan.InputPath)).Append(' ');
            }

            builder.Append("-map 0:v:0 -an -sn -dn -frames:v 1 -q:v 2 -y ");
            builder.Append(QuoteArgument(outputPath));
            return builder.ToString();
        }

        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string BuildProcessError(long chapterTicks, ProcessResult result)
        {
            var detail = ((result.StandardError ?? string.Empty) + "\n" + (result.StandardOutput ?? string.Empty))
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
            return string.IsNullOrWhiteSpace(detail)
                ? "Chapter " + chapterTicks + ": ffmpeg exited with code " + result.ExitCode + "."
                : "Chapter " + chapterTicks + ": " + detail;
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
            if (!process.Start()) throw new InvalidOperationException("Unable to start ffmpeg: " + executable);

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

        private sealed class ChapterInputPlan
        {
            public bool Valid { get; set; }
            public bool OpticalMedia { get; set; }
            public OpticalMediaKind Kind { get; set; }
            public string InputPath { get; set; }
            public int? BluRayPlaylist { get; set; }
            public string Error { get; set; }
        }

        private sealed class ProcessResult
        {
            public int ExitCode { get; set; }
            public string StandardOutput { get; set; }
            public string StandardError { get; set; }
        }
    }
}
