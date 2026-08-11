using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.MediaEnhance
{
    public sealed class BluRayDiscEnrichmentSummary
    {
        public bool Available { get; set; }
        public bool Attempted { get; set; }
        public bool Applied { get; set; }
        public string PlaylistName { get; set; }
        public List<string> PlayableFiles { get; set; } = new List<string>();
        public int DiscStreamCount { get; set; }
        public int DiscChapterCount { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Runtime-optional bridge to Emby's Blu-ray examiner. Some Emby SDK generations
    /// do not expose IBlurayExaminer at compile time, so the capability is resolved by
    /// reflection and instantiated through IApplicationHost.CreateInstance(Type).
    /// </summary>
    public sealed class BluRayDiscInfoEnricher
    {
        private const string ExaminerInterfaceName = "MediaBrowser.Model.MediaInfo.IBlurayExaminer";
        private readonly object _examiner;
        private readonly MethodInfo _getDiscInfo;

        public BluRayDiscInfoEnricher(IApplicationHost applicationHost)
        {
            if (applicationHost == null) return;

            try
            {
                LoadCandidateAssemblies();
                var interfaceType = FindType(ExaminerInterfaceName);
                if (interfaceType == null) return;

                var implementationType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(SafeGetTypes)
                    .FirstOrDefault(type => type != null && !type.IsAbstract && !type.IsInterface &&
                                            interfaceType.IsAssignableFrom(type));
                if (implementationType == null) return;

                _examiner = applicationHost.CreateInstance(implementationType);
                _getDiscInfo = interfaceType.GetMethod("GetDiscInfo", new[] { typeof(string) }) ??
                               implementationType.GetMethod("GetDiscInfo", new[] { typeof(string) });
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Debug("BluRayDiscInfoEnricher init failed: " + ex.Message);
                Plugin.Instance?.Logger?.Debug(ex.StackTrace);
            }
        }

        public BluRayDiscEnrichmentSummary Enrich(Video item, OpticalProbeResult probeResult)
        {
            var summary = new BluRayDiscEnrichmentSummary
            {
                Available = _examiner != null && _getDiscInfo != null
            };

            if (item == null || probeResult == null || !probeResult.Success || !summary.Available)
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
                var discInfo = _getDiscInfo.Invoke(_examiner, new object[] { path });
                if (discInfo == null)
                {
                    summary.Error = "Emby Blu-ray examiner returned no disc information.";
                    return summary;
                }

                summary.PlaylistName = ReadValue(discInfo, "PlaylistName")?.ToString();
                summary.PlayableFiles = ReadStrings(discInfo, "Files");
                var discStreams = ReadMediaStreams(discInfo, "MediaStreams");
                var discChapters = ReadDoubles(discInfo, "Chapters");
                summary.DiscStreamCount = discStreams.Count;
                summary.DiscChapterCount = discChapters.Count;

                // Match Emby's own policy: multi-file Blu-ray playlists use BDInfo as the
                // authoritative stream/chapter source, with ffprobe filling video gaps.
                if (summary.PlayableFiles.Count <= 1 || discStreams.Count == 0)
                    return summary;

                var ffprobeVideo = probeResult.Streams?
                    .FirstOrDefault(stream => string.Equals(stream.Type, "video", StringComparison.OrdinalIgnoreCase));
                var enrichedStreams = discStreams.Select(ToProbeStream).ToList();
                var enrichedVideo = enrichedStreams
                    .FirstOrDefault(stream => string.Equals(stream.Type, "video", StringComparison.OrdinalIgnoreCase));

                FillVideoGaps(enrichedVideo, ffprobeVideo);
                probeResult.Streams = enrichedStreams;

                var runtime = ReadNullableLong(discInfo, "RunTimeTicks");
                if (runtime.HasValue && runtime.Value > 0) probeResult.RunTimeTicks = runtime;

                if (discChapters.Count > 0)
                {
                    probeResult.Chapters = discChapters
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
            catch (TargetInvocationException ex)
            {
                summary.Error = ex.InnerException?.Message ?? ex.Message;
            }
            catch (Exception ex)
            {
                summary.Error = ex.Message;
            }

            return summary;
        }

        private static void FillVideoGaps(OpticalProbeStreamInfo target, OpticalProbeStreamInfo fallback)
        {
            if (target == null || fallback == null) return;
            if (!target.Width.HasValue || target.Width.Value == 0) target.Width = fallback.Width;
            if (!target.Height.HasValue || target.Height.Value == 0) target.Height = fallback.Height;
            if (!target.BitRate.HasValue || target.BitRate.Value == 0) target.BitRate = fallback.BitRate;
            if (!target.AverageFrameRate.HasValue) target.AverageFrameRate = fallback.AverageFrameRate;
            if (string.IsNullOrWhiteSpace(target.PixelFormat)) target.PixelFormat = fallback.PixelFormat;
            if (string.IsNullOrWhiteSpace(target.ColorTransfer)) target.ColorTransfer = fallback.ColorTransfer;
            if (string.IsNullOrWhiteSpace(target.ColorPrimaries)) target.ColorPrimaries = fallback.ColorPrimaries;
            if (string.IsNullOrWhiteSpace(target.ColorSpace)) target.ColorSpace = fallback.ColorSpace;
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

        private static object ReadValue(object target, string propertyName)
        {
            return target?.GetType().GetProperty(propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);
        }

        private static List<string> ReadStrings(object target, string propertyName)
        {
            return (ReadValue(target, propertyName) as IEnumerable)?.Cast<object>()
                       .Select(value => value?.ToString())
                       .Where(value => !string.IsNullOrWhiteSpace(value))
                       .ToList() ?? new List<string>();
        }

        private static List<MediaStream> ReadMediaStreams(object target, string propertyName)
        {
            return (ReadValue(target, propertyName) as IEnumerable)?.Cast<object>()
                       .OfType<MediaStream>()
                       .ToList() ?? new List<MediaStream>();
        }

        private static List<double> ReadDoubles(object target, string propertyName)
        {
            var values = new List<double>();
            if (!(ReadValue(target, propertyName) is IEnumerable enumerable)) return values;

            foreach (var value in enumerable)
            {
                try
                {
                    values.Add(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                }
                catch
                {
                    // ignore malformed chapter entries
                }
            }

            return values;
        }

        private static long? ReadNullableLong(object target, string propertyName)
        {
            var value = ReadValue(target, propertyName);
            if (value == null) return null;
            try
            {
                return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }

            return null;
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null);
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        private static void LoadCandidateAssemblies()
        {
            foreach (var name in new[]
                     {
                         "MediaBrowser.Model", "MediaBrowser.Controller", "Emby.Providers",
                         "Emby.Server.Implementations", "MediaBrowser.MediaEncoding"
                     })
            {
                try
                {
                    Assembly.Load(name);
                }
                catch
                {
                    // Optional runtime assembly.
                }
            }
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
