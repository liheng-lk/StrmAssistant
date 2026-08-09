using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using System;

namespace StrmAssistant.Provider
{
    public class MovieDbEpisodeGroupExternalId : IExternalId
    {
        private string _tmdbId;
        private string _episodeGroupId;

        public string Name => "MovieDb Episode Group";
        public string Key => StaticName;

        public string UrlFormatString
        {
            get
            {
                if (IsHttpUrl(_episodeGroupId)) return _episodeGroupId;
                return !string.IsNullOrWhiteSpace(_tmdbId)
                    ? $"https://www.themoviedb.org/tv/{_tmdbId}/episode_group/{{0}}"
                    : null;
            }
        }

        public bool Supports(IHasProviderIds item)
        {
            _tmdbId = item?.GetProviderId(MetadataProviders.Tmdb);
            _episodeGroupId = item?.GetProviderId(StaticName);
            return item is Series;
        }

        public static string StaticName => "TmdbEg";

        private static bool IsHttpUrl(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }
}
