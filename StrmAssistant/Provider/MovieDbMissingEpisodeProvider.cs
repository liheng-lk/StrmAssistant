using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using StrmAssistant.Compatibility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Provider
{
    /// <summary>
    /// Provides a complete TMDB episode list to Emby's missing-episode feature.
    /// It preserves TMDB episode ids and understands the same TmdbEg/episodegroup.json
    /// mapping used by the metadata Episode Group compatibility layer.
    /// </summary>
    public sealed class MovieDbMissingEpisodeProvider : ISeriesMetadataProvider
    {
        public string Name => "TheMovieDb";

        public async Task<RemoteSearchResult[]> GetAllEpisodes(SeriesInfo seriesInfo,
            CancellationToken cancellationToken)
        {
            if (Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions?.EnhanceMissingEpisodes != true ||
                seriesInfo == null)
                return Array.Empty<RemoteSearchResult>();

            var tmdbId = seriesInfo.GetProviderId(MetadataProviders.Tmdb);
            if (string.IsNullOrWhiteSpace(tmdbId)) return Array.Empty<RemoteSearchResult>();

            var language = seriesInfo.MetadataLanguage;
            var episodeGroupId = seriesInfo.GetProviderId(MovieDbEpisodeGroupExternalId.StaticName)?.Trim();
            EpisodeGroupResponse episodeGroup = null;
            string localEpisodeGroupPath = null;

            var metadataOptions = Plugin.Instance?.GetPluginOptions()?.MetadataEnhanceOptions;
            var currentSeriesPath = MissingEpisodesRuntimeContext.CurrentSeriesContainingFolderPath.Value;
            MissingEpisodesRuntimeContext.CurrentSeriesContainingFolderPath.Value = null;

            if (metadataOptions?.LocalEpisodeGroup == true && !string.IsNullOrWhiteSpace(currentSeriesPath))
            {
                localEpisodeGroupPath = Path.Combine(currentSeriesPath, "episodegroup.json");
                episodeGroup = await Plugin.MetadataApi.FetchLocalEpisodeGroup(localEpisodeGroupPath)
                    .ConfigureAwait(false);
            }

            if (episodeGroup == null && metadataOptions?.MovieDbEpisodeGroup == true &&
                !string.IsNullOrWhiteSpace(episodeGroupId))
            {
                episodeGroup = await Plugin.MetadataApi.FetchOnlineEpisodeGroup(tmdbId, episodeGroupId, language,
                        localEpisodeGroupPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (episodeGroup?.groups != null && episodeGroup.groups.Count > 0)
            {
                if (episodeGroup.groups.SelectMany(group => group.episodes ?? new List<GroupEpisode>())
                    .Any(episode => string.IsNullOrWhiteSpace(episode.name) || episode.id <= 0))
                {
                    var standard = await FetchSeriesInfoAsync(tmdbId, language, cancellationToken)
                        .ConfigureAwait(false);
                    EnrichEpisodeGroup(episodeGroup, standard);
                }

                return episodeGroup.groups
                    .Where(group => group != null && group.episodes != null)
                    .SelectMany(group => group.episodes, (group, episode) => ToSearchResult(
                        episode.id,
                        episode.order + 1,
                        group.order + 1,
                        episode.name,
                        episode.overview,
                        episode.air_date))
                    .Where(result => result != null)
                    .ToArray();
            }

            var series = await FetchSeriesInfoAsync(tmdbId, language, cancellationToken).ConfigureAwait(false);
            if (series?.seasons == null) return Array.Empty<RemoteSearchResult>();

            return series.seasons
                .Where(season => season?.episodes != null)
                .SelectMany(season => season.episodes)
                .Where(episode => episode != null)
                .Select(episode => ToSearchResult(
                    episode.id,
                    episode.episode_number,
                    episode.season_number,
                    episode.name,
                    episode.overview,
                    episode.air_date))
                .Where(result => result != null)
                .ToArray();
        }

        private static RemoteSearchResult ToSearchResult(int tmdbEpisodeId, int episodeNumber, int seasonNumber,
            string name, string overview, DateTimeOffset airDate)
        {
            if (episodeNumber < 1 || seasonNumber < 0) return null;
            var result = new RemoteSearchResult
            {
                SearchProviderName = "TheMovieDb",
                IndexNumber = episodeNumber,
                ParentIndexNumber = seasonNumber,
                Name = name,
                Overview = overview
            };

            if (airDate.Year > 1)
            {
                result.PremiereDate = airDate;
                result.ProductionYear = airDate.Year;
            }

            if (tmdbEpisodeId > 0)
            {
                result.ProviderIds = new ProviderIdDictionary
                {
                    { MetadataProviders.Tmdb.ToString(), tmdbEpisodeId.ToString(CultureInfo.InvariantCulture) }
                };
            }
            return result;
        }

        private static async Task<SeriesResponseInfo> FetchSeriesInfoAsync(string tmdbId, string language,
            CancellationToken cancellationToken)
        {
            var languageKey = string.IsNullOrWhiteSpace(language) ? "default" : language;
            var cacheKey = "tmdb_all_episodes_" + tmdbId + "_" + languageKey;
            var cachePath = Path.Combine(Plugin.Instance.ApplicationPaths.CachePath, "tmdb-tv", tmdbId,
                "series-all-episodes-" + SanitizeFilePart(languageKey) + ".json");

            var cached = Plugin.MetadataApi.TryGetFromCache<SeriesResponseInfo>(cacheKey, cachePath);
            if (cached?.seasons != null) return cached;

            var rootUrl = Plugin.MetadataApi.BuildMovieDbApiUrl("tv/" + tmdbId, language);
            if (string.IsNullOrWhiteSpace(rootUrl)) return null;
            var root = await Plugin.MetadataApi.GetMovieDbResponse<SeriesResponseInfo>(rootUrl, cancellationToken)
                .ConfigureAwait(false);
            if (root?.seasons == null) return null;

            var seasons = new List<SeasonResponseInfo>();
            foreach (var season in root.seasons.OrderBy(value => value.season_number))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var seasonUrl = Plugin.MetadataApi.BuildMovieDbApiUrl(
                    "tv/" + tmdbId + "/season/" + season.season_number.ToString(CultureInfo.InvariantCulture),
                    language);
                if (string.IsNullOrWhiteSpace(seasonUrl)) continue;
                var fetched = await Plugin.MetadataApi.GetMovieDbResponse<SeasonResponseInfo>(seasonUrl,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (fetched != null) seasons.Add(fetched);
            }

            root.seasons = seasons;
            if (root.seasons.Count > 0)
                Plugin.MetadataApi.AddOrUpdateCache(root, cacheKey, cachePath);
            return root;
        }

        private static void EnrichEpisodeGroup(EpisodeGroupResponse groupResponse, SeriesResponseInfo standard)
        {
            if (groupResponse?.groups == null || standard?.seasons == null) return;
            foreach (var group in groupResponse.groups)
            {
                if (group?.episodes == null) continue;
                foreach (var episode in group.episodes)
                {
                    var mapped = standard.seasons
                        .FirstOrDefault(season => season.season_number == episode.season_number)
                        ?.episodes?.FirstOrDefault(item => item.episode_number == episode.episode_number);
                    if (mapped == null) continue;
                    if (episode.id <= 0) episode.id = mapped.id;
                    if (string.IsNullOrWhiteSpace(episode.name)) episode.name = mapped.name;
                    if (string.IsNullOrWhiteSpace(episode.overview)) episode.overview = mapped.overview;
                    if (episode.air_date.Year <= 1) episode.air_date = mapped.air_date;
                }
            }
        }

        private static string SanitizeFilePart(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string((value ?? "default").Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        }
    }
}
