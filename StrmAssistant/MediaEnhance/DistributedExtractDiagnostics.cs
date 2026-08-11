using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.MediaEnhance
{
    public sealed class MediaToolCapabilityResult
    {
        public bool Success { get; set; }
        public string Executable { get; set; }
        public string Version { get; set; }
        public bool SupportsBluray { get; set; }
        public bool SupportsSmb { get; set; }
        public bool SupportsChromaprint { get; set; }
        public bool SupportsVulkan { get; set; }
        public bool SupportsLibplacebo { get; set; }
        public bool UsesRffmpegBackend { get; set; }
        public bool? VulkanLibplaceboTestPassed { get; set; }
        public bool? ChromaprintTestPassed { get; set; }
        public string Error { get; set; }
        public string DiagnosticOutput { get; set; }
    }

    public sealed class RffmpegStatusResult
    {
        public bool Configured { get; set; }
        public bool Success { get; set; }
        public string Executable { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
    }

    public sealed class DistributedExtractHealthResult
    {
        public bool Enabled { get; set; }
        public MediaToolCapabilityResult Ffprobe { get; set; }
        public MediaToolCapabilityResult Ffmpeg { get; set; }
        public RffmpegStatusResult Rffmpeg { get; set; }
    }

    /// <summary>
    /// Read-only diagnostics for custom ffprobe/ffmpeg and rffmpeg wrappers.
    /// It never modifies Emby's global encoder paths.
    /// </summary>
    public sealed class DistributedExtractDiagnostics
    {
        public Task<DistributedExtractHealthResult> CheckAsync(MediaInfoExtractOptions options,
            bool runVulkanTest, CancellationToken cancellationToken)
        {
            return CheckAsync(options, runVulkanTest, false, cancellationToken);
        }

        public async Task<DistributedExtractHealthResult> CheckAsync(MediaInfoExtractOptions options,
            bool runVulkanTest, bool runChromaprintTest, CancellationToken cancellationToken)
        {
            options ??= new MediaInfoExtractOptions();
            var timeout = Math.Max(5, Math.Min(options.DistributedToolTimeoutSeconds, 120));

            var ffprobePath = ResolveExecutable(options.DistributedFfprobeExecutablePath, "ffprobe");
            var ffmpegPath = ResolveExecutable(options.DistributedFfmpegExecutablePath, "ffmpeg");

            var ffprobeTask = CheckToolAsync(ffprobePath, false, false, false, timeout, cancellationToken);
            var ffmpegTask = CheckToolAsync(ffmpegPath, true, runVulkanTest, runChromaprintTest, timeout,
                cancellationToken);
            var rffmpegTask = CheckRffmpegAsync(options.RffmpegExecutablePath, timeout, cancellationToken);

            await Task.WhenAll(ffprobeTask, ffmpegTask, rffmpegTask).ConfigureAwait(false);

            return new DistributedExtractHealthResult
            {
                Enabled = options.EnableDistributedExtractDiagnostics,
                Ffprobe = await ffprobeTask.ConfigureAwait(false),
                Ffmpeg = await ffmpegTask.ConfigureAwait(false),
                Rffmpeg = await rffmpegTask.ConfigureAwait(false)
            };
        }

        private static async Task<MediaToolCapabilityResult> CheckToolAsync(string executable, bool isFfmpeg,
            bool runVulkanTest, bool runChromaprintTest, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var result = new MediaToolCapabilityResult { Executable = executable };
            var diagnosticParts = new List<string>();

            try
            {
                var version = await RunProcessAsync(executable, "-version", timeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
                var combinedVersion = CombineOutput(version);
                diagnosticParts.Add(combinedVersion);
                result.Version = FirstVersionLine(combinedVersion);
                result.SupportsChromaprint = Contains(combinedVersion, "--enable-chromaprint");
                result.SupportsVulkan = Contains(combinedVersion, "--enable-vulkan");
                result.SupportsLibplacebo = Contains(combinedVersion, "--enable-libplacebo");
                result.UsesRffmpegBackend = LooksLikeRffmpeg(version.StandardOutput, version.StandardError);

                if (version.ExitCode != 0)
                {
                    result.Error = BuildError(version);
                    result.DiagnosticOutput = Truncate(string.Join(Environment.NewLine, diagnosticParts), 12000);
                    return result;
                }

                var protocols = await RunProcessAsync(executable, "-v error -protocols", timeoutSeconds,
                        cancellationToken)
                    .ConfigureAwait(false);
                var protocolOutput = CombineOutput(protocols);
                diagnosticParts.Add(protocolOutput);
                result.SupportsBluray = HasProtocol(protocolOutput, "bluray");
                result.SupportsSmb = HasProtocol(protocolOutput, "smb");
                result.UsesRffmpegBackend |= LooksLikeRffmpeg(protocols.StandardOutput, protocols.StandardError);

                if (protocols.ExitCode != 0)
                {
                    result.Error = BuildError(protocols);
                    result.DiagnosticOutput = Truncate(string.Join(Environment.NewLine, diagnosticParts), 12000);
                    return result;
                }

                if (isFfmpeg && runChromaprintTest)
                {
                    // Active test: synthesize one second of audio and ask ffmpeg to emit a Chromaprint fingerprint.
                    // This verifies the muxer/runtime path rather than trusting only the build configuration string.
                    const string chromaprintArgs = "-hide_banner -loglevel error " +
                                                   "-f lavfi -i sine=frequency=997:sample_rate=44100:duration=1 " +
                                                   "-map 0:a:0 -ac 1 -ar 11025 -f chromaprint -";
                    var chromaprint = await RunProcessAsync(executable, chromaprintArgs, timeoutSeconds,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var chromaprintOutput = CombineOutput(chromaprint);
                    diagnosticParts.Add("[chromaprint-test]" + Environment.NewLine + chromaprintOutput);
                    result.ChromaprintTestPassed = chromaprint.ExitCode == 0 &&
                                                   !string.IsNullOrWhiteSpace(chromaprint.StandardOutput);
                    result.UsesRffmpegBackend |= LooksLikeRffmpeg(chromaprint.StandardOutput,
                        chromaprint.StandardError);
                    if (result.ChromaprintTestPassed == true) result.SupportsChromaprint = true;
                    if (chromaprint.ExitCode != 0)
                        result.Error = "Chromaprint capability test failed: " + BuildError(chromaprint);
                    else if (result.ChromaprintTestPassed != true)
                        result.Error = "Chromaprint capability test returned no fingerprint output.";
                }

                if (isFfmpeg && runVulkanTest)
                {
                    const string testArgs = "-hide_banner -loglevel error -init_hw_device vulkan=vulkan " +
                                            "-filter_hw_device vulkan -f lavfi -i color=c=black:s=16x16:d=1 " +
                                            "-vf libplacebo -f null -";
                    var test = await RunProcessAsync(executable, testArgs, timeoutSeconds, cancellationToken)
                        .ConfigureAwait(false);
                    var testOutput = CombineOutput(test);
                    diagnosticParts.Add("[vulkan-libplacebo-test]" + Environment.NewLine + testOutput);
                    result.VulkanLibplaceboTestPassed = test.ExitCode == 0;
                    result.UsesRffmpegBackend |= LooksLikeRffmpeg(test.StandardOutput, test.StandardError);
                    if (test.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Error))
                        result.Error = "Vulkan/libplacebo capability test failed: " + BuildError(test);
                }

                result.Success = string.IsNullOrWhiteSpace(result.Error);
                result.DiagnosticOutput = Truncate(string.Join(Environment.NewLine, diagnosticParts), 12000);
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.DiagnosticOutput = Truncate(string.Join(Environment.NewLine, diagnosticParts), 12000);
                return result;
            }
        }

        private static async Task<RffmpegStatusResult> CheckRffmpegAsync(string configuredPath, int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            var result = new RffmpegStatusResult
            {
                Configured = !string.IsNullOrWhiteSpace(configuredPath),
                Executable = configuredPath?.Trim().Trim('"')
            };

            if (!result.Configured) return result;

            try
            {
                var process = await RunProcessAsync(result.Executable, "status", timeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
                result.Output = Truncate(CombineOutput(process), 16000);
                result.Success = process.ExitCode == 0;
                if (!result.Success) result.Error = BuildError(process);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            return result;
        }

        private static bool HasProtocol(string output, string protocol)
        {
            return (output ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Any(line => string.Equals(line, protocol, StringComparison.OrdinalIgnoreCase));
        }

        private static bool LooksLikeRffmpeg(string stdout, string stderr)
        {
            var value = (stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty);
            return Contains(value, "starting rffmpeg") ||
                   Contains(value, "database in use:") ||
                   Contains(value, "running command host=") ||
                   Contains(value, "finished rffmpeg");
        }

        private static string FirstVersionLine(string output)
        {
            return (output ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("ffprobe version", StringComparison.OrdinalIgnoreCase) ||
                                        line.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase));
        }

        private static bool Contains(string value, string needle)
        {
            return value?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ResolveExecutable(string configured, string fallback)
        {
            return string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim().Trim('"');
        }

        private static string CombineOutput(ProcessResult result)
        {
            return (result.StandardOutput ?? string.Empty) + Environment.NewLine + (result.StandardError ?? string.Empty);
        }

        private static string BuildError(ProcessResult result)
        {
            var first = CombineOutput(result)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
            return string.IsNullOrWhiteSpace(first)
                ? "Process exited with code " + result.ExitCode + "."
                : "Process exited with code " + result.ExitCode + ": " + first;
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
            if (!process.Start()) throw new InvalidOperationException("Unable to start process: " + executable);

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