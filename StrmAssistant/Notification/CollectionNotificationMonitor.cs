using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using System;
using System.Collections.Generic;
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
        private readonly ILibraryManager _libraryManager;
        private bool _started;

        public CollectionNotificationMonitor(ICollectionManager collectionManager, ILibraryManager libraryManager)
        {
            _collectionManager = collectionManager;
            _libraryManager = libraryManager;
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

        private void OnItemsAdded(object sender, CollectionModifiedEventArgs e)
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

        private void OnItemsRemoved(object sender, CollectionModifiedEventArgs e)
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

        private string BuildDescription(CollectionModifiedEventArgs e, string action)
        {
            var names = new List<string>();

            if (e.ItemsChanged != null)
            {
                foreach (var itemId in e.ItemsChanged)
                {
                    var item = _libraryManager.GetItemById(itemId);
                    names.Add(item == null || string.IsNullOrWhiteSpace(item.Name)
                        ? itemId.ToString()
                        : item.Name);
                }
            }

            var firstLine = $"Collection: {e.Collection.Name}";
            if (names.Count == 0) return firstLine;

            return firstLine + Environment.NewLine +
                   string.Join(Environment.NewLine, names.Select(name => $"{action}: {name}"));
        }
    }
}
