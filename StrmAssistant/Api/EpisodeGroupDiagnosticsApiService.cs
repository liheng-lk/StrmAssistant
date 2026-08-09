using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;
using StrmAssistant.Provider;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    public sealed class EpisodeGroupPreviewEpisode
    {
        public int DisplaySeason { get; set; }
        public int DisplayEpisode { get; set; }
        public int OriginalSeason { get; set; }
        public int OriginalEpisode { get; set; }
    }

    public sealed class EpisodeGroupPreviewGroup
    {
        public string Name { get; set; }
        public int DisplaySeason { get; set; }
        public int EpisodeCount { get; set; }
        public List<EpisodeGroupPreviewEpisode> Episodes { get; set; } = new List<EpisodeGroupPreviewEpisode>();
    }

    public sealed class EpisodeGroupPreviewResult
    {
        public bool Success { get; set; }
        public string SeriesId { get; set; }
        public string SeriesName { get; set; }
        public string SeriesPath { get; set; }
        public string TmdbId { get; set; }
        public string EpisodeGroupId { get; set; }
        public string LocalPath { get; set; }
        public bool LocalExists { get; set; }
        public string Source { get; set; }
        public string GroupDescription { get; set; }
        public int GroupCount { get; set; }
        public List<EpisodeGroupPreviewGroup> Groups { get; set; } = new List<EpisodeGroupPreviewGroup>();
        public List<string> Warnings { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    [Route("/StrmAssistant/EpisodeGroup/{Id}/Preview", "GET",
        Summary = "Preview TMDB/local episode-group mappings without refreshing metadata")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetEpisodeGroupPreview : IReturn<EpisodeGroupPreviewResult>
    {
        public string Id { get; set; }
        public bool PreferLocal { get; set; } = true;
        public int MaxEpisodesPerGroup { get; set; } = 50;
    }

    public sealed class EpisodeGroupDiagnosticsApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;

        public EpisodeGroupDiagnosticsApiService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public async Task<object> Get(GetEpisodeGroupPreview request)
        {
            var series = ResolveSeries(request?.Id);
            if (series == null)
                return new EpisodeGroupPreviewResult { Error = "Series item was not found." };

            var result = new EpisodeGroupPreviewResult
            {
                SeriesId = series.InternalId.ToString(),
                SeriesName = series.Name,
                SeriesPath = series.Path,
                TmdbId = series.GetProviderId(MetadataProviders.Tmdb),
                EpisodeGroupId = series.GetProviderId(MovieDbEpisodeGroupExternalId.StaticName),
                LocalPath = Plugin.MetadataApi.GetEpisodeGroupLocalPath(series)
            };
            result.LocalExists = !string.IsNullOrWhiteSpace(result.LocalPath) && File.Exists(result.LocalPath);

            EpisodeGroupResponse response = null;
            if (request?.PreferLocal != false && result.LocalExists)
            {
                response = await Plugin.MetadataApi.FetchLocalEpisodeGroup(result.LocalPath).ConfigureAwait(false);
                if (response != null) result.Source = "Local";
            }

            if (response == null && !string.IsNullOrWhiteSpace(result.EpisodeGroupId))
            {
                response = await Plugin.MetadataApi.FetchOnlineEpisodeGroup(result.TmdbId,
                        result.EpisodeGroupId, Plugin.MetadataApi.GetPreferredMetadataLanguage(series),
                        null, CancellationToken.None)
                    .ConfigureAwait(false);
                if (response != null) result.Source = "MovieDb";
            }

            if (response == null)
            {
                result.Error = string.IsNullOrWhiteSpace(result.EpisodeGroupId) && !result.LocalExists
                    ? "No TmdbEg provider ID or local episodegroup.json is available."
                    : "Episode-group data could not be loaded.";
                return result;
            }

            var maxEpisodes = Math.Max(1, Math.Min(request?.MaxEpisodesPerGroup ?? 50, 500));
            result.GroupDescription = response.description;
            result.GroupCount = response.groups?.Count ?? 0;
            foreach (var group in response.groups ?? new List<EpisodeGroup>())
            {
                var preview = new EpisodeGroupPreviewGroup
                {
                    Name = group.name,
                    DisplaySeason = group.order + 1,
                    EpisodeCount = group.episodes?.Count ?? 0
                };

                preview.Episodes = (group.episodes ?? new List<GroupEpisode>())
                    .OrderBy(e => e.order)
                    .Take(maxEpisodes)
                    .Select(e => new EpisodeGroupPreviewEpisode
                    {
                        DisplaySeason = group.order + 1,
                        DisplayEpisode = e.order + 1,
                        OriginalSeason = e.season_number,
                        OriginalEpisode = e.episode_number
                    }).ToList();
                result.Groups.Add(preview);
            }

            if (result.Groups.Any(g => g.EpisodeCount > maxEpisodes))
                result.Warnings.Add("Preview episode lists were truncated; increase MaxEpisodesPerGroup if needed.");

            result.Success = true;
            return result;
        }

        private Series ResolveSeries(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !long.TryParse(id, out var internalId)) return null;
            var item = _libraryManager.GetItemById(internalId);
            if (item is Series series) return series;
            if (item is Season season) return season.Series;
            if (item is Episode episode) return episode.Series;
            return null;
        }
    }
}
