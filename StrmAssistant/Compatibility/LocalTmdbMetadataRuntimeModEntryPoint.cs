using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Metadata;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class LocalTmdbMetadataCapabilityStatus
    {
        public bool MovieDbAssemblyLoaded { get; set; }
        public int CompatibleProvidersFound { get; set; }
        public int PatchedProviders { get; set; }
        public List<string> Targets { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    public static class LocalTmdbMetadataModState
    {
        public static LocalTmdbMetadataCapabilityStatus Status { get; internal set; } =
            new LocalTmdbMetadataCapabilityStatus();
    }

    public sealed class LocalTmdbMetadataRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.local-tmdb";
        private readonly LocalTmdbMetadataStore _store;
        private Harmony _harmony;

        public LocalTmdbMetadataRuntimeModEntryPoint(IJsonSerializer jsonSerializer)
        {
            _store = new LocalTmdbMetadataStore(jsonSerializer);
        }

        public void Run()
        {
            var status = new LocalTmdbMetadataCapabilityStatus();
            LocalTmdbMetadataModState.Status = status;
            LocalTmdbMetadataPatches.Store = _store;

            try
            {
                var movieDb = Assembly.Load("MovieDb");
                status.MovieDbAssemblyLoaded = true;
                _harmony = new Harmony(HarmonyId);

                PatchProvider<Movie>(movieDb, "MovieDbMovieProvider", "movie",
                    nameof(LocalTmdbMetadataPatches.MovieMetadataPostfix), status);
                PatchProvider<Series>(movieDb, "MovieDbSeriesProvider", "tv",
                    nameof(LocalTmdbMetadataPatches.SeriesMetadataPostfix), status);
                PatchProvider<Season>(movieDb, "MovieDbSeasonProvider", "season",
                    nameof(LocalTmdbMetadataPatches.SeasonMetadataPostfix), status);
                PatchProvider<Episode>(movieDb, "MovieDbEpisodeProvider", "episode",
                    nameof(LocalTmdbMetadataPatches.EpisodeMetadataPostfix), status);
                PatchProvider<Person>(movieDb, "MovieDbPersonProvider", "person",
                    nameof(LocalTmdbMetadataPatches.PersonMetadataPostfix), status);
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Local TMDB runtime mod unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }

        private void PatchProvider<T>(Assembly movieDb, string typeName, string kind, string postfixName,
            LocalTmdbMetadataCapabilityStatus status) where T : BaseItem, new()
        {
            var type = movieDb.GetTypes().FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.Ordinal));
            if (type == null) return;
            var targets = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => string.Equals(method.Name, "GetMetadata", StringComparison.Ordinal) &&
                                 method.ReturnType == typeof(Task<MetadataResult<T>>))
                .ToArray();
            status.CompatibleProvidersFound += targets.Length;
            if (targets.Length == 0) return;

            var postfix = typeof(LocalTmdbMetadataPatches).GetMethod(postfixName,
                BindingFlags.Static | BindingFlags.Public);
            foreach (var target in targets)
            {
                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                status.Targets.Add(kind + ": " + type.FullName + "." + target);
                status.PatchedProviders++;
            }
        }
    }

    public static class LocalTmdbMetadataPatches
    {
        internal static LocalTmdbMetadataStore Store { get; set; }

        public static void MovieMetadataPostfix(object[] __args, ref Task<MetadataResult<Movie>> __result)
        {
            __result = ApplyAsync(__result, __args, "movie");
        }

        public static void SeriesMetadataPostfix(object[] __args, ref Task<MetadataResult<Series>> __result)
        {
            __result = ApplyAsync(__result, __args, "tv");
        }

        public static void SeasonMetadataPostfix(object[] __args, ref Task<MetadataResult<Season>> __result)
        {
            __result = ApplyAsync(__result, __args, "season");
        }

        public static void EpisodeMetadataPostfix(object[] __args, ref Task<MetadataResult<Episode>> __result)
        {
            __result = ApplyAsync(__result, __args, "episode");
        }

        public static void PersonMetadataPostfix(object[] __args, ref Task<MetadataResult<Person>> __result)
        {
            __result = ApplyAsync(__result, __args, "person");
        }

        private static async Task<MetadataResult<T>> ApplyAsync<T>(Task<MetadataResult<T>> source,
            object[] args, string kind) where T : BaseItem, new()
        {
            var options = LocalTmdbMetadataRuntimeSettings.GetSnapshot();
            if (!options.Enabled || Store == null || !IsKindEnabled(options, kind))
                return source == null ? new MetadataResult<T>() : await source.ConfigureAwait(false);

            MetadataResult<T> result = null;
            Exception remoteError = null;
            try
            {
                result = source == null ? new MetadataResult<T>() : await source.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                remoteError = ex;
                result = new MetadataResult<T>();
            }

            var identity = result?.Item != null ? Store.ResolveIdentity(result.Item) : null;
            if (identity == null || string.IsNullOrWhiteSpace(identity.RelativePath))
                identity = LocalTmdbMetadataStore.ResolveIdentityFromLookup(args, kind);

            if (!Store.TryRead(identity, out var document, out var fullPath, out var localError))
            {
                if (remoteError != null) throw remoteError;
                if (!string.IsNullOrWhiteSpace(localError) && Plugin.Instance?.DebugMode == true &&
                    localError.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) < 0)
                    Plugin.Instance.Logger.Debug("Local TMDB skipped: " + localError);
                return result;
            }

            if (result == null) result = new MetadataResult<T>();
            if (result.Item == null) result.Item = new T();
            ApplyDocument(result.Item, document, options.OnlyFillMissingFields);
            result.HasMetadata = true;

            if (Plugin.Instance?.DebugMode == true)
            {
                Plugin.Instance.Logger.Debug("Local TMDB metadata applied from {0}{1}", fullPath,
                    remoteError == null ? string.Empty : " after remote provider failure: " + remoteError.Message);
            }
            return result;
        }

        private static bool IsKindEnabled(LocalTmdbMetadataOptions options, string kind)
        {
            switch (kind)
            {
                case "movie": return options.EnableMovies;
                case "tv": return options.EnableSeries;
                case "season": return options.EnableSeasons;
                case "episode": return options.EnableEpisodes;
                case "person": return options.EnablePeople;
                default: return false;
            }
        }

        private static void ApplyDocument(BaseItem item, LocalTmdbMetadataDocument document, bool onlyFillMissing)
        {
            if (item == null || document == null) return;
            if (ShouldSet(item.Name, document.Name, onlyFillMissing)) item.Name = document.Name;
            if (ShouldSet(item.OriginalTitle, document.OriginalTitle, onlyFillMissing))
                item.OriginalTitle = document.OriginalTitle;
            if (ShouldSet(item.Overview, document.Overview, onlyFillMissing)) item.Overview = document.Overview;

            SetStringProperty(item, "Tagline", document.Tagline, onlyFillMissing);
            SetValueProperty(item, "ProductionYear", document.ProductionYear, onlyFillMissing);
            SetPremiereDate(item, document.PremiereDate, onlyFillMissing);
            SetStringCollectionProperty(item, "Genres", document.Genres, onlyFillMissing);
            MergeProviderIds(item, document.ProviderIds, onlyFillMissing);
        }

        private static bool ShouldSet(string current, string incoming, bool onlyFillMissing)
        {
            return !string.IsNullOrWhiteSpace(incoming) && (!onlyFillMissing || string.IsNullOrWhiteSpace(current));
        }

        private static void SetStringProperty(object target, string propertyName, string incoming, bool onlyFillMissing)
        {
            if (string.IsNullOrWhiteSpace(incoming) || target == null) return;
            try
            {
                var property = target.GetType().GetProperty(propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.CanRead != true || property.CanWrite != true || property.PropertyType != typeof(string)) return;
                var current = property.GetValue(target) as string;
                if (!onlyFillMissing || string.IsNullOrWhiteSpace(current)) property.SetValue(target, incoming);
            }
            catch { }
        }

        private static void SetValueProperty(object target, string propertyName, object incoming, bool onlyFillMissing)
        {
            if (incoming == null || target == null) return;
            try
            {
                var property = target.GetType().GetProperty(propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.CanRead != true || property.CanWrite != true) return;
                var current = property.GetValue(target);
                if (onlyFillMissing && !IsDefault(current)) return;
                var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                property.SetValue(target, Convert.ChangeType(incoming, targetType));
            }
            catch { }
        }

        private static void SetPremiereDate(object target, string incoming, bool onlyFillMissing)
        {
            if (string.IsNullOrWhiteSpace(incoming) || target == null ||
                !DateTimeOffset.TryParse(incoming, out var parsed)) return;
            try
            {
                var property = target.GetType().GetProperty("PremiereDate",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.CanRead != true || property.CanWrite != true) return;
                var current = property.GetValue(target);
                if (onlyFillMissing && !IsDefault(current)) return;

                var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (targetType == typeof(DateTimeOffset)) property.SetValue(target, parsed);
                else if (targetType == typeof(DateTime)) property.SetValue(target, parsed.UtcDateTime);
            }
            catch { }
        }

        private static void SetStringCollectionProperty(object target, string propertyName,
            IEnumerable<string> incoming, bool onlyFillMissing)
        {
            var clean = incoming?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (clean == null || clean.Count == 0 || target == null) return;
            try
            {
                var property = target.GetType().GetProperty(propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.CanRead != true || property.CanWrite != true) return;
                var current = property.GetValue(target) as IEnumerable;
                if (onlyFillMissing && current != null && current.Cast<object>().Any()) return;

                if (property.PropertyType == typeof(string[])) property.SetValue(target, clean.ToArray());
                else if (property.PropertyType.IsAssignableFrom(typeof(List<string>))) property.SetValue(target, clean);
            }
            catch { }
        }

        private static void MergeProviderIds(BaseItem item, IDictionary<string, string> incoming, bool onlyFillMissing)
        {
            if (item == null || incoming == null || incoming.Count == 0) return;
            try
            {
                var property = item.GetType().GetProperty("ProviderIds",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var dictionary = property?.GetValue(item) as IDictionary<string, string>;
                if (dictionary == null) return;
                foreach (var pair in incoming)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
                    if (onlyFillMissing && dictionary.ContainsKey(pair.Key) &&
                        !string.IsNullOrWhiteSpace(dictionary[pair.Key])) continue;
                    dictionary[pair.Key] = pair.Value;
                }
            }
            catch { }
        }

        private static bool IsDefault(object value)
        {
            if (value == null) return true;
            var type = value.GetType();
            if (!type.IsValueType) return false;
            return value.Equals(Activator.CreateInstance(type));
        }
    }
}
