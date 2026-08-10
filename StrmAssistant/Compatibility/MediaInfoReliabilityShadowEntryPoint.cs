using HarmonyLib;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.MediaEnhance;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace StrmAssistant.Compatibility
{
    public sealed class MediaInfoReliabilityShadowRuntimeStatus
    {
        public int SaveMediaStreamsTargetsPatched { get; set; }
        public long DeferredCapturesScheduled { get; set; }
        public long DeferredCapturesCompleted { get; set; }
        public long DeferredCapturesSkipped { get; set; }
        public int PendingCaptureCount { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class MediaInfoReliabilityShadowRuntimeState
    {
        public static MediaInfoReliabilityShadowRuntimeStatus Status { get; internal set; } =
            new MediaInfoReliabilityShadowRuntimeStatus();
    }

    public sealed class MediaInfoReliabilityShadowEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.mediainfo-shadow";
        private Harmony _harmony;

        public void Run()
        {
            var status = new MediaInfoReliabilityShadowRuntimeStatus();
            MediaInfoReliabilityShadowRuntimeState.Status = status;
            try
            {
                var repository = Plugin.Instance?.ApplicationHost?.Resolve<IItemRepository>();
                if (repository == null)
                {
                    status.Error = "IItemRepository is unavailable.";
                    return;
                }

                var saveTargets = repository.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method => string.Equals(method.Name, "SaveMediaStreams", StringComparison.Ordinal) &&
                                     method.GetParameters().Length >= 2)
                    .Distinct()
                    .ToArray();
                if (saveTargets.Length == 0)
                {
                    status.Error = "No runtime SaveMediaStreams target was found for the STRM reliability shadow.";
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                var postfix = new HarmonyMethod(typeof(MediaInfoReliabilityShadowPatches).GetMethod(
                    nameof(MediaInfoReliabilityShadowPatches.SaveMediaStreamsPostfix),
                    BindingFlags.Public | BindingFlags.Static));
                foreach (var target in saveTargets)
                {
                    try
                    {
                        _harmony.Patch(target, postfix: postfix);
                        status.SaveMediaStreamsTargetsPatched++;
                    }
                    catch (Exception ex)
                    {
                        status.LastError = ex.Message;
                    }
                }

                if (status.SaveMediaStreamsTargetsPatched == 0)
                {
                    status.Error = "SaveMediaStreams targets were discovered but none could be patched.";
                    return;
                }

                MediaInfoReliabilityShadowPatches.Start();
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Error("MediaInfo reliability shadow patch failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            MediaInfoReliabilityShadowPatches.Stop();
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }
    }

    public static class MediaInfoReliabilityShadowPatches
    {
        private const int MaxBatchPerTick = 50;
        private static readonly ConcurrentDictionary<long, DateTimeOffset> Pending =
            new ConcurrentDictionary<long, DateTimeOffset>();
        private static readonly object TimerSync = new object();
        private static Timer _timer;
        private static int _draining;
        private static int _pendingCount;

        public static void Start()
        {
            lock (TimerSync)
            {
                if (_timer != null) return;
                _timer = new Timer(Drain, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
        }

        public static void Stop()
        {
            lock (TimerSync)
            {
                try { _timer?.Dispose(); } catch { }
                _timer = null;
                Pending.Clear();
                Interlocked.Exchange(ref _pendingCount, 0);
                MediaInfoReliabilityShadowRuntimeState.Status.PendingCaptureCount = 0;
            }
        }

        public static void SaveMediaStreamsPostfix(object[] __args)
        {
            if (__args == null) return;
            var itemId = FindItemId(__args);
            if (itemId <= 0) return;

            try
            {
                var libraryManager = Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>();
                var item = libraryManager?.GetItemById(itemId);
                if (!MediaInfoReliabilityShadowStore.AppliesTo(item)) return;

                var isNew = Pending.TryAdd(itemId, DateTimeOffset.UtcNow);
                if (!isNew)
                {
                    Pending[itemId] = DateTimeOffset.UtcNow;
                }
                else
                {
                    Interlocked.Increment(ref _pendingCount);
                    MediaInfoReliabilityShadowRuntimeState.Status.DeferredCapturesScheduled++;
                }
                MediaInfoReliabilityShadowRuntimeState.Status.PendingCaptureCount = Volatile.Read(ref _pendingCount);
            }
            catch (Exception ex)
            {
                MediaInfoReliabilityShadowRuntimeState.Status.LastError = ex.GetBaseException().Message;
            }
        }

        private static void Drain(object state)
        {
            if (Interlocked.Exchange(ref _draining, 1) != 0) return;
            try
            {
                var now = DateTimeOffset.UtcNow;
                var ready = Pending
                    .Where(pair => now - pair.Value >= TimeSpan.FromMilliseconds(750))
                    .OrderBy(pair => pair.Value)
                    .Take(MaxBatchPerTick)
                    .Select(pair => pair.Key)
                    .ToArray();
                if (ready.Length == 0) return;

                var libraryManager = Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>();
                foreach (var itemId in ready)
                {
                    if (!Pending.TryRemove(itemId, out _)) continue;
                    Interlocked.Decrement(ref _pendingCount);
                    try
                    {
                        var item = libraryManager?.GetItemById(itemId);
                        if (MediaInfoReliabilityShadowStore.Capture(item))
                            MediaInfoReliabilityShadowRuntimeState.Status.DeferredCapturesCompleted++;
                        else
                            MediaInfoReliabilityShadowRuntimeState.Status.DeferredCapturesSkipped++;
                    }
                    catch (Exception ex)
                    {
                        MediaInfoReliabilityShadowRuntimeState.Status.LastError = ex.GetBaseException().Message;
                    }
                }
            }
            catch (Exception ex)
            {
                MediaInfoReliabilityShadowRuntimeState.Status.LastError = ex.GetBaseException().Message;
            }
            finally
            {
                MediaInfoReliabilityShadowRuntimeState.Status.PendingCaptureCount = Volatile.Read(ref _pendingCount);
                Volatile.Write(ref _draining, 0);
            }
        }

        private static long FindItemId(object[] args)
        {
            if (args == null) return 0;
            foreach (var arg in args)
            {
                if (arg is long longValue && longValue > 0) return longValue;
                if (arg is int intValue && intValue > 0) return intValue;
            }
            return 0;
        }
    }
}
