using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.Api;
using StrmAssistant.Common;
using StrmAssistant.MediaEnhance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class NativeRemoteDeleteDeferredCleanupStatus
    {
        public bool ImmediateCleanupSuppressed { get; set; }
        public int CleanupTargetsPatched { get; set; }
        public long PendingQueued { get; set; }
        public long ItemRemovedMatched { get; set; }
        public long DeferredCleanupsSucceeded { get; set; }
        public long DeferredCleanupsFailed { get; set; }
        public long PendingExpired { get; set; }
        public int PendingCount { get; set; }
        public string LastItemPath { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class NativeRemoteDeleteDeferredCleanupState
    {
        public static NativeRemoteDeleteDeferredCleanupStatus Status { get; internal set; } =
            new NativeRemoteDeleteDeferredCleanupStatus();
    }

    /// <summary>
    /// Remote/local target deletion necessarily happens before Emby removes its library item. MediaInfo
    /// persistence must not be deleted before that final local/library step because it can still fail.
    /// This entry point suppresses immediate cleanup in both the native remote bridge and the explicit
    /// DeepDelete API, records the exact item, and clears persistence/shadow only after ItemRemoved
    /// confirms that Emby actually removed that item.
    /// </summary>
    public sealed class NativeRemoteDeleteDeferredCleanupEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.native-remote-delete-deferred-cleanup";
        private readonly ILibraryManager _libraryManager;
        private Harmony _harmony;
        private System.Threading.Timer _pruneTimer;

        public NativeRemoteDeleteDeferredCleanupEntryPoint(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public void Run()
        {
            var status = new NativeRemoteDeleteDeferredCleanupStatus();
            NativeRemoteDeleteDeferredCleanupState.Status = status;
            try
            {
                var cleanups = new[]
                    {
                        typeof(NativeItemDeleteRemoteBridgePatches).GetMethod(
                            "CleanupPersistenceSnapshot", BindingFlags.Static | BindingFlags.NonPublic),
                        typeof(DeepDeleteApiService).GetMethod(
                            "CleanupPersistenceSnapshot", BindingFlags.Static | BindingFlags.NonPublic)
                    }
                    .Where(method => method != null)
                    .Distinct()
                    .ToArray();
                if (cleanups.Length == 0)
                {
                    status.Error = "No pre-ItemRemoved MediaInfo cleanup target was found.";
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                var prefix = new HarmonyMethod(
                    typeof(NativeRemoteDeleteDeferredCleanupPatches).GetMethod(
                        nameof(NativeRemoteDeleteDeferredCleanupPatches.SuppressImmediateCleanup),
                        BindingFlags.Public | BindingFlags.Static));
                foreach (var cleanup in cleanups)
                {
                    try
                    {
                        _harmony.Patch(cleanup, prefix: prefix);
                        status.CleanupTargetsPatched++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Instance?.Logger?.Warn("Deferred MediaInfo cleanup could not patch {0}: {1}",
                            cleanup, ex.Message);
                    }
                }
                status.ImmediateCleanupSuppressed = status.CleanupTargetsPatched > 0;
                if (!status.ImmediateCleanupSuppressed)
                {
                    status.Error = "All deferred cleanup Harmony patches failed.";
                    return;
                }

                _libraryManager.ItemRemoved += OnItemRemoved;
                _pruneTimer = new System.Threading.Timer(_ => NativeRemoteDeleteDeferredCleanupQueue.PruneExpired(),
                    null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Error("Native remote-delete deferred cleanup initialization failed: " + status.Error);
            }
        }

        private void OnItemRemoved(object sender, ItemChangeEventArgs e)
        {
            try
            {
                if (e?.Item == null || !NativeRemoteDeleteDeferredCleanupQueue.TryTake(e.Item, out var pending)) return;
                Increment(status =>
                {
                    status.ItemRemovedMatched++;
                    status.LastItemPath = pending.ItemPath;
                });
                if (CleanupPersistenceSnapshot(e.Item))
                {
                    Increment(status =>
                    {
                        status.DeferredCleanupsSucceeded++;
                        status.LastError = null;
                    });
                }
                else
                {
                    Increment(status => status.DeferredCleanupsFailed++);
                }
            }
            catch (Exception ex)
            {
                Increment(status =>
                {
                    status.DeferredCleanupsFailed++;
                    status.LastError = ex.GetBaseException().Message;
                });
                Plugin.Instance?.Logger?.Warn("Deferred remote-delete MediaInfo cleanup failed: " + ex.Message);
            }
        }

        private static bool CleanupPersistenceSnapshot(BaseItem item)
        {
            if (item == null || Plugin.MediaInfoApi == null) return false;
            try
            {
                MediaInfoPersistenceReliabilityPatches.ExplicitDeepDeletePrefix();
                try
                {
                    var directoryService = Plugin.MediaInfoApi.GetMediaInfoRefreshOptions().DirectoryService;
                    var primary = MediaInfoApi.GetMediaInfoJsonPath(item);
                    Plugin.MediaInfoApi.DeleteMediaInfoJson(item, directoryService,
                        "Confirmed Deep Delete ItemRemoved");
                    var backup = MediaInfoPersistenceReliabilityPatches.BackupPath(primary);
                    if (!string.IsNullOrWhiteSpace(backup) && System.IO.File.Exists(backup))
                        System.IO.File.Delete(backup);
                    MediaInfoReliabilityShadowStore.Delete(item);
                }
                finally
                {
                    MediaInfoPersistenceReliabilityPatches.ExplicitDeepDeleteFinalizer(null);
                }
                return true;
            }
            catch (Exception ex)
            {
                Increment(status => status.LastError = ex.GetBaseException().Message);
                return false;
            }
        }

        public void Dispose()
        {
            try { _libraryManager.ItemRemoved -= OnItemRemoved; } catch { }
            try { _pruneTimer?.Dispose(); } catch { }
            _pruneTimer = null;
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }

        internal static void Increment(Action<NativeRemoteDeleteDeferredCleanupStatus> action)
        {
            var status = NativeRemoteDeleteDeferredCleanupState.Status;
            if (status == null || action == null) return;
            lock (NativeRemoteDeleteDeferredCleanupQueue.SyncRoot)
            {
                action(status);
                status.PendingCount = NativeRemoteDeleteDeferredCleanupQueue.CountUnsafe;
            }
        }
    }

    public static class NativeRemoteDeleteDeferredCleanupPatches
    {
        public static bool SuppressImmediateCleanup(BaseItem item)
        {
            NativeRemoteDeleteDeferredCleanupQueue.MarkPending(item, null);
            return false;
        }
    }

    public sealed class NativeRemoteDeletePendingCleanup
    {
        public long ItemId { get; set; }
        public string ItemPath { get; set; }
        public string RemotePath { get; set; }
        public DateTimeOffset QueuedUtc { get; set; }
    }

    public static class NativeRemoteDeleteDeferredCleanupQueue
    {
        internal static readonly object SyncRoot = new object();
        private static readonly Dictionary<long, NativeRemoteDeletePendingCleanup> Pending =
            new Dictionary<long, NativeRemoteDeletePendingCleanup>();
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

        internal static int CountUnsafe => Pending.Count;

        public static void MarkPending(BaseItem item, string remotePath)
        {
            if (item == null || item.InternalId <= 0) return;
            lock (SyncRoot)
            {
                PruneExpiredUnsafe();
                Pending[item.InternalId] = new NativeRemoteDeletePendingCleanup
                {
                    ItemId = item.InternalId,
                    ItemPath = item.Path,
                    RemotePath = remotePath,
                    QueuedUtc = DateTimeOffset.UtcNow
                };
                var status = NativeRemoteDeleteDeferredCleanupState.Status;
                if (status != null)
                {
                    status.PendingQueued++;
                    status.PendingCount = Pending.Count;
                    status.LastItemPath = item.Path;
                }
            }
        }

        public static void CancelPending(long itemId)
        {
            if (itemId <= 0) return;
            lock (SyncRoot)
            {
                Pending.Remove(itemId);
                var status = NativeRemoteDeleteDeferredCleanupState.Status;
                if (status != null) status.PendingCount = Pending.Count;
            }
        }

        public static bool TryTake(BaseItem item, out NativeRemoteDeletePendingCleanup pending)
        {
            pending = null;
            if (item == null || item.InternalId <= 0) return false;
            lock (SyncRoot)
            {
                PruneExpiredUnsafe();
                if (!Pending.TryGetValue(item.InternalId, out var candidate)) return false;

                // InternalId is authoritative. Path is a second guard when both sides still have one.
                if (!string.IsNullOrWhiteSpace(candidate.ItemPath) && !string.IsNullOrWhiteSpace(item.Path) &&
                    !string.Equals(Normalize(candidate.ItemPath), Normalize(item.Path), StringComparison.Ordinal))
                    return false;

                Pending.Remove(item.InternalId);
                pending = candidate;
                var status = NativeRemoteDeleteDeferredCleanupState.Status;
                if (status != null) status.PendingCount = Pending.Count;
                return true;
            }
        }

        public static void PruneExpired()
        {
            lock (SyncRoot) PruneExpiredUnsafe();
        }

        private static void PruneExpiredUnsafe()
        {
            var threshold = DateTimeOffset.UtcNow - MaxAge;
            var expired = new List<long>();
            foreach (var pair in Pending)
                if (pair.Value == null || pair.Value.QueuedUtc < threshold) expired.Add(pair.Key);
            foreach (var id in expired) Pending.Remove(id);

            var status = NativeRemoteDeleteDeferredCleanupState.Status;
            if (status != null)
            {
                status.PendingExpired += expired.Count;
                status.PendingCount = Pending.Count;
            }
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().Replace('\\', '/');
        }
    }
}
