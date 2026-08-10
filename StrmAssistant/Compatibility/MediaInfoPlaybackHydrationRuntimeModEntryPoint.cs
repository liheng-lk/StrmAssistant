using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.MediaEnhance;
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class MediaInfoPlaybackHydrationStatus
    {
        public bool TargetFound { get; set; }
        public bool Patched { get; set; }
        public int PatchedMethodCount { get; set; }
        public long PlaybackChecks { get; set; }
        public long HydrationAttempts { get; set; }
        public long HydrationSucceeded { get; set; }
        public long HydrationFailed { get; set; }
        public long LastHydrationMilliseconds { get; set; }
        public string LastItemPath { get; set; }
        public string Error { get; set; }
    }

    public static class MediaInfoPlaybackHydrationState
    {
        public static MediaInfoPlaybackHydrationStatus Status { get; internal set; } =
            new MediaInfoPlaybackHydrationStatus();
    }

    /// <summary>
    /// Pre-play fast path for persisted STRM/remote MediaInfo. Emby builds PlaybackInfo through its
    /// runtime MediaSourceManager; if core streams/runtime disappeared after a refresh, Emby may
    /// probe the remote target again. This prefix restores only local persisted core fields before
    /// the original playback method runs. It never invokes ffprobe or a remote network request.
    /// </summary>
    public sealed class MediaInfoPlaybackHydrationRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.mediainfo-playback-hydration";
        private Harmony _harmony;

        public void Run()
        {
            var status = new MediaInfoPlaybackHydrationStatus();
            MediaInfoPlaybackHydrationState.Status = status;
            try
            {
                var manager = Plugin.Instance?.ApplicationHost?.Resolve<IMediaSourceManager>();
                var managerType = manager?.GetType();
                if (managerType == null)
                {
                    status.Error = "IMediaSourceManager runtime implementation is unavailable.";
                    return;
                }

                var methods = managerType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method =>
                        (string.Equals(method.Name, "GetPlayackMediaSources", StringComparison.Ordinal) ||
                         string.Equals(method.Name, "GetPlaybackMediaSources", StringComparison.Ordinal)) &&
                        method.GetParameters().Any(parameter => typeof(BaseItem).IsAssignableFrom(parameter.ParameterType)))
                    .ToList();

                status.TargetFound = methods.Count > 0;
                if (methods.Count == 0)
                {
                    status.Error = "No runtime playback-media-source method was found.";
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                var prefix = new HarmonyMethod(typeof(MediaInfoPlaybackHydrationPatches).GetMethod(
                    nameof(MediaInfoPlaybackHydrationPatches.Prefix), BindingFlags.Public | BindingFlags.Static));
                foreach (var method in methods)
                {
                    try
                    {
                        _harmony.Patch(method, prefix: prefix);
                        status.PatchedMethodCount++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Instance?.Logger?.Warn("MediaInfo playback hydration target skipped: {0}: {1}",
                            method, ex.Message);
                    }
                }

                status.Patched = status.PatchedMethodCount > 0;
                if (!status.Patched && string.IsNullOrWhiteSpace(status.Error))
                    status.Error = "Playback media source targets were found but none could be patched.";
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("MediaInfo playback hydration patch failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }
    }

    public static class MediaInfoPlaybackHydrationPatches
    {
        private static readonly object Sync = new object();

        public static void Prefix(object[] __args)
        {
            var status = MediaInfoPlaybackHydrationState.Status;
            lock (Sync) status.PlaybackChecks++;

            BaseItem item = null;
            try
            {
                item = __args?.OfType<BaseItem>().FirstOrDefault();
                if (item == null || (!item.IsShortcut && item.IsFileProtocol)) return;

                var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
                if (!MediaInfoIntegrityMonitor.PersistenceEnabledFor(item, options)) return;
                if (Plugin.LibraryApi?.IsLibraryInScope(item) != true) return;
                if (MediaInfoIntegrityService.IsCoreMediaInfoComplete(item)) return;
                if (!MediaInfoIntegrityService.SnapshotExists(item)) return;

                var watch = Stopwatch.StartNew();
                lock (Sync)
                {
                    status.HydrationAttempts++;
                    status.LastItemPath = item.Path;
                }

                var success = MediaInfoIntegrityService.HydrateCore(item, "PlaybackInfo PreHydration");
                watch.Stop();
                lock (Sync)
                {
                    status.LastHydrationMilliseconds = watch.ElapsedMilliseconds;
                    if (success) status.HydrationSucceeded++;
                    else status.HydrationFailed++;
                }
            }
            catch (Exception ex)
            {
                lock (Sync)
                {
                    status.HydrationFailed++;
                    status.LastItemPath = item?.Path;
                }
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("MediaInfo PlaybackInfo pre-hydration failed: " + ex.Message);
            }
        }
    }
}
