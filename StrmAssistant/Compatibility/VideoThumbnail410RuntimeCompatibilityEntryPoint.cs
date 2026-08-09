using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using StrmAssistant.Common;
using StrmAssistant.MediaEnhance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class VideoThumbnail410CapabilityStatus
    {
        public bool TargetFound { get; set; }
        public bool Patched { get; set; }
        public bool NativeRefreshMethodFound { get; set; }
        public int NativeParameterCount { get; set; }
        public string Error { get; set; }
    }

    public static class VideoThumbnail410CompatibilityState
    {
        public static VideoThumbnail410CapabilityStatus Status { get; internal set; } =
            new VideoThumbnail410CapabilityStatus();
    }

    /// <summary>
    /// Keeps Strm Assistant's distributed chapter-image pre-generation while adapting the
    /// final call into Emby's ThumbnailGenerator. Emby 4.10 adds another extraction flag,
    /// producing a 10-parameter RefreshThumbnailImages overload.
    /// </summary>
    public sealed class VideoThumbnail410RuntimeCompatibilityEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.thumbnail-410";
        private Harmony _harmony;

        public void Run()
        {
            var status = new VideoThumbnail410CapabilityStatus();
            VideoThumbnail410CompatibilityState.Status = status;

            try
            {
                var target = typeof(VideoThumbnailApi).GetMethod(
                    "RefreshThumbnailImages",
                    BindingFlags.Instance | BindingFlags.Public);
                status.TargetFound = target != null;
                if (target == null)
                {
                    status.Error = "VideoThumbnailApi.RefreshThumbnailImages was not found.";
                    return;
                }

                var nativeMethod = typeof(VideoThumbnailApi).GetField(
                    "_refreshThumbnailImages",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Plugin.VideoThumbnailApi) as MethodInfo;
                status.NativeRefreshMethodFound = nativeMethod != null;
                status.NativeParameterCount = nativeMethod?.GetParameters().Length ?? 0;

                _harmony = new Harmony(HarmonyId);
                var prefix = typeof(VideoThumbnail410Patches).GetMethod(
                    nameof(VideoThumbnail410Patches.RefreshThumbnailImagesPrefix),
                    BindingFlags.Static | BindingFlags.Public);
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                status.Patched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Video thumbnail 4.10 compatibility unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }
    }

    public static class VideoThumbnail410Patches
    {
        public static bool RefreshThumbnailImagesPrefix(
            VideoThumbnailApi __instance,
            Video item,
            LibraryOptions libraryOptions,
            IDirectoryService directoryService,
            List<ChapterInfo> chapters,
            bool extractImages,
            bool saveChapters,
            CancellationToken cancellationToken,
            ref Task<bool> __result)
        {
            __result = InvokeCompatibleAsync(__instance, item, libraryOptions, directoryService, chapters,
                extractImages, saveChapters, cancellationToken);
            return false;
        }

        private static async Task<bool> InvokeCompatibleAsync(
            VideoThumbnailApi instance,
            Video item,
            LibraryOptions libraryOptions,
            IDirectoryService directoryService,
            List<ChapterInfo> chapters,
            bool extractImages,
            bool saveChapters,
            CancellationToken cancellationToken)
        {
            if (MediaExtractionFilter.ShouldSkip(item, out var reason))
            {
                Plugin.Instance?.Logger?.Info("VideoThumbnailExtract - Skipped by extraction blacklist: {0} ({1})",
                    item?.Path, reason);
                return false;
            }

            var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
            if (extractImages && options?.EnableDistributedChapterImageRouting == true)
            {
                var generator = typeof(VideoThumbnailApi).GetField(
                    "_distributedChapterImageGenerator",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) as DistributedChapterImageGenerator;

                if (generator != null)
                {
                    try
                    {
                        var distributedResult = await generator
                            .GenerateMissingAsync(item, chapters, options, cancellationToken)
                            .ConfigureAwait(false);

                        if (distributedResult.Attempted)
                        {
                            Plugin.Instance?.Logger?.Info(
                                "VideoThumbnailExtract - Distributed chapter image pre-generation for {0}: generated={1}, existing={2}, failed={3}, fallback={4}, executable={5}",
                                item.Path, distributedResult.GeneratedCount, distributedResult.ExistingCount,
                                distributedResult.FailedCount, distributedResult.FellBackToNative,
                                distributedResult.Executable);

                            if (!string.IsNullOrWhiteSpace(distributedResult.Error))
                                Plugin.Instance?.Logger?.Warn(
                                    "VideoThumbnailExtract - Distributed chapter image detail: {0}",
                                    distributedResult.Error);

                            if (!distributedResult.Success && options.DistributedChapterImageFallbackToEmby != true)
                                return false;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Instance?.Logger?.ErrorException(
                            "VideoThumbnailExtract - Distributed chapter pre-generation failed for {0}",
                            ex, item.Path);
                        if (options.DistributedChapterImageFallbackToEmby != true) return false;
                    }
                }
            }

            var nativeMethod = typeof(VideoThumbnailApi).GetField(
                "_refreshThumbnailImages",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) as MethodInfo;
            var nativeGenerator = typeof(VideoThumbnailApi).GetField(
                "_thumbnailGenerator",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance);
            if (nativeMethod == null || nativeGenerator == null)
            {
                Plugin.Instance?.Logger?.Warn("VideoThumbnailExtract - Native ThumbnailGenerator is unavailable.");
                return false;
            }

            var methodParameters = nativeMethod.GetParameters();
            var mediaSource = item?.GetMediaSources(false, false, libraryOptions).FirstOrDefault();
            object[] args;

            switch (methodParameters.Length)
            {
                case 10:
                    args = new object[]
                    {
                        item, mediaSource, null, libraryOptions, directoryService, chapters, extractImages,
                        extractImages, saveChapters, cancellationToken
                    };
                    break;
                case 9:
                    args = new object[]
                    {
                        item, mediaSource, null, libraryOptions, directoryService, chapters, extractImages,
                        saveChapters, cancellationToken
                    };
                    break;
                default:
                    args = new object[]
                    {
                        item, null, libraryOptions, directoryService, chapters, extractImages, saveChapters,
                        cancellationToken
                    };
                    break;
            }

            try
            {
                var invocation = nativeMethod.Invoke(nativeGenerator, args);
                if (invocation is Task<bool> typedTask)
                    return await typedTask.ConfigureAwait(false);

                if (!(invocation is Task task)) return false;
                await task.ConfigureAwait(false);
                var result = task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(task);
                return result is bool success && success;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }
}
