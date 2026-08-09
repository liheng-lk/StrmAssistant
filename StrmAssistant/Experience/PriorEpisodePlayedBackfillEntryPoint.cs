using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using System;
using System.Linq;
using System.Threading;

namespace StrmAssistant.Experience
{
    /// <summary>
    /// When an episode genuinely finishes playback, optionally marks earlier episodes in the same
    /// series as played only when they already have a non-zero playback position. Manual TogglePlayed
    /// events do not trigger the backfill.
    /// </summary>
    public sealed class PriorEpisodePlayedBackfillEntryPoint : IServerEntryPoint
    {
        private readonly IUserDataManager _userDataManager;
        private readonly ILibraryManager _libraryManager;

        [ThreadStatic]
        private static bool _updatingPriorEpisodes;

        public PriorEpisodePlayedBackfillEntryPoint(IUserDataManager userDataManager, ILibraryManager libraryManager)
        {
            _userDataManager = userDataManager;
            _libraryManager = libraryManager;
        }

        public void Run()
        {
            _userDataManager.UserDataSaved += OnUserDataSaved;
        }

        public void Dispose()
        {
            _userDataManager.UserDataSaved -= OnUserDataSaved;
        }

        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            if (_updatingPriorEpisodes || e?.Item is not Episode current || e.User == null || e.UserData == null)
                return;
            if (Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions?.MarkPriorProgressEpisodesPlayed != true)
                return;
            if (e.SaveReason != UserDataSaveReason.PlaybackFinished || !e.UserData.Played)
                return;
            if (!current.ParentIndexNumber.HasValue || !current.IndexNumber.HasValue || current.Series == null)
                return;

            try
            {
                _updatingPriorEpisodes = true;
                var series = current.Series;
                var episodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    ParentWithPresentationUniqueKeyFromItemId = series.InternalId,
                    Recursive = true
                }).OfType<Episode>()
                    .Where(episode => episode.Series?.InternalId == series.InternalId &&
                                      episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue &&
                                      IsEarlier(episode, current))
                    .OrderBy(episode => episode.ParentIndexNumber.Value)
                    .ThenBy(episode => episode.IndexNumber.Value)
                    .ThenBy(episode => episode.InternalId)
                    .ToList();

                var changed = 0;
                foreach (var episode in episodes)
                {
                    var data = _userDataManager.GetUserData(e.User, episode);
                    if (data == null || data.Played || data.PlaybackPositionTicks <= 0) continue;

                    data.Played = true;
                    data.PlaybackPositionTicks = 0;
                    _userDataManager.SaveUserData(e.User, episode, data, UserDataSaveReason.TogglePlayed,
                        CancellationToken.None);
                    changed++;
                }

                if (changed > 0)
                {
                    Plugin.Instance?.Logger?.Info(
                        "Marked {0} earlier partially-played episode(s) as played after {1} S{2:00}E{3:00} finished.",
                        changed, series.Name, current.ParentIndexNumber.Value, current.IndexNumber.Value);
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Prior episode played backfill failed: " + ex.Message);
            }
            finally
            {
                _updatingPriorEpisodes = false;
            }
        }

        private static bool IsEarlier(Episode candidate, Episode current)
        {
            var candidateSeason = candidate.ParentIndexNumber.Value;
            var currentSeason = current.ParentIndexNumber.Value;
            if (candidateSeason < currentSeason) return true;
            return candidateSeason == currentSeason && candidate.IndexNumber.Value < current.IndexNumber.Value;
        }
    }
}
