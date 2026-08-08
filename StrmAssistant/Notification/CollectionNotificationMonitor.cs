using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Plugins;
using System;
using System.Linq;

namespace StrmAssistant.Notification
{
    /// <summary>
    /// Emits collection.items.added / collection.items.removed from Emby's public
    /// ICollectionManager events.
    /// </summary>
    public sealed class CollectionNotificationMonitor : IServerEntryPoint
    {
        private readonly ICollectionManager _collectionManager;
        private bool _started;

        public CollectionNotificationMonitor(ICollectionManager collectionManager)
        {
            _collectionManager = collectionManager;
        }

        public void Run()
        {
            if (_started) return;
            _started = true;

            _collectionManager.ItemsAddedToCollection += OnItemsAdded;
            _collectionManager.ItemsRemovedFromCollection += OnItemsRemoved;
        }

        public void Dispose()
        {
            if (!_started) return;
            _started = false;

            _collectionManager.ItemsAddedToCollection -= OnItemsAdded;
            _collectionManager.ItemsRemovedFromCollection -= OnItemsRemoved;
        }

        private static void OnItemsAdded(object sender, CollectionModifiedEventArgs e)
        {
            var notificationApi = Plugin.NotificationApi;
            var options = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            if (notificationApi == null || options == null || !options.EnableNotificationEnhance ||
                !options.NotifyCollectionItemsUpdate || e?.Collection == null)
            {
                return;
            }

            notificationApi.CollectionItemsAddedSendNotification(
                e.Collection,
                BuildDescription(e, "Added"));
        }

        private static void OnItemsRemoved(object sender, CollectionModifiedEventArgs e)
        {
            var notificationApi = Plugin.NotificationApi;
            var options = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            if (notificationApi == null || options == null || !options.EnableNotificationEnhance ||
                !options.NotifyCollectionItemsUpdate || e?.Collection == null)
            {
                return;
            }

            notificationApi.CollectionItemsRemovedSendNotification(
                e.Collection,
                BuildDescription(e, "Removed"));
        }

        private static string BuildDescription(CollectionModifiedEventArgs e, string action)
        {
            var names = e.ItemsChanged?
                .Where(item => item != null)
                .Select(item => string.IsNullOrWhiteSpace(item.Name)
                    ? item.InternalId.ToString()
                    : item.Name)
                .ToList() ?? new System.Collections.Generic.List<string>();

            var firstLine = $"Collection: {e.Collection.Name}";
            if (names.Count == 0) return firstLine;

            return firstLine + Environment.NewLine +
                   string.Join(Environment.NewLine, names.Select(name => $"{action}: {name}"));
        }
    }
}
