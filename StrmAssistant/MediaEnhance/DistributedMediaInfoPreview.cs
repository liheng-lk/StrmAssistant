using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.MediaEnhance
{
    public sealed class DistributedPreviewStream
    {
        public int Index { get; set; }
        public string Type { get; set; }
        public string Codec { get; set; }
        public string Profile { get; set; }
        public string Language { get; set; }
        public string Title { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int? Channels { get; set; }
        public int? BitRate { get; set; }
        public bool AttachedPicture { get; set; }
    }

    public sealed class DistributedMediaInfoPreviewResult
    {
        public bool Success { get; set; }
        public bool PathAccessConfirmed { get; set; }
        public bool UsedRffmpegBackend { get; set; }
        public string Error { get; set; }
        public string Executable { get; set; }
        public string InputPath { get; set; }
        public string FormatName { get; set; }
        public long? Size { get; set; }
        public long? RunTimeTicks { get; set; }
        public int? BitRate { get; set; }
        public int ChapterCount { get; set; }
        public string StandardError { get; set; }
        public List<DistributedPreviewStream> Streams { get; set; } = new List<DistributedPreviewStream>();
    }

    /// <summary>
    /// Executes the configured distributed ffprobe against one concrete media path without
    /// writing any Emby state. A successful response proves that the wrapper/worker can open
    /// the exact path that would be sent by distributed routing.
    /// </summary>
    public sealed class DistributedMediaInfoPreview
    {
        private readonly IJsonSerializer _jsonSerializer;

        public DistributedMediaInfoPreview(IJsonSerializer jsonSerializer)
        {
            _jsonSerializer = jsonSerializer;
        }

        public async Task<DistributedMediaInfoPreviewResult> ProbeAsync(BaseItem item, string inputPath,
            MediaInfoExtractOptions options, CancellationToken cancellationToken)
        {
            var result = new DistributedMediaInfoPreviewResult { InputPath = inputPath };
            if (item == null)
            {
                result.Error = "Item is null.";
                return result;
            }

            if (options == null || string.IsNullOrWhiteSpace(options.DistributedFfprobeExecutablePath))
            {
                result.Error = "Distributed ffprobe path is empty.";
                return result;
            }

            if (string.IsNullOrWhiteSpace(inputPath))
            {
                result.Error = "Input media path is empty.";
                return result;
            }

            if (item is Video video && OpticalMediaProbe.GetMediaKind(video) != OpticalMediaKind.Unsupported)
            {
                result.Error = "Optical media must be tested through /StrmAssistant/OpticalProbe instead.";
                return result;
            }

            result.Executable = options.DistributedFfprobeExecutablePath.Trim().Trim('"');
            var arguments = string.Join(" ", new[]
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
                process = await RunProcessAsync(result.Executable, arguments,
                        Math.Max(30, Math.Min(options.DistributedExtractTimeoutSeconds, 3600)), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result.Error = cancellationToken.IsCancellationRequested
                    ? "Distributed preview was cancelled."
                    : "Distributed preview timed out.";
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
                result.Error = BuildError(process);
                return result;
            }

            if (string.IsNullOrWhiteSpace(process.StandardOutput))
            {
                result.Error = "Distributed ffprobe returned no JSON output.";
                return result;
            }

            try
            {
                var document = _jsonSerializer.DeserializeFromString<FfProbeDocument>(process.StandardOutput);
                if (document == null)
                {
                    result.Error = "Distributed ffprobe JSON could not be deserialized.";
                    return result;
                }

                result.FormatName = document.format?.format_name;
                result.Size = ParseLong(document.format?.size);
                result.BitRate = ParseInt(document.format?.bit_rate);
                var duration = ParseDouble(document.format?.duration);
                result.RunTimeTicks = duration.HasValue && duration.Value >= 0
                    ? SecondsToTicks(duration.Value)
                    : (long?)null;
                result.ChapterCount = document.chapters?.Count ?? 0;
                result.Streams = (document.streams ?? new List<FfProbeStream>())
                    .Select(stream => new DistributedPreviewStream
                    {
                        Index = stream.index,
                        Type = stream.codec_type,
                        Codec = stream.codec_name,
                        Profile = stream.profile,
                        Language = stream.tags?.language,
                        Title = stream.tags?.title,
                        Width = stream.width,
                        Height = stream.height,
                        Channels = stream.channels,
                        BitRate = ParseInt(stream.bit_rate),
                        AttachedPicture = stream.disposition?.attached_pic == 1
                    })
                    .ToList();

                result.PathAccessConfirmed = true;
                result.Success = result.Streams.Any(stream =>
                                     string.Equals(stream.Type, "video", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(stream.Type, "audio", StringComparison.OrdinalIgnoreCase)) ||
                                 result.RunTimeTicks.HasValue;

                if (!result.Success)
                    result.Error = "The path opened successfully but ffprobe returned no usable video/audio stream or duration.";
            }
            catch (Exception ex)
            {
                result.Error = "Distributed preview JSON parse failed: " + ex.Message;
            }

            return result;
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

        private static long SecondsToTicks(double seconds)
        {
            var ticks = seconds * TimeSpan.TicksPerSecond;
            return ticks > long.MaxValue ? long.MaxValue : ticks < 0 ? 0 : Convert.ToInt64(ticks);
        }

        private static bool LooksLikeRffmpeg(string stdout, string stderr)
        {
            var value = (stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty);
            return Contains(value, "starting rffmpeg") || Contains(value, "database in use:") ||
                   Contains(value, "running command host=") || Contains(value, "finished rffmpeg");
        }

        private static bool Contains(string value, string needle)
        {
            return !string.IsNullOrEmpty(value) && value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string BuildError(ProcessResult result)
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
            public List<object> chapters { get; set; }
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
            public int? channels { get; set; }
            public FfProbeDisposition disposition { get; set; }
            public FfProbeTags tags { get; set; }
        }

        private sealed class FfProbeDisposition
        {
            public int attached_pic { get; set; }
        }

        private sealed class FfProbeTags
        {
            public string language { get; set; }
            public string title { get; set; }
        }
    }
}
