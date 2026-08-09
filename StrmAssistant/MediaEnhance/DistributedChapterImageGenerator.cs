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
        public int ExistingCount { get; set; }
        public int GeneratedCount { get; set; }
        public int FailedCount { get; set; }
        public string Executable { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Pre-generates Emby chapter JPEGs with a custom/distributed ffmpeg executable.
    /// The caller still invokes Emby's native ThumbnailGenerator afterwards so Emby
    /// remains responsible for chapter persistence, stale-image cleanup and BIF/thumbnail-set bookkeeping.
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

            if (OpticalMediaProbe.GetMediaKind(item) != OpticalMediaKind.Unsupported)
            {
                result.Error = "ISO/BDMV chapter-image generation remains on the dedicated optical pipeline in this phase.";
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
                            BuildArguments(item.Path, seekSeconds, outputPath),
                            Math.Max(10, Math.Min(options.DistributedChapterImageTimeoutSeconds, 600)),
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (processResult.ExitCode != 0)
                    {
                        result.FailedCount++;
                        failedMessages.Add(BuildProcessError(chapter.StartPositionTicks, processResult));
                        if (!options.DistributedChapterImageFallbackToEmby) break;
                        continue;
                    }

                    var file = _fileSystem.GetFileInfo(outputPath);
                    if (file?.Exists != true || file.Length <= 0)
                    {
                        result.FailedCount++;
                        failedMessages.Add("ffmpeg reported success but the chapter image is not visible on the Emby host: " + outputPath);
                        if (!options.DistributedChapterImageFallbackToEmby) break;
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
                    if (!options.DistributedChapterImageFallbackToEmby) break;
                }
            }

            // Persisting here makes the generated images immediately visible to Emby.
            // Native ThumbnailGenerator still runs afterwards and remains the final authority.
            if (generatedAny)
            {
                _itemRepository.SaveChapters(item.InternalId, chapters.ToList());
            }

            result.FellBackToNative = result.FailedCount > 0 && options.DistributedChapterImageFallbackToEmby;
            result.Success = result.FailedCount == 0 || result.FellBackToNative;
            result.Error = failedMessages.Count == 0 ? null : string.Join(" | ", failedMessages.Take(3));
            return result;
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

        private static string BuildArguments(string inputPath, double seekSeconds, string outputPath)
        {
            var builder = new StringBuilder();
            builder.Append("-hide_banner -loglevel error ");
            builder.Append("-ss ").Append(seekSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ');
            builder.Append("-i ").Append(QuoteArgument(inputPath)).Append(' ');
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

        private sealed class ProcessResult
        {
            public int ExitCode { get; set; }
            public string StandardOutput { get; set; }
            public string StandardError { get; set; }
        }
    }
}
