using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Events;
using StrmAssistant.Compatibility;
using StrmAssistant.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.MediaEnhance
{
    public sealed class MediaInfoIntegrityRecoveryQueueStatus
    {
        public bool Started { get; set; }
        public long Queued { get; set; }
        public long Deduplicated { get; set; }
        public long Recovered { get; set; }
        public long NoRecoverySource { get; set; }
        public long FailedAttempts { get; set; }
        public long Exhausted { get; set; }
        public long DroppedBecauseFull { get; set; }
        public long DrainSkippedDuringLibraryScan { get; set; }
        public int PendingCount { get; set; }
        public string LastItemPath { get; set; }
        public string LastError { get; set; }
    }

    public static class MediaInfoIntegrityRecoveryQueueState
    {
        public static MediaInfoIntegrityRecoveryQueueStatus Status { get; } =
            new MediaInfoIntegrityRecoveryQueueStatus();
    }

    internal sealed class MediaInfoRecoveryCandidate
    {
        public long ItemId { get; set; }
        public string Source { get; set; }
        public int Attempts { get; set; }
        public DateTimeOffset NextAttemptUtc { get; set; }
    }

    internal static class MediaInfoIntegrityRecoveryQueue
    {
        private const int MaxPending = 2048;
        private const int MaxAttempts = 3;
        private const int TimerBatchSize = 20;
        private static readonly ConcurrentDictionary<long, MediaInfoRecoveryCandidate> Pending =
            new ConcurrentDictionary<long, MediaInfoRecoveryCandidate>();
        private static readonly object TimerSync = new object();
        private static Timer _timer;
        private static int _draining;
        private static int _pendingCount;

        public static void Start()
        {
            lock (TimerSync)
            {
                if (_timer != null) return;
                MediaInfoIntegrityRecoveryQueueState.Status.Started = true;
                _timer = new Timer(_ =>
                {
                    try { _ = DrainAsync(false, TimerBatchSize, CancellationToken.None); }
                    catch { }
                }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
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
                MediaInfoIntegrityRecoveryQueueState.Status.Started = false;
                MediaInfoIntegrityRecoveryQueueState.Status.PendingCount = 0;
            }
        }

        public static void Queue(long itemId, string source)
        {
            if (itemId <= 0) return;
            if (Pending.TryGetValue(itemId, out var existing))
            {
                existing.Source = source ?? existing.Source;
                existing.NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(1);
                Increment(status => status.Deduplicated++);
                return;
            }

            if (Volatile.Read(ref _pendingCount) >= MaxPending)
            {
                Increment(status => status.DroppedBecauseFull++);
                return;
            }

            if (Pending.TryAdd(itemId, new MediaInfoRecoveryCandidate
                {
                    ItemId = itemId,
                    Source = source,
                    Attempts = 0,
                    NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(1)
                }))
            {
                var count = Interlocked.Increment(ref _pendingCount);
                Increment(status =>
                {
                    status.Queued++;
                    status.PendingCount = count;
                });
            }
            else
            {
                Increment(status => status.Deduplicated++);
            }
        }

        public static async Task<int> DrainAsync(bool force, int maxItems, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _draining, 1) != 0) return 0;
            var processed = 0;
            try
            {
                var manager = Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>();
                if (manager == null) return 0;
                if (!force && manager.IsScanRunning)
                {
                    Increment(status => status.DrainSkippedDuringLibraryScan++);
                    return 0;
                }

                var now = DateTimeOffset.UtcNow;
                var candidates = Pending.Values
                    .Where(candidate => force || candidate.NextAttemptUtc <= now)
                    .OrderBy(candidate => candidate.NextAttemptUtc)
                    .Take(Math.Max(1, maxItems))
                    .ToArray();

                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Pending.TryGetValue(candidate.ItemId, out var current)) continue;

                    BaseItem item;
                    try
                    {
                        item = manager.GetItemById(candidate.ItemId);
                    }
                    catch (Exception ex)
                    {
                        RegisterFailure(current, null, ex.GetBaseException().Message);
                        processed++;
                        continue;
                    }

                    if (item == null || MediaInfoIntegrityService.IsCoreMediaInfoComplete(item))
                    {
                        Remove(candidate.ItemId);
                        processed++;
                        continue;
                    }

                    if (!MediaInfoIntegrityMonitor.ShouldRecover(item))
                    {
                        Remove(candidate.ItemId);
                        Increment(status =>
                        {
                            status.NoRecoverySource++;
                            status.LastItemPath = item.Path;
                        });
                        processed++;
                        continue;
                    }

                    var recovered = false;
                    string recoveryError = null;
                    try
                    {
                        recovered = await MediaInfoIntegrityMonitor.RecoverAsync(item,
                                (current.Source ?? "Queued IntegrityRepair") + " Background", cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        recoveryError = ex.GetBaseException().Message;
                    }

                    if (recovered)
                    {
                        Remove(candidate.ItemId);
                        Increment(status =>
                        {
                            status.Recovered++;
                            status.LastItemPath = item.Path;
                            status.LastError = null;
                        });
                    }
                    else
                    {
                        RegisterFailure(current, item.Path, recoveryError ??
                            "Validated local MediaInfo recovery returned false.");
                    }
                    processed++;
                }
                return processed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Increment(status => status.LastError = "Recovery queue drain failed: " + ex.GetBaseException().Message);
                return processed;
            }
            finally
            {
                Increment(status => status.PendingCount = Volatile.Read(ref _pendingCount));
                Volatile.Write(ref _draining, 0);
            }
        }

        private static void RegisterFailure(MediaInfoRecoveryCandidate current, string itemPath, string error)
        {
            if (current == null) return;
            current.Attempts++;
            Increment(status =>
            {
                status.FailedAttempts++;
                status.LastItemPath = itemPath;
                status.LastError = error;
            });

            if (current.Attempts >= MaxAttempts)
            {
                Remove(current.ItemId);
                Increment(status =>
                {
                    status.Exhausted++;
                    status.LastItemPath = itemPath;
                    status.LastError = "Local MediaInfo recovery exhausted automatic retries; use the explicit STRM MediaInfo repair task if a remote rebuild is required. Last error: " + error;
                });
            }
            else
            {
                current.NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(30);
            }
        }

        private static bool Remove(long itemId)
        {
            if (!Pending.TryRemove(itemId, out _)) return false;
            Interlocked.Decrement(ref _pendingCount);
            return true;
        }

        private static void Increment(Action<MediaInfoIntegrityRecoveryQueueStatus> action)
        {
            if (action == null) return;
            lock (MediaInfoIntegrityRecoveryQueueState.Status) action(MediaInfoIntegrityRecoveryQueueState.Status);
        }
    }

    /// <summary>
    /// Repairs MediaInfo after provider refreshes that leave runtime/streams incomplete. Refresh events
    /// only enqueue item ids; they never enumerate the whole library and never deserialize/write recovery
    /// files inline. The bounded queue pauses normal draining while a library scan is active. Playback
    /// pre-read can still recover the same item immediately from local snapshots when a user requests it.
    /// </summary>
    public sealed class MediaInfoIntegrityMonitor : IServerEntryPoint
    {
        private readonly IProviderManager _providerManager;
        private bool _started;
        private static readonly object[] RecoveryLocks = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();

        public MediaInfoIntegrityMonitor(IProviderManager providerManager)
        {
            _providerManager = providerManager;
        }

        public void Run()
        {
            if (_started) return;
            _started = true;
            _providerManager.RefreshCompleted += OnRefreshCompleted;
            MediaInfoIntegrityRecoveryQueue.Start();
        }

        public void Dispose()
        {
            if (!_started) return;
            _started = false;
            _providerManager.RefreshCompleted -= OnRefreshCompleted;
            MediaInfoIntegrityRecoveryQueue.Stop();
        }

        private void OnRefreshCompleted(object sender, GenericEventArgs<RefreshProgressInfo> e)
        {
            var item = e?.Argument?.Item;
            if (item == null) return;

            if (MediaInfoReliabilityShadowStore.AppliesTo(item) &&
                MediaInfoIntegrityService.IsCoreMediaInfoComplete(item))
            {
                MediaInfoReliabilityShadowPatches.QueueCapture(item.InternalId);
                return;
            }

            if (item is Video || item is Audio)
                MediaInfoIntegrityRecoveryQueue.Queue(item.InternalId, "RefreshCompleted IntegrityRepair");
        }

        internal static bool ShouldRecover(BaseItem item)
        {
            if (item == null || Plugin.Instance == null || Plugin.LibraryApi == null || Plugin.MediaInfoApi == null)
                return false;
            if (MediaInfoIntegrityService.IsCoreMediaInfoComplete(item)) return false;

            if (MediaInfoReliabilityShadowStore.AppliesTo(item) &&
                MediaInfoReliabilityShadowStore.Exists(item))
                return true;

            var options = Plugin.Instance.GetPluginOptions()?.MediaInfoExtractOptions;
            return PersistenceEnabledFor(item, options) &&
                   Plugin.LibraryApi.IsLibraryInScope(item) &&
                   MediaInfoIntegrityService.SnapshotExists(item);
        }

        internal static bool PersistenceEnabledFor(BaseItem item, MediaInfoExtractOptions options)
        {
            if (options == null ||
                options.PersistMediaInfoMode == MediaInfoExtractOptions.PersistMediaInfoOption.None.ToString())
                return false;

            if (item is Audio) return options.PersistMusicMediaInfo;
            return item is Video;
        }

        internal static bool SnapshotExists(BaseItem item)
        {
            return MediaInfoIntegrityService.SnapshotExists(item) || MediaInfoReliabilityShadowStore.Exists(item);
        }

        internal static Task<bool> RecoverAsync(BaseItem item, string source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item == null || MediaInfoIntegrityService.IsCoreMediaInfoComplete(item))
                return Task.FromResult(true);

            var lockIndex = (int)(item.InternalId % RecoveryLocks.Length);
            if (lockIndex < 0) lockIndex = -lockIndex;
            lock (RecoveryLocks[lockIndex])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fresh = Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>()?.GetItemById(item.InternalId) ?? item;
                if (MediaInfoIntegrityService.IsCoreMediaInfoComplete(fresh)) return Task.FromResult(true);

                var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
                var canUsePersisted = PersistenceEnabledFor(fresh, options) &&
                                      Plugin.LibraryApi?.IsLibraryInScope(fresh) == true &&
                                      MediaInfoIntegrityService.SnapshotExists(fresh);
                if (canUsePersisted && MediaInfoIntegrityService.HydrateCore(fresh, source + " Persisted"))
                {
                    var restored = Plugin.Instance.ApplicationHost.Resolve<ILibraryManager>()?.GetItemById(fresh.InternalId) ?? fresh;
                    if (MediaInfoReliabilityShadowStore.AppliesTo(restored))
                        MediaInfoReliabilityShadowPatches.QueueCapture(restored.InternalId);
                    return Task.FromResult(true);
                }

                return Task.FromResult(MediaInfoReliabilityShadowStore.Restore(fresh, source + " Shadow"));
            }
        }
    }

    public sealed class MediaInfoIntegrityPostScanTask : ILibraryPostScanTask
    {
        public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (Plugin.Instance == null || Plugin.LibraryApi == null || Plugin.MediaInfoApi == null)
                return;

            progress?.Report(0);
            // No GetItemList/full-library sweep here. RefreshCompleted has already queued only item ids
            // that actually changed; force one bounded pass now that Emby's scan reached post-scan.
            await MediaInfoIntegrityRecoveryQueue.DrainAsync(true, 200, cancellationToken).ConfigureAwait(false);
            progress?.Report(100);
        }
    }
}
