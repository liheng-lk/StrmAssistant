using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using StrmAssistant.Options;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.MediaEnhance
{
    public sealed class CustomImageCapturePlan
    {
        public bool Valid { get; set; }
        public string Error { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string MediaKind { get; set; }
        public bool OpticalMedia { get; set; }
        public bool DistributedExecutable { get; set; }
        public string Executable { get; set; }
        public string InputPath { get; set; }
        public int? BluRayPlaylist { get; set; }
        public int PositionPercent { get; set; }
        public double SeekSeconds { get; set; }
        public bool HasExistingPrimaryImage { get; set; }
        public string CommandPreview { get; set; }
    }

    public sealed class CustomImageCaptureResult
    {
        public bool Success { get; set; }
        public bool SavedPrimaryImage { get; set; }
        public bool OutputFileVisibleLocally { get; set; }
        public string Error { get; set; }
        public string StandardError { get; set; }
        public string OutputPath { get; set; }
        public long OutputSize { get; set; }
        public CustomImageCapturePlan Plan { get; set; }
    }

    /// <summary>
    /// Explicit, admin-triggered single-frame capture. This does not patch Emby's encoder path.
    /// A configured custom/distributed ffmpeg is invoked only for this operation.
    /// </summary>
    public sealed class CustomImageCapture
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly IApplicationPaths _applicationPaths;

        public CustomImageCapture(ILibraryManager libraryManager, IProviderManager providerManager,
            IFileSystem fileSystem, IApplicationPaths applicationPaths)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _applicationPaths = applicationPaths;
        }

        public CustomImageCapturePlan BuildPlan(Video item, string inputPath, MediaInfoExtractOptions options,
            OpticalProbeResult opticalProbe = null, BluRayDiscEnrichmentSummary discInfo = null,
            int? requestedPositionPercent = null)
        {
            var plan = new CustomImageCapturePlan
            {
                ItemId = item?.InternalId.ToString(),
                ItemName = item?.Name,
                InputPath = inputPath,
                HasExistingPrimaryImage = item?.HasImage(ImageType.Primary) == true
            };

            if (item == null)
            {
                plan.Error = "Video item is null.";
                return plan;
            }

            if (options?.EnableCustomImageCapture != true)
            {
                plan.Error = "Custom/optical image capture is disabled in plugin options.";
                return plan;
            }

            if (string.IsNullOrWhiteSpace(inputPath))
            {
                plan.Error = "Capture input path is empty.";
                return plan;
            }

            var kind = OpticalMediaProbe.GetMediaKind(item);
            plan.MediaKind = kind.ToString();
            plan.OpticalMedia = kind != OpticalMediaKind.Unsupported;

            if (kind == OpticalMediaKind.DvdIso)
            {
                plan.Error = "DVD ISO image capture is not enabled in this phase.";
                return plan;
            }

            if (plan.OpticalMedia && options.EnableOpticalMediaProbe != true)
            {
                plan.Error = "Optical image capture requires ISO / BDMV media probing to be enabled first.";
                return plan;
            }

            plan.Executable = ResolveExecutable(options, out var distributed);
            plan.DistributedExecutable = distributed;
            if (string.IsNullOrWhiteSpace(plan.Executable))
            {
                plan.Error = "No ffmpeg executable is configured.";
                return plan;
            }

            var percent = requestedPositionPercent ?? options.ImageCapturePosition;
            percent = Math.Max(1, Math.Min(percent, 99));
            plan.PositionPercent = percent;

            var runtimeTicks = opticalProbe?.RunTimeTicks ?? item.RunTimeTicks;
            if (!runtimeTicks.HasValue || runtimeTicks.Value <= 0)
            {
                plan.Error = "Runtime is unknown. Probe/extract MediaInfo before calculating a safe capture position.";
                return plan;
            }

            plan.SeekSeconds = TimeSpan.FromTicks(runtimeTicks.Value).TotalSeconds * percent / 100d;
            if (plan.SeekSeconds < 0.1) plan.SeekSeconds = 0.1;

            if (plan.OpticalMedia)
            {
                plan.InputPath = BuildOpticalInput(item, inputPath);
                plan.BluRayPlaylist = ParsePlaylistNumber(discInfo?.PlaylistName);
            }

            plan.CommandPreview = BuildArguments(plan, "<temporary-output.jpg>");
            plan.Valid = true;
            return plan;
        }

        public async Task<CustomImageCaptureResult> CaptureAndSaveAsync(Video item, string inputPath,
            MediaInfoExtractOptions options, bool replaceExistingPrimaryImage, int? requestedPositionPercent,
            OpticalProbeResult opticalProbe, BluRayDiscEnrichmentSummary discInfo,
            CancellationToken cancellationToken)
        {
            var plan = BuildPlan(item, inputPath, options, opticalProbe, discInfo, requestedPositionPercent);
            var result = new CustomImageCaptureResult { Plan = plan };
            if (!plan.Valid)
            {
                result.Error = plan.Error;
                return result;
            }

            if (plan.HasExistingPrimaryImage && !replaceExistingPrimaryImage)
            {
                result.Error = "The item already has a primary image. Set ReplaceExistingPrimaryImage=true explicitly to overwrite it.";
                return result;
            }

            var tempDirectory = _applicationPaths.TempDirectory;
            if (string.IsNullOrWhiteSpace(tempDirectory)) tempDirectory = Path.GetTempPath();
            Directory.CreateDirectory(tempDirectory);
            var outputPath = Path.Combine(tempDirectory, "strmassistant-capture-" + Guid.NewGuid().ToString("N") + ".jpg");
            result.OutputPath = outputPath;

            try
            {
                var arguments = BuildArguments(plan, outputPath);
                var process = await RunProcessAsync(plan.Executable, arguments,
                        Math.Max(10, Math.Min(options.ImageCaptureTimeoutSeconds, 600)), cancellationToken)
                    .ConfigureAwait(false);

                result.StandardError = Truncate(process.StandardError, 12000);
                if (process.ExitCode != 0)
                {
                    result.Error = BuildProcessError(process);
                    return result;
                }

                var file = _fileSystem.GetFileInfo(outputPath);
                result.OutputFileVisibleLocally = file?.Exists == true;
                result.OutputSize = file?.Length ?? 0;
                if (!result.OutputFileVisibleLocally || result.OutputSize <= 0)
                {
                    result.Error = plan.DistributedExecutable
                        ? "ffmpeg reported success but the generated image is not visible on the Emby host. The remote wrapper must return/synchronize output files before distributed image capture can be used."
                        : "ffmpeg reported success but no usable image file was generated.";
                    return result;
                }

                var libraryOptions = _libraryManager.GetLibraryOptions(item);
                var directoryService = new DirectoryService(Plugin.Instance.Logger, _fileSystem);
                await _providerManager.SaveImage(item, libraryOptions, outputPath, ImageType.Primary,
                        null, Array.Empty<long>(), directoryService, false, cancellationToken)
                    .ConfigureAwait(false);

                result.Success = true;
                result.SavedPrimaryImage = true;
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Error = cancellationToken.IsCancellationRequested
                    ? "Image capture was cancelled."
                    : "Image capture timed out.";
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "Image capture failed: " + ex.Message;
                return result;
            }
            finally
            {
                try
                {
                    if (_fileSystem.FileExists(outputPath)) _fileSystem.DeleteFile(outputPath);
                }
                catch
                {
                    // Temporary-file cleanup is best effort only.
                }
            }
        }

        private static string ResolveExecutable(MediaInfoExtractOptions options, out bool distributed)
        {
            distributed = options?.EnableDistributedImageCapture == true &&
                          !string.IsNullOrWhiteSpace(options.DistributedFfmpegExecutablePath);
            if (distributed) return options.DistributedFfmpegExecutablePath.Trim().Trim('"');

            return string.IsNullOrWhiteSpace(options?.ImageCaptureFfmpegExecutablePath)
                ? "ffmpeg"
                : options.ImageCaptureFfmpegExecutablePath.Trim().Trim('"');
        }

        private static string BuildOpticalInput(Video item, string resolvedPath)
        {
            var kind = OpticalMediaProbe.GetMediaKind(item);
            if (kind == OpticalMediaKind.BluRayDirectory)
            {
                var trimmed = resolvedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(Path.GetFileName(trimmed), "BDMV", StringComparison.OrdinalIgnoreCase))
                    trimmed = Path.GetDirectoryName(trimmed);
                return "bluray:" + trimmed;
            }

            return kind == OpticalMediaKind.BluRayIso ? "bluray:" + resolvedPath : resolvedPath;
        }

        private static int? ParsePlaylistNumber(string playlistName)
        {
            if (string.IsNullOrWhiteSpace(playlistName)) return null;
            var fileName = Path.GetFileNameWithoutExtension(playlistName.Trim());
            return int.TryParse(fileName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : (int?)null;
        }

        private static string BuildArguments(CustomImageCapturePlan plan, string outputPath)
        {
            var builder = new StringBuilder();
            builder.Append("-hide_banner -loglevel error ");
            if (plan.BluRayPlaylist.HasValue)
                builder.Append("-playlist ").Append(plan.BluRayPlaylist.Value).Append(' ');
            builder.Append("-i ").Append(QuoteArgument(plan.InputPath)).Append(' ');
            builder.Append("-ss ").Append(plan.SeekSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ');
            builder.Append("-map 0:v:0 -an -sn -dn -frames:v 1 -q:v 2 -y ");
            builder.Append(QuoteArgument(outputPath));
            return builder.ToString();
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
                ? "ffmpeg exited with code " + result.ExitCode + "."
                : "ffmpeg exited with code " + result.ExitCode + ": " + detail;
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
