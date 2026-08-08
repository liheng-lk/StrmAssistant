using MediaBrowser.Controller.Notifications;
using MediaBrowser.Model.Notifications;
using System.Collections.Generic;

namespace StrmAssistant.Notification
{
    /// <summary>
    /// Registers Strm Assistant events with Emby's notification settings UI.
    /// Delivery is still controlled by ExperienceEnhanceOptions so registering an event
    /// never enables destructive or noisy behavior by itself.
    /// </summary>
    public sealed class StrmAssistantNotificationTypes : INotificationTypeFactory
    {
        public const string FavoritesUpdate = "favorites.update";
        public const string IntroSkipUpdate = "introskip.update";
        public const string DeepDelete = "deep.delete";
        public const string MetadataUpdate = "metadata.update";
        public const string ImageUpdate = "image.update";
        public const string CollectionItemsAdded = "collection.items.added";
        public const string CollectionItemsRemoved = "collection.items.removed";

        public IEnumerable<NotificationTypeInfo> GetNotificationTypes()
        {
            const string category = "Strm Assistant";

            return new[]
            {
                Create(FavoritesUpdate, "收藏媒体更新", category, true),
                Create(IntroSkipUpdate, "片头片尾更新", category, true),
                Create(DeepDelete, "媒体深度删除", category, true),
                Create(MetadataUpdate, "媒体元数据更新", category, false),
                Create(ImageUpdate, "媒体图片更新", category, false),
                Create(CollectionItemsAdded, "合集新增媒体", category, false),
                Create(CollectionItemsRemoved, "合集移除媒体", category, false)
            };
        }

        private static NotificationTypeInfo Create(string type, string name, string category, bool userEvent)
        {
            return new NotificationTypeInfo
            {
                Type = type,
                Name = name,
                Category = category,
                IsBasedOnUserEvent = userEvent
            };
        }
    }
}
