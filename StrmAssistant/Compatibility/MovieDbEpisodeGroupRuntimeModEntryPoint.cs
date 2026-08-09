using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using StrmAssistant.Provider;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class MovieDbEpisodeGroupCapabilityStatus
    {
        public bool MovieDbAssemblyLoaded { get; set; }
        public bool SeasonMetadataPatched { get; set; }
        public bool EpisodeMetadataPatched { get; set; }
        public bool SeasonImagesPatched { get; set; }
        public bool EpisodeImagesPatched { get; set; }
        public bool LocalSeriesContextPatched { get; set; }
        public List<string> Targets { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    public static class MovieDbEpisodeGroupModState
    {
        public static MovieDbEpisodeGroupCapabilityStatus Status { get; internal set; } =
            new MovieDbEpisodeGroupCapabilityStatus();
    }

    public sealed class MovieDbEpisodeGroupRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.moviedb-episode-group";
        private Harmony _harmony;

        public void Run()
        {
            var status = new MovieDbEpisodeGroupCapabilityStatus();
            MovieDbEpisodeGroupModState.Status = status;

            try
            {
                var movieDb = Assembly.Load("MovieDb");
                status.MovieDbAssemblyLoaded = true;
                _harmony = new Harmony(HarmonyId);

                status.SeasonMetadataPatched = PatchMetadata<Season>(movieDb,
                    "MovieDb.Providers.MovieDbSeasonProvider",
                    nameof(MovieDbEpisodeGroupPatches.SeasonMetadataPrefix),
                    nameof(MovieDbEpisodeGroupPatches.SeasonMetadataPostfix), status);

                status.EpisodeMetadataPatched = PatchMetadata<Episode>(movieDb,
                    "MovieDb.Providers.MovieDbEpisodeProvider",
                    nameof(MovieDbEpisodeGroupPatches.EpisodeMetadataPrefix),
                    nameof(MovieDbEpisodeGroupPatches.EpisodeMetadataPostfix), status);

                status.SeasonImagesPatched = PatchImages(movieDb,
                    "MovieDb.Providers.MovieDbSeasonImageProvider", typeof(Season),
                    nameof(MovieDbEpisodeGroupPatches.SeasonImagesPrefix),
                    nameof(MovieDbEpisodeGroupPatches.SeasonImagesPostfix), status);

                status.EpisodeImagesPatched = PatchImages(movieDb,
                    "MovieDb.Providers.MovieDbEpisodeImageProvider", typeof(Episode),
                    nameof(MovieDbEpisodeGroupPatches.EpisodeImagesPrefix),
                    nameof(MovieDbEpisodeGroupPatches.EpisodeImagesPostfix), status);

                status.LocalSeriesContextPatched = PatchProviderManagerContext(status);
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("MovieDb Episode Group runtime mod unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }

        private bool PatchMetadata<T>(Assembly movieDb, string typeName, string prefixName,
            string postfixName, MovieDbEpisodeGroupCapabilityStatus status) where T : BaseItem
        {
            var type = movieDb.GetType(typeName);
            var target = type?.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => string.Equals(m.Name, "GetMetadata", StringComparison.Ordinal) &&
                    m.ReturnType == typeof(Task<MetadataResult<T>>));
            if (target == null) return false;

            var prefix = typeof(MovieDbEpisodeGroupPatches).GetMethod(prefixName,
                BindingFlags.Static | BindingFlags.Public);
            var postfix = typeof(MovieDbEpisodeGroupPatches).GetMethod(postfixName,
                BindingFlags.Static | BindingFlags.Public);
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
            status.Targets.Add(target.ToString());
            return true;
        }

        private bool PatchImages(Assembly movieDb, string typeName, Type itemType, string prefixName,
            string postfixName, MovieDbEpisodeGroupCapabilityStatus status)
        {
            var type = movieDb.GetType(typeName);
            var target = type?.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => string.Equals(m.Name, "GetImages", StringComparison.Ordinal) &&
                    m.ReturnType == typeof(Task<IEnumerable<RemoteImageInfo>>) &&
                    m.GetParameters().Any(p => p.ParameterType == itemType));
            if (target == null) return false;

            var prefix = typeof(MovieDbEpisodeGroupPatches).GetMethod(prefixName,
                BindingFlags.Static | BindingFlags.Public);
            var postfix = typeof(MovieDbEpisodeGroupPatches).GetMethod(postfixName,
                BindingFlags.Static | BindingFlags.Public);
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
            status.Targets.Add(target.ToString());
            return true;
        }

        private bool PatchProviderManagerContext(MovieDbEpisodeGroupCapabilityStatus status)
        {
            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var type = embyProviders.GetType("Emby.Providers.Manager.ProviderManager");
                var target = type?.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .FirstOrDefault(m => string.Equals(m.Name, "CanRefresh", StringComparison.Ordinal) &&
                        m.GetParameters().Any(p => typeof(IMetadataProvider).IsAssignableFrom(p.ParameterType)) &&
                        m.GetParameters().Any(p => typeof(BaseItem).IsAssignableFrom(p.ParameterType)));
                if (target == null) return false;

                var prefix = typeof(MovieDbEpisodeGroupPatches).GetMethod(
                    nameof(MovieDbEpisodeGroupPatches.ProviderCanRefreshPrefix),
                    BindingFlags.Static | BindingFlags.Public);
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                status.Targets.Add(target.ToString());
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static class MovieDbEpisodeGroupPatches
    {
        private static readonly AsyncLocal<Series> CurrentSeries = new AsyncLocal<Series>();

        public static void ProviderCanRefreshPrefix(object[] __args)
        {
            try
            {
                if (Plugin.Instance?.GetPluginOptions()?.MetadataEnhanceOptions?.LocalEpisodeGroup != true ||
                    __args == null) return;

                var provider = __args.OfType<IMetadataProvider>().FirstOrDefault();
                var series = __args.OfType<Series>().FirstOrDefault();
                if (series == null || provider == null) return;

                var providerName = provider.GetType().Name;
                if (providerName == "MovieDbSeriesProvider" || providerName == "MovieDbSeasonProvider" ||
                    providerName == "MovieDbEpisodeProvider")
                    CurrentSeries.Value = series;
            }
            catch { }
        }

        public static void SeasonMetadataPrefix(object[] __args, out SeasonState __state)
        {
            __state = null;
            try
            {
                if (!Enabled() || __args == null) return;
                var search = GetLookupInfo<SeasonInfo>(__args);
                if (search == null || !search.IndexNumber.HasValue || search.IndexNumber.Value < 1) return;

                var response = FetchGroup(search.SeriesProviderIds, search.MetadataLanguage,
                    GetCancellationToken(__args));
                var group = response?.groups?.FirstOrDefault(g => g.order == search.IndexNumber.Value - 1);
                if (group == null) return;

                __state = new SeasonState { GroupName = group.name, GroupIndex = search.IndexNumber.Value };
            }
            catch (Exception ex)
            {
                Debug("Season metadata episode-group mapping skipped", ex);
            }
        }

        public static void SeasonMetadataPostfix(SeasonState __state, ref Task<MetadataResult<Season>> __result)
        {
            if (__state == null || __result == null) return;
            __result = ApplySeasonMetadataAsync(__result, __state);
        }

        public static void EpisodeMetadataPrefix(object[] __args, out EpisodeState __state)
        {
            __state = null;
            try
            {
                if (!Enabled() || __args == null) return;
                var search = GetLookupInfo<EpisodeInfo>(__args);
                if (search == null || !search.ParentIndexNumber.HasValue || !search.IndexNumber.HasValue ||
                    search.ParentIndexNumber.Value < 1 || search.IndexNumber.Value < 1) return;

                var response = FetchGroup(search.SeriesProviderIds, search.MetadataLanguage,
                    GetCancellationToken(__args));
                var group = response?.groups?.FirstOrDefault(g => g.order == search.ParentIndexNumber.Value - 1);
                var mapped = group?.episodes?.FirstOrDefault(e => e.order == search.IndexNumber.Value - 1);
                if (mapped == null) return;

                __state = new EpisodeState
                {
                    DisplaySeason = search.ParentIndexNumber.Value,
                    DisplayEpisode = search.IndexNumber.Value,
                    OriginalSeason = mapped.season_number,
                    OriginalEpisode = mapped.episode_number
                };
                search.ParentIndexNumber = mapped.season_number;
                search.IndexNumber = mapped.episode_number;
            }
            catch (Exception ex)
            {
                Debug("Episode metadata episode-group mapping skipped", ex);
            }
        }

        public static void EpisodeMetadataPostfix(EpisodeState __state, ref Task<MetadataResult<Episode>> __result)
        {
            if (__state == null || __result == null) return;
            __result = RestoreEpisodeMetadataAsync(__result, __state);
        }

        public static void SeasonImagesPrefix(object[] __args, out SeasonImageState __state)
        {
            __state = null;
            try
            {
                if (!Enabled() || __args == null) return;
                var season = __args.OfType<Season>().FirstOrDefault();
                if (season == null || !season.IndexNumber.HasValue || season.IndexNumber.Value < 1) return;

                var response = FetchGroup(season.Series?.ProviderIds, null, GetCancellationToken(__args));
                var group = response?.groups?.FirstOrDefault(g => g.order == season.IndexNumber.Value - 1);
                if (group?.episodes == null || group.episodes.Count == 0) return;

                var representativeSeason = group.episodes.GroupBy(e => e.season_number)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault();
                if (representativeSeason < 1 || representativeSeason == season.IndexNumber.Value) return;

                __state = new SeasonImageState { DisplaySeason = season.IndexNumber.Value };
                season.IndexNumber = representativeSeason;
            }
            catch (Exception ex)
            {
                Debug("Season image episode-group mapping skipped", ex);
            }
        }

        public static void SeasonImagesPostfix(object[] __args, SeasonImageState __state,
            ref Task<IEnumerable<RemoteImageInfo>> __result)
        {
            if (__state == null || __args == null) return;
            var season = __args.OfType<Season>().FirstOrDefault();
            if (season != null) season.IndexNumber = __state.DisplaySeason;
        }

        public static void EpisodeImagesPrefix(object[] __args, out EpisodeImageState __state)
        {
            __state = null;
            try
            {
                if (!Enabled() || __args == null) return;
                var episode = __args.OfType<Episode>().FirstOrDefault();
                if (episode == null || !episode.ParentIndexNumber.HasValue || !episode.IndexNumber.HasValue ||
                    episode.ParentIndexNumber.Value < 1 || episode.IndexNumber.Value < 1) return;

                var response = FetchGroup(episode.Series?.ProviderIds, null, GetCancellationToken(__args));
                var group = response?.groups?.FirstOrDefault(g => g.order == episode.ParentIndexNumber.Value - 1);
                var mapped = group?.episodes?.FirstOrDefault(e => e.order == episode.IndexNumber.Value - 1);
                if (mapped == null) return;

                __state = new EpisodeImageState
                {
                    DisplaySeason = episode.ParentIndexNumber.Value,
                    DisplayEpisode = episode.IndexNumber.Value
                };
                episode.ParentIndexNumber = mapped.season_number;
                episode.IndexNumber = mapped.episode_number;
            }
            catch (Exception ex)
            {
                Debug("Episode image episode-group mapping skipped", ex);
            }
        }

        public static void EpisodeImagesPostfix(object[] __args, EpisodeImageState __state,
            ref Task<IEnumerable<RemoteImageInfo>> __result)
        {
            if (__state == null || __args == null) return;
            var episode = __args.OfType<Episode>().FirstOrDefault();
            if (episode == null) return;
            episode.ParentIndexNumber = __state.DisplaySeason;
            episode.IndexNumber = __state.DisplayEpisode;
        }

        private static bool Enabled()
        {
            var options = Plugin.Instance?.GetPluginOptions()?.MetadataEnhanceOptions;
            return options?.MovieDbEpisodeGroup == true || options?.LocalEpisodeGroup == true;
        }

        private static EpisodeGroupResponse FetchGroup(Dictionary<string, string> providerIds, string language,
            CancellationToken cancellationToken)
        {
            try
            {
                var options = Plugin.Instance?.GetPluginOptions()?.MetadataEnhanceOptions;
                var currentSeries = CurrentSeries.Value;

                if (options?.LocalEpisodeGroup == true && currentSeries != null)
                {
                    var localPath = Plugin.MetadataApi.GetEpisodeGroupLocalPath(currentSeries);
                    var local = Plugin.MetadataApi.FetchLocalEpisodeGroup(localPath).GetAwaiter().GetResult();
                    if (local?.groups != null && local.groups.Count > 0) return local;
                }

                if (options?.MovieDbEpisodeGroup != true || providerIds == null ||
                    !providerIds.TryGetValue(MetadataProviders.Tmdb.ToString(), out var tmdbId) ||
                    !providerIds.TryGetValue(MovieDbEpisodeGroupExternalId.StaticName, out var episodeGroupId) ||
                    string.IsNullOrWhiteSpace(tmdbId) || string.IsNullOrWhiteSpace(episodeGroupId))
                    return null;

                var localSavePath = options.LocalEpisodeGroup == true && currentSeries != null
                    ? Plugin.MetadataApi.GetEpisodeGroupLocalPath(currentSeries)
                    : null;
                return Plugin.MetadataApi.FetchOnlineEpisodeGroup(tmdbId, episodeGroupId, language,
                        localSavePath, cancellationToken)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug("Episode group fetch failed", ex);
                return null;
            }
        }

        private static T GetLookupInfo<T>(object[] args) where T : ItemLookupInfo
        {
            foreach (var arg in args)
            {
                if (arg is T direct) return direct;
                if (arg == null) continue;
                var searchInfo = arg.GetType().GetProperty("SearchInfo", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(arg);
                if (searchInfo is T typed) return typed;
            }
            return null;
        }

        private static CancellationToken GetCancellationToken(object[] args)
        {
            return args?.OfType<CancellationToken>().FirstOrDefault() ?? CancellationToken.None;
        }

        private static async Task<MetadataResult<Season>> ApplySeasonMetadataAsync(
            Task<MetadataResult<Season>> source, SeasonState state)
        {
            var result = await source.ConfigureAwait(false);
            if (result?.HasMetadata == true && result.Item != null && !string.IsNullOrWhiteSpace(state.GroupName))
                result.Item.Name = state.GroupName;
            return result;
        }

        private static async Task<MetadataResult<Episode>> RestoreEpisodeMetadataAsync(
            Task<MetadataResult<Episode>> source, EpisodeState state)
        {
            var result = await source.ConfigureAwait(false);
            if (result?.HasMetadata == true && result.Item != null)
            {
                result.Item.ParentIndexNumber = state.DisplaySeason;
                result.Item.IndexNumber = state.DisplayEpisode;
            }
            return result;
        }

        private static void Debug(string prefix, Exception ex)
        {
            if (Plugin.Instance?.DebugMode == true)
                Plugin.Instance.Logger.Debug(prefix + ": " + ex.Message);
        }

        public sealed class SeasonState
        {
            public string GroupName { get; set; }
            public int GroupIndex { get; set; }
        }

        public sealed class EpisodeState
        {
            public int DisplaySeason { get; set; }
            public int DisplayEpisode { get; set; }
            public int OriginalSeason { get; set; }
            public int OriginalEpisode { get; set; }
        }

        public sealed class SeasonImageState
        {
            public int DisplaySeason { get; set; }
        }

        public sealed class EpisodeImageState
        {
            public int DisplaySeason { get; set; }
            public int DisplayEpisode { get; set; }
        }
    }
}
