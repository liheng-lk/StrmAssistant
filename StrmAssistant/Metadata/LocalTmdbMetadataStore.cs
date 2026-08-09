using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Metadata
{
    public sealed class LocalTmdbMetadataDocument
    {
        public string Name { get; set; }
        public string OriginalTitle { get; set; }
        public string Overview { get; set; }
        public string Tagline { get; set; }
        public int? ProductionYear { get; set; }
        public string PremiereDate { get; set; }
        public List<string> Genres { get; set; } = new List<string>();
        public Dictionary<string, string> ProviderIds { get; set; } = new Dictionary<string, string>();
    }

    public sealed class LocalTmdbMetadataIdentity
    {
        public string Kind { get; set; }
        public string TmdbId { get; set; }
        public string SeriesTmdbId { get; set; }
        public int? SeasonNumber { get; set; }
        public int? EpisodeNumber { get; set; }
        public string RelativePath { get; set; }
    }

    public sealed class LocalTmdbMetadataStore
    {
        private readonly IJsonSerializer _jsonSerializer;

        public LocalTmdbMetadataStore(IJsonSerializer jsonSerializer)
        {
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
        }

        public bool TryRead(BaseItem item, out LocalTmdbMetadataIdentity identity,
            out LocalTmdbMetadataDocument document, out string fullPath, out string error)
        {
            identity = ResolveIdentity(item);
            return TryRead(identity, out document, out fullPath, out error);
        }

        public bool TryRead(LocalTmdbMetadataIdentity identity, out LocalTmdbMetadataDocument document,
            out string fullPath, out string error)
        {
            document = null;
            fullPath = null;
            error = null;
            var options = LocalTmdbMetadataRuntimeSettings.GetSnapshot();
            if (!options.Enabled)
            {
                error = "Local TMDB metadata source is disabled.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(options.RootPath) || !Path.IsPathRooted(options.RootPath))
            {
                error = "Local TMDB RootPath must be a rooted local/mounted path.";
                return false;
            }
            if (identity == null || string.IsNullOrWhiteSpace(identity.RelativePath))
            {
                error = "No stable TMDB identity could be resolved for this item.";
                return false;
            }

            try
            {
                var root = Path.GetFullPath(options.RootPath);
                fullPath = Path.GetFullPath(Path.Combine(root, identity.RelativePath));
                var relative = Path.GetRelativePath(root, fullPath);
                if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    relative == "..")
                {
                    error = "Resolved local metadata path escaped RootPath.";
                    return false;
                }
                if (!File.Exists(fullPath)) return false;

                document = _jsonSerializer.DeserializeFromFile<LocalTmdbMetadataDocument>(fullPath);
                if (document == null)
                {
                    error = "Local metadata JSON deserialized to null.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        public LocalTmdbMetadataIdentity ResolveIdentity(BaseItem item)
        {
            if (item == null) return null;
            var tmdbId = GetProviderId(item, MetadataProviders.Tmdb.ToString());
            var identity = new LocalTmdbMetadataIdentity { TmdbId = tmdbId };

            if (item is Movie)
            {
                identity.Kind = "movie";
                identity.RelativePath = BuildIdPath("movie", tmdbId);
                return identity;
            }
            if (item is Series)
            {
                identity.Kind = "tv";
                identity.RelativePath = BuildIdPath("tv", tmdbId);
                return identity;
            }
            if (item is Person)
            {
                identity.Kind = "person";
                identity.RelativePath = BuildIdPath("person", tmdbId);
                return identity;
            }
            if (item is Season season)
            {
                identity.Kind = "season";
                identity.SeasonNumber = season.IndexNumber;
                identity.SeriesTmdbId = season.Series?.GetProviderId(MetadataProviders.Tmdb);
                identity.RelativePath = !string.IsNullOrWhiteSpace(tmdbId)
                    ? BuildIdPath("season", tmdbId)
                    : BuildNestedSeasonPath(identity.SeriesTmdbId, identity.SeasonNumber);
                return identity;
            }
            if (item is Episode episode)
            {
                identity.Kind = "episode";
                identity.SeasonNumber = episode.ParentIndexNumber;
                identity.EpisodeNumber = episode.IndexNumber;
                identity.SeriesTmdbId = episode.Series?.GetProviderId(MetadataProviders.Tmdb);
                identity.RelativePath = !string.IsNullOrWhiteSpace(tmdbId)
                    ? BuildIdPath("episode", tmdbId)
                    : BuildNestedEpisodePath(identity.SeriesTmdbId, identity.SeasonNumber, identity.EpisodeNumber);
                return identity;
            }

            return null;
        }

        public static LocalTmdbMetadataIdentity ResolveIdentityFromLookup(object[] args, string kind)
        {
            if (args == null) return null;
            object lookup = null;
            foreach (var arg in args)
            {
                if (arg == null) continue;
                if (arg is ItemLookupInfo)
                {
                    lookup = arg;
                    break;
                }
                try
                {
                    var candidate = arg.GetType().GetProperty("SearchInfo",
                        BindingFlags.Instance | BindingFlags.Public)?.GetValue(arg);
                    if (candidate is ItemLookupInfo)
                    {
                        lookup = candidate;
                        break;
                    }
                }
                catch { }
            }
            if (lookup == null) return null;

            var identity = new LocalTmdbMetadataIdentity { Kind = kind };
            identity.TmdbId = ReadProviderId(lookup, "ProviderIds", MetadataProviders.Tmdb.ToString());
            identity.SeriesTmdbId = ReadProviderId(lookup, "SeriesProviderIds", MetadataProviders.Tmdb.ToString());
            identity.SeasonNumber = ReadNullableInt(lookup, kind == "season" ? "IndexNumber" : "ParentIndexNumber");
            identity.EpisodeNumber = ReadNullableInt(lookup, "IndexNumber");

            switch (kind)
            {
                case "movie":
                case "tv":
                case "person":
                    identity.RelativePath = BuildIdPath(kind, identity.TmdbId);
                    break;
                case "season":
                    identity.RelativePath = !string.IsNullOrWhiteSpace(identity.TmdbId)
                        ? BuildIdPath("season", identity.TmdbId)
                        : BuildNestedSeasonPath(identity.SeriesTmdbId, identity.SeasonNumber);
                    break;
                case "episode":
                    identity.RelativePath = !string.IsNullOrWhiteSpace(identity.TmdbId)
                        ? BuildIdPath("episode", identity.TmdbId)
                        : BuildNestedEpisodePath(identity.SeriesTmdbId, identity.SeasonNumber, identity.EpisodeNumber);
                    break;
            }
            return identity;
        }

        private static string BuildIdPath(string kind, string id)
        {
            id = CleanSegment(id);
            return string.IsNullOrWhiteSpace(id) ? null : Path.Combine(kind, id + ".json");
        }

        private static string BuildNestedSeasonPath(string seriesId, int? season)
        {
            seriesId = CleanSegment(seriesId);
            return string.IsNullOrWhiteSpace(seriesId) || !season.HasValue
                ? null
                : Path.Combine("tv", seriesId, "season-" + season.Value + ".json");
        }

        private static string BuildNestedEpisodePath(string seriesId, int? season, int? episode)
        {
            seriesId = CleanSegment(seriesId);
            return string.IsNullOrWhiteSpace(seriesId) || !season.HasValue || !episode.HasValue
                ? null
                : Path.Combine("tv", seriesId, "season-" + season.Value,
                    "episode-" + episode.Value + ".json");
        }

        private static string CleanSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return new string(value.Trim().Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
        }

        private static string GetProviderId(BaseItem item, string key)
        {
            try { return item?.GetProviderId(key); } catch { return null; }
        }

        private static string ReadProviderId(object target, string propertyName, string key)
        {
            try
            {
                var value = target.GetType().GetProperty(propertyName,
                    BindingFlags.Instance | BindingFlags.Public)?.GetValue(target);
                if (value is IDictionary<string, string> typed && typed.TryGetValue(key, out var result))
                    return result;
                if (value is IDictionary dictionary)
                {
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (string.Equals(Convert.ToString(entry.Key), key, StringComparison.OrdinalIgnoreCase))
                            return Convert.ToString(entry.Value);
                    }
                }
            }
            catch { }
            return null;
        }

        private static int? ReadNullableInt(object target, string propertyName)
        {
            try
            {
                var value = target.GetType().GetProperty(propertyName,
                    BindingFlags.Instance | BindingFlags.Public)?.GetValue(target);
                if (value == null) return null;
                return Convert.ToInt32(value);
            }
            catch { return null; }
        }
    }
}
