using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.Experience;
using System;
using System.Collections.Concurrent;

namespace StrmAssistant.Notification
{
    /// <summary>
    /// Server-side notification bridge for public Emby library events.
    ///
    /// Metadata notifications are restricted to ItemUpdateType.MetadataEdit so regular scans,
    /// metadata downloads and imports do not generate metadata.update events. The cache is used
    /// to compare the last observed state with the post-edit state.
    /// </summary>
    public sealed class NotificationEventMonitor : IServerEntryPoint
    {
        private const int MaxSnapshotCount = 50000;

        private readonly ILibraryManager _libraryManager;
        private readonly ConcurrentDictionary<long, MetadataSnapshot> _metadataSnapshots =
            new ConcurrentDictionary<long, MetadataSnapshot>();
        private bool _started;

        public NotificationEventMonitor(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public void Run()
        {
            if (_started) return;
            _started = true;

            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemUpdated += OnItemUpdated;
            _libraryManager.ItemRemoved += OnItemRemoved;
        }

        public void Dispose()
        {
            if (!_started) return;
            _started = false;

            _libraryManager.ItemAdded -= OnItemAdded;
            _libraryManager.ItemUpdated -= OnItemUpdated;
            _libraryManager.ItemRemoved -= OnItemRemoved;
            _metadataSnapshots.Clear();
        }

        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            CaptureBaseline(e?.Item);
        }

        private void OnItemRemoved(object sender, ItemChangeEventArgs e)
        {
            if (e?.Item == null) return;
            _metadataSnapshots.TryRemove(e.Item.InternalId, out _);
        }

        private void OnItemUpdated(object sender, ItemChangeEventArgs e)
        {
            var item = e?.Item;
            var plugin = Plugin.Instance;
            var notificationApi = Plugin.NotificationApi;
            if (item == null || plugin == null || notificationApi == null) return;

            var options = plugin.GetPluginOptions()?.ExperienceEnhanceOptions;
            if (options == null || !options.EnableNotificationEnhance)
            {
                return;
            }

            var trackedFields = MetadataChangeTracker.ParseTrackedFields(options.MetadataUpdateTrackedFields);
            MetadataSnapshot currentSnapshot = null;

            if (trackedFields.Count > 0)
            {
                currentSnapshot = MetadataChangeTracker.Capture(item, trackedFields);

                if (options.NotifyMetadataUpdate && HasFlag(e.UpdateReason, ItemUpdateType.MetadataEdit) &&
                    _metadataSnapshots.TryGetValue(item.InternalId, out var previousSnapshot))
                {
                    var changes = MetadataChangeTracker.Compare(previousSnapshot, currentSnapshot);
                    if (changes.Count > 0)
                    {
                        notificationApi.MetadataUpdateSendNotification(item, changes);
                    }
                }

                StoreSnapshot(currentSnapshot);
            }

            if (options.NotifyImageUpdate && HasFlag(e.UpdateReason, ItemUpdateType.ImageUpdate))
            {
                // ItemUpdated does not expose the concrete image type. The event is still useful
                // as a server-side fallback. The upcoming REST/UI integration will supply the
                // exact Primary/Backdrop/Logo/etc. type when available.
                notificationApi.ImageUpdateSendNotification(item, "Unknown");
            }
        }

        private void CaptureBaseline(MediaBrowser.Controller.Entities.BaseItem item)
        {
            var plugin = Plugin.Instance;
            if (item == null || plugin == null) return;

            var options = plugin.GetPluginOptions()?.ExperienceEnhanceOptions;
            if (options == null || !options.EnableNotificationEnhance || !options.NotifyMetadataUpdate) return;

            var trackedFields = MetadataChangeTracker.ParseTrackedFields(options.MetadataUpdateTrackedFields);
            if (trackedFields.Count == 0) return;

            StoreSnapshot(MetadataChangeTracker.Capture(item, trackedFields));
        }

        private void StoreSnapshot(MetadataSnapshot snapshot)
        {
            if (snapshot == null) return;

            if (_metadataSnapshots.Count >= MaxSnapshotCount)
            {
                // Avoid unbounded growth on very large libraries. Clearing only affects the next
                // edit notification for items that have not yet established a new baseline.
                _metadataSnapshots.Clear();
            }

            _metadataSnapshots[snapshot.ItemId] = snapshot;
        }

        private static bool HasFlag(ItemUpdateType value, ItemUpdateType flag)
        {
            return (value & flag) == flag;
        }
    }
}
