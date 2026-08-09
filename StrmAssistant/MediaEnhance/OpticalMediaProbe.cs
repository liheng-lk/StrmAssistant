using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
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
    public enum OpticalMediaKind
    {
        Unsupported,
        BluRayDirectory,
        BluRayIso,
        GenericIso,
        DvdIso
    }

    public sealed class OpticalProbeHealthResult
    {
        public bool Success { get; set; }
        public string Executable { get; set; }
        public string Version { get; set; }
        public bool HasBlurayProtocol { get; set; }
        public string Error { get; set; }
    }

    public sealed class OpticalProbeStreamInfo
    {
        public int Index { get; set; }
        public string Type { get; set; }
        public string Codec { get; set; }
        public string Profile { get; set; }
        public string Language { get; set; }
        public string Title { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int? BitRate { get; set; }
        public int? Channels { get; set; }
        public int? SampleRate { get; set; }
        public string ChannelLayout { get; set; }
        public string PixelFormat { get; set; }
        public string ColorTransfer { get; set; }
        public string ColorPrimaries { get; set; }
        public string ColorSpace { get; set; }
        public float? AverageFrameRate { get; set; }
        public bool IsDefault { get; set; }
        public bool IsForced { get; set; }
    }

    public sealed class OpticalProbeChapterInfo
    {
        public int Index { get; set; }
        public double? StartSeconds { get; set; }
        public double? EndSeconds { get; set; }
        public string Title { get; set; }
    }

    public sealed class OpticalProbeResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string Executable { get; set; }
        public string Kind { get; set; }
        public string SourcePath { get; set; }
        public string ProbeInput { get; set; }
        public string FormatName { get; set; }
        public long? RunTimeTicks { get; set; }
        public int? BitRate { get; set; }
        public string StandardError { get; set; }
        public List<OpticalProbeStreamInfo> Streams { get; set; } = new List<OpticalProbeStreamInfo>();
        public List<OpticalProbeChapterInfo> Chapters { get; set; } = new List<OpticalProbeChapterInfo>();
    }

    /// <summary>
    /// Standalone optical-media probe. This is intentionally read-only: it never writes MediaStreams,
    /// chapters or item metadata. The first runtime gate is proving that the selected ffprobe can open
    /// the requested ISO/BDMV input and return stable JSON on the real Emby host.
    /// </summary>
    public sealed class OpticalMediaProbe
    {
        private readonly IJsonSerializer _jsonSerializer;

        public OpticalMediaProbe(IJsonSerializer jsonSerializer)
        {
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
        }

        public static OpticalMediaKind GetMediaKind(Video item)
        {
            if (item == null) return OpticalMediaKind.Unsupported;

            if (item.VideoType == VideoType.BluRay)
                return OpticalMediaKind.BluRayDirectory;

            if (item.VideoType == VideoType.Iso)
            {
                if (item.IsoType == IsoType.BluRay) return OpticalMediaKind.BluRayIso;
                if (item.IsoType == IsoType.Dvd) return OpticalMediaKind.DvdIso;
                return OpticalMediaKind.GenericIso;
            }

            var path = item.Path;
            if (string.IsNullOrWhiteSpace(path)) return OpticalMediaKind.Unsupported;

            var extension = Path.GetExtension(path);
            if (string.Equals(extension, ".iso", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".img", StringComparison.OrdinalIgnoreCase))
                return OpticalMediaKind.GenericIso;

            if (Directory.Exists(path) &&
                (Directory.Exists(Path.Combine(path, "BDMV")) ||
                 string.Equals(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                     "BDMV", StringComparison.OrdinalIgnoreCase)))
                return OpticalMediaKind.BluRayDirectory;

            return OpticalMediaKind.Unsupported;
        }

        public async Task<OpticalProbeHealthResult> CheckHealthAsync(MediaInfoExtractOptions options,
            CancellationToken cancellationToken)
        {
            var executable = ResolveExecutable(options);
            var timeoutSeconds = ResolveTimeoutSeconds(options);

            try
            {
                var version = await RunProcessAsync(executable, "-version", timeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
                if (version.ExitCode != 0)
                {
                    return new OpticalProbeHealthResult
                    {
                        Success = false,
                        Executable = executable,
                        Error = BuildProcessError(version)
                    };
                }

                var protocols = await RunProcessAsync(executable, "-v error -protocols", timeoutSeconds,
                        cancellationToken)
                    .ConfigureAwait(false);

                var versionLine = FirstNonEmptyLine(version.StandardOutput) ?? FirstNonEmptyLine(version.StandardError);
                var protocolLines = (protocols.StandardOutput ?? string.Empty)
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim());

                return new OpticalProbeHealthResult
                {
                    Success = protocols.ExitCode == 0,
                    Executable = executable,
                    Version = versionLine,
                    HasBlurayProtocol = protocolLines.Any(line =>
                        string.Equals(line, "bluray", StringComparison.OrdinalIgnoreCase)),
                    Error = protocols.ExitCode == 0 ? null : BuildProcessError(protocols)
                };
            }
            catch (Exception ex)
            {
                return new OpticalProbeHealthResult
                {
                    Success = false,
                    Executable = executable,
                    Error = ex.Message
                };
            }
        }

        public async Task<OpticalProbeResult> ProbeAsync(Video item, MediaInfoExtractOptions options,
            CancellationToken cancellationToken)
        {
            var result = new OpticalProbeResult
            {
                SourcePath = item?.Path,
                Executable = ResolveExecutable(options)
            };

            if (item == null)
            {
                result.Error = "Item is null.";
                return result;
            }

            if (options == null || !options.EnableOpticalMediaProbe)
            {
                result.Error = "ISO / BDMV optical probing is disabled in plugin options.";
                return result;
            }

            var kind = GetMediaKind(item);
            result.Kind = kind.ToString();
            if (kind == OpticalMediaKind.Unsupported)
            {
                result.Error = "The selected item is not an ISO/IMG or Blu-ray directory item.";
                return result;
            }

            if (kind == OpticalMediaKind.DvdIso)
            {
                result.Error = "DVD ISO write/probe integration is not enabled in this phase; Blu-ray ISO/BDMV is the current target.";
                return result;
            }

            var sourcePath = ResolveDiscRoot(item, kind);
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                result.Error = "Unable to resolve an optical-media source path.";
                return result;
            }

            if (kind == OpticalMediaKind.BluRayDirectory && !Directory.Exists(sourcePath))
            {
                result.Error = "Blu-ray directory does not exist: " + sourcePath;
                return result;
            }

            if ((kind == OpticalMediaKind.BluRayIso || kind == OpticalMediaKind.GenericIso) &&
                item.IsFileProtocol && !File.Exists(sourcePath))
            {
                result.Error = "ISO/IMG file does not exist: " + sourcePath;
                return result;
            }

            // FFmpeg's libbluray protocol accepts a mounted Blu-ray directory and, when the linked
            // libbluray/libudfread build supports it, a UDF Blu-ray image as well. Only known Blu-ray
            // items are forced through bluray:. Unknown ISO images are probed as ordinary files first.
            var probeInput = kind == OpticalMediaKind.BluRayDirectory || kind == OpticalMediaKind.BluRayIso
                ? "bluray:" + sourcePath
                : sourcePath;

            result.ProbeInput = probeInput;

            var args = string.Join(" ", new[]
            {
                "-v error",
                "-hide_banner",
                "-print_format json",
                "-show_format",
                "-show_streams",
                "-show_chapters",
                QuoteArgument(probeInput)
            });

            ProcessResult processResult;
            try
            {
                processResult = await RunProcessAsync(result.Executable, args, ResolveTimeoutSeconds(options),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result.Error = cancellationToken.IsCancellationRequested
                    ? "Optical-media probe was cancelled."
                    : "Optical-media probe timed out.";
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }

            result.StandardError = Truncate(processResult.StandardError, 8192);
            if (processResult.ExitCode != 0)
            {
                result.Error = BuildProcessError(processResult);
                return result;
            }

            if (string.IsNullOrWhiteSpace(processResult.StandardOutput))
            {
                result.Error = "ffprobe returned no JSON output.";
                return result;
            }

            try
            {
                var document = _jsonSerializer.DeserializeFromString<FfProbeDocument>(processResult.StandardOutput);
                if (document == null)
                {
                    result.Error = "Unable to deserialize ffprobe JSON output.";
                    return result;
                }

                result.FormatName = document.format?.format_name;
                result.RunTimeTicks = SecondsToTicks(ParseDouble(document.format?.duration));
                result.BitRate = ParseInt(document.format?.bit_rate);
                result.Streams = (document.streams ?? new List<FfProbeStream>())
                    .Select(ToStreamInfo)
                    .ToList();
                result.Chapters = (document.chapters ?? new List<FfProbeChapter>())
                    .Select(ToChapterInfo)
                    .ToList();
                result.Success = result.Streams.Count > 0 || result.Chapters.Count > 0 || result.RunTimeTicks.HasValue;

                if (!result.Success)
                    result.Error = "ffprobe JSON was valid but contained no usable streams, chapters or duration.";
            }
            catch (Exception ex)
            {
                result.Error = "ffprobe JSON parse failed: " + ex.Message;
            }

            return result;
        }

        private static string ResolveExecutable(MediaInfoExtractOptions options)
        {
            var configured = options?.OpticalProbeExecutablePath?.Trim();
            return string.IsNullOrWhiteSpace(configured) ? "ffprobe" : configured.Trim('"');
        }

        private static int ResolveTimeoutSeconds(MediaInfoExtractOptions options)
        {
            var configured = options?.OpticalProbeTimeoutSeconds ?? 120;
            return Math.Max(10, Math.Min(configured, 600));
        }

        private static string ResolveDiscRoot(Video item, OpticalMediaKind kind)
        {
            var path = item.Path;
            if (string.IsNullOrWhiteSpace(path)) return null;

            if (kind != OpticalMediaKind.BluRayDirectory) return path;

            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(Path.GetFileName(trimmed), "BDMV", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(trimmed);

            return trimmed;
        }

        private static OpticalProbeStreamInfo ToStreamInfo(FfProbeStream stream)
        {
            return new OpticalProbeStreamInfo
            {
                Index = stream.index,
                Type = stream.codec_type,
                Codec = stream.codec_name,
                Profile = stream.profile,
                Language = stream.tags?.language,
                Title = stream.tags?.title,
                Width = stream.width,
                Height = stream.height,
                BitRate = ParseInt(stream.bit_rate),
                Channels = stream.channels,
                SampleRate = ParseInt(stream.sample_rate),
                ChannelLayout = stream.channel_layout,
                PixelFormat = stream.pix_fmt,
                ColorTransfer = stream.color_transfer,
                ColorPrimaries = stream.color_primaries,
                ColorSpace = stream.color_space,
                AverageFrameRate = ParseFrameRate(stream.avg_frame_rate),
                IsDefault = stream.disposition?.@default == 1,
                IsForced = stream.disposition?.forced == 1
            };
        }

        private static OpticalProbeChapterInfo ToChapterInfo(FfProbeChapter chapter)
        {
            return new OpticalProbeChapterInfo
            {
                Index = chapter.id,
                StartSeconds = ParseDouble(chapter.start_time),
                EndSeconds = ParseDouble(chapter.end_time),
                Title = chapter.tags?.title
            };
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

            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var direct))
                return direct;

            return null;
        }

        private static int? ParseInt(string value)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
                return result;

            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longResult))
                return longResult > int.MaxValue ? int.MaxValue : longResult < int.MinValue ? int.MinValue : (int)longResult;

            return null;
        }

        private static double? ParseDouble(string value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
                ? result
                : (double?)null;
        }

        private static long? SecondsToTicks(double? seconds)
        {
            if (!seconds.HasValue || seconds.Value < 0 || double.IsNaN(seconds.Value) || double.IsInfinity(seconds.Value))
                return null;

            var ticks = seconds.Value * TimeSpan.TicksPerSecond;
            if (ticks > long.MaxValue) return long.MaxValue;
            return Convert.ToInt64(ticks);
        }

        private static string FirstNonEmptyLine(string text)
        {
            return (text ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        }

        private static string BuildProcessError(ProcessResult result)
        {
            var detail = FirstNonEmptyLine(result.StandardError) ?? FirstNonEmptyLine(result.StandardOutput);
            return string.IsNullOrWhiteSpace(detail)
                ? "ffprobe exited with code " + result.ExitCode + "."
                : "ffprobe exited with code " + result.ExitCode + ": " + detail;
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            return value.Substring(0, maxLength) + "…";
        }

        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            if (value.All(ch => !char.IsWhiteSpace(ch) && ch != '"')) return value;
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
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

            var exitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, __) => exitTcs.TrySetResult(true);

            if (!process.Start())
                throw new InvalidOperationException("Unable to start ffprobe process: " + executable);

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var registration = timeoutCts.Token.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch
                {
                    // best effort cancellation only
                }

                exitTcs.TrySetCanceled(timeoutCts.Token);
            });

            if (process.HasExited) exitTcs.TrySetResult(true);

            await exitTcs.Task.ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            return new ProcessResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = stdout,
                StandardError = stderr
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
        }

        private sealed class FfProbeChapter
        {
            public int id { get; set; }
            public string start_time { get; set; }
            public string end_time { get; set; }
            public FfProbeTags tags { get; set; }
        }

        private sealed class FfProbeTags
        {
            public string language { get; set; }
            public string title { get; set; }
        }
    }
}
