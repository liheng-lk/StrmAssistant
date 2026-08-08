using Emby.Notifications;
using MediaBrowser.Controller;
using System.Collections.Generic;

namespace StrmAssistant.Notification
{
    /// <summary>
    /// Registers Strm Assistant events with the current Emby notification settings UI.
    /// Event registration is independent from delivery; delivery remains gated by
    /// ExperienceEnhanceOptions.
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

        private readonly IServerApplicationHost _appHost;

        public StrmAssistantNotificationTypes(IServerApplicationHost appHost)
        {
            _appHost = appHost;
        }

        public List<NotificationTypeInfo> GetNotificationTypes(string language)
        {
            const string categoryId = "strm.assistant";
            const string categoryName = "Strm Assistant";

            return new List<NotificationTypeInfo>
            {
                Create(FavoritesUpdate, "收藏媒体更新", categoryId, categoryName),
                Create(IntroSkipUpdate, "片头片尾更新", categoryId, categoryName),
                Create(DeepDelete, "媒体深度删除", categoryId, categoryName),
                Create(MetadataUpdate, "媒体元数据更新", categoryId, categoryName),
                Create(ImageUpdate, "媒体图片更新", categoryId, categoryName),
                Create(CollectionItemsAdded, "合集新增媒体", categoryId, categoryName),
                Create(CollectionItemsRemoved, "合集移除媒体", categoryId, categoryName)
            };
        }

        private static NotificationTypeInfo Create(string id, string name, string categoryId, string categoryName)
        {
            return new NotificationTypeInfo
            {
                Id = id,
                Name = name,
                CategoryId = categoryId,
                CategoryName = categoryName
            };
        }
    }
}
