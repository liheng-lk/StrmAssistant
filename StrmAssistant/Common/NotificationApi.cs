using Emby.Notifications;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;
using StrmAssistant.Experience;
using StrmAssistant.Notification;
using StrmAssistant.Options;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    public class NotificationApi
    {
        private readonly ILogger _logger;
        private readonly INotificationManager _notificationManager;
        private readonly IUserManager _userManager;
        private readonly ISessionManager _sessionManager;

        public NotificationApi(INotificationManager notificationManager, IUserManager userManager, ISessionManager sessionManager)
        {
            _logger = Plugin.Instance.Logger;
            _notificationManager = notificationManager;
            _userManager = userManager;
            _sessionManager = sessionManager;
        }

        private ExperienceEnhanceOptions ExperienceOptions =>
            Plugin.Instance.GetPluginOptions().ExperienceEnhanceOptions;

        public void FavoritesUpdateSendNotification(BaseItem item)
        {
            var options = ExperienceOptions;
            if (!options.EnableNotificationEnhance || !options.NotifyFavoritesUpdate) return;

            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            var users = Plugin.LibraryApi.GetUsersByFavorites(item);
            foreach (var user in users)
            {
                var request = new NotificationRequest
                {
                    Title = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                    EventId = StrmAssistantNotificationTypes.FavoritesUpdate,
                    User = user,
                    Item = item,
                    Description =
                        string.Format(
                            Resources.Notification_CatchupUpdate_EventDescription.Replace("\\n",
                                Environment.NewLine), item.Path, user)
                };
                _notificationManager.SendNotification(request);
            }
        }

        public void DeepDeleteSendNotification(BaseItem item, User user, HashSet<string> mountPaths)
        {
            var options = ExperienceOptions;
            if (!options.EnableNotificationEnhance || !options.NotifyDeepDelete) return;

            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            var mountPathList = string.Join(Environment.NewLine, mountPaths ?? new HashSet<string>());

            var request = new NotificationRequest
            {
                Title = Resources.PluginOptions_EditorTitle_Strm_Assistant + " - " +
                        Resources.Notification_DeepDelete_EventName,
                EventId = StrmAssistantNotificationTypes.DeepDelete,
                User = user,
                Item = item,
                Description =
                    string.Format(
                        Resources.Notification_DeepDelete_EventDescription.Replace("\\n", Environment.NewLine),
                        item.Name, item.Path, mountPathList)
            };

            _notificationManager.SendNotification(request);
        }

        public async Task IntroUpdateSendNotification(Episode episode, SessionInfo session, string introStartTime,
            string introEndTime)
        {
            var options = ExperienceOptions;
            if (!options.EnableNotificationEnhance || !options.NotifyIntroCreditsUpdate) return;

            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            if (CanDisplayMessage(session))
            {
                var message = new MessageCommand
                {
                    Header = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                    Text = string.Format(
                        Resources.Notification_IntroUpdate_Message, episode.FindSeriesName(), episode.FindSeasonName()),
                    TimeoutMs = 500
                };
                await _sessionManager.SendMessageCommand(session.Id, session.Id, message, CancellationToken.None);
            }

            var request = new NotificationRequest
            {
                Title = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                EventId = StrmAssistantNotificationTypes.IntroSkipUpdate,
                User = _userManager.GetUserById(session.UserInternalId),
                Item = episode,
                Session = session,
                Description = string.Format(
                    Resources.Notification_IntroUpdate_Description.Replace("\\n", Environment.NewLine),
                    episode.FindSeriesName(), episode.FindSeasonName(), introStartTime, introEndTime,
                    session.UserName)
            };
            _notificationManager.SendNotification(request);
        }

        public async Task CreditsUpdateSendNotification(Episode episode, SessionInfo session, string creditsDuration)
        {
            var options = ExperienceOptions;
            if (!options.EnableNotificationEnhance || !options.NotifyIntroCreditsUpdate) return;

            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            if (CanDisplayMessage(session))
            {
                var message = new MessageCommand
                {
                    Header = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                    Text = string.Format(
                        Resources.Notification_CreditsUpdate_Message, episode.FindSeriesName(), episode.FindSeasonName()),
                    TimeoutMs = 500
                };
                await _sessionManager.SendMessageCommand(session.Id, session.Id, message, CancellationToken.None);
            }

            var request = new NotificationRequest
            {
                Title = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                EventId = StrmAssistantNotificationTypes.IntroSkipUpdate,
                User = _userManager.GetUserById(session.UserInternalId),
                Item = episode,
                Session = session,
                Description = string.Format(
                    Resources.Notification_CreditsUpdate_Description.Replace("\\n", Environment.NewLine),
                    episode.FindSeriesName(), episode.FindSeasonName(), creditsDuration, session.UserName)
            };
            _notificationManager.SendNotification(request);
        }

        public void MetadataUpdateSendNotification(BaseItem item, IEnumerable<MetadataFieldChange> changes)
        {
            var changeList = (changes ?? Array.Empty<MetadataFieldChange>()).ToList();
            if (changeList.Count == 0) return;

            MetadataUpdateSendNotification(item, MetadataChangeTracker.FormatDescription(changeList));
        }

        public void MetadataUpdateSendNotification(BaseItem item, string description)
        {
            var options = ExperienceOptions;
            if (!options.EnableNotificationEnhance || !options.NotifyMetadataUpdate) return;

            SendItemEventToAdmins(StrmAssistantNotificationTypes.MetadataUpdate, "媒体元数据更新", item, description);
        }

        public void ImageUpdateSendNotification(BaseItem item, string imageType)
        {
            var options = ExperienceOptions;
            if (!options.EnableNotificationEnhance || !options.NotifyImageUpdate) return;

            var description = string.IsNullOrWhiteSpace(imageType)
                ? "Image Type: Unknown"
                : $"Image Type: {imageType.Trim()}";

            SendItemEventToAdmins(StrmAssistantNotificationTypes.ImageUpdate, "媒体图片更新", item, description);
        }

        public void CollectionItemsAddedSendNotification(BaseItem item, string description)
        {
            var options = ExperienceOptions;
            if (!options.EnableNotificationEnhance || !options.NotifyCollectionItemsUpdate) return;

            SendItemEventToAdmins(StrmAssistantNotificationTypes.CollectionItemsAdded, "合集新增媒体", item, description);
        }

        public void CollectionItemsRemovedSendNotification(BaseItem item, string description)
        {
            var options = ExperienceOptions;
            if (!options.EnableNotificationEnhance || !options.NotifyCollectionItemsUpdate) return;

            SendItemEventToAdmins(StrmAssistantNotificationTypes.CollectionItemsRemoved, "合集移除媒体", item, description);
        }

        private void SendItemEventToAdmins(string eventId, string titleSuffix, BaseItem item, string description)
        {
            var admins = LibraryApi.AllUsers.Where(kvp => kvp.Value).Select(kvp => kvp.Key).ToList();
            foreach (var admin in admins)
            {
                _notificationManager.SendNotification(new NotificationRequest
                {
                    Title = $"{Resources.PluginOptions_EditorTitle_Strm_Assistant} - {titleSuffix}",
                    EventId = eventId,
                    User = admin,
                    Item = item,
                    Description = description ?? string.Empty
                });
            }
        }

        public async Task SendMessageToAdmins(string text, long? timeout)
        {
            var message = new MessageCommand
            {
                Header = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                Text = text,
                TimeoutMs = timeout
            };

            var admins = LibraryApi.AllUsers.Where(kvp => kvp.Value).Select(kvp => kvp.Key);
            var sessions = _sessionManager.Sessions.Where(CanDisplayMessage)
                .Where(s => admins.Any(u => s.ContainsUser(u.InternalId)));

            foreach (var session in sessions)
            {
                await _sessionManager.SendMessageCommand(session.Id, session.Id, message, CancellationToken.None);
            }
        }

        private bool CanDisplayMessage(SessionInfo session)
        {
            return session?.SupportedCommands?.Contains("DisplayMessage") == true;
        }
    }
}
