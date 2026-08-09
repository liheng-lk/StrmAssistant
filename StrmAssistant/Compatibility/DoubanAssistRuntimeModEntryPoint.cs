using HarmonyLib;
using MediaBrowser.Common.Net;
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
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class DoubanAssistCapabilityStatus
    {
        public bool MovieDbAssemblyLoaded { get; set; }
        public int CompatibleProvidersFound { get; set; }
        public int PatchedProviders { get; set; }
        public List<string> Targets { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    public static class DoubanAssistModState
    {
        public static DoubanAssistCapabilityStatus Status { get; internal set; } =
            new DoubanAssistCapabilityStatus();
    }

    public sealed class DoubanAssistRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.douban-assist";
        private readonly DoubanAssistBridge _bridge;
        private Harmony _harmony;

        public DoubanAssistRuntimeModEntryPoint(IHttpClient httpClient, IJsonSerializer jsonSerializer)
        {
            _bridge = new DoubanAssistBridge(httpClient, jsonSerializer);
        }

        public void Run()
        {
            var status = new DoubanAssistCapabilityStatus();
            DoubanAssistModState.Status = status;
            DoubanAssistPatches.Bridge = _bridge;

            try
            {
                var movieDb = Assembly.Load("MovieDb");
                status.MovieDbAssemblyLoaded = true;
                _harmony = new Harmony(HarmonyId);
                PatchProvider<Movie>(movieDb, "MovieDbMovieProvider",
                    nameof(DoubanAssistPatches.MovieMetadataPostfix), status);
                PatchProvider<Series>(movieDb, "MovieDbSeriesProvider",
                    nameof(DoubanAssistPatches.SeriesMetadataPostfix), status);
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Douban Assist runtime mod unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }

        private void PatchProvider<T>(Assembly movieDb, string typeName, string postfixName,
            DoubanAssistCapabilityStatus status) where T : BaseItem, new()
        {
            var type = movieDb.GetTypes().FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.Ordinal));
            if (type == null) return;
            var targets = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => string.Equals(method.Name, "GetMetadata", StringComparison.Ordinal) &&
                                 method.ReturnType == typeof(Task<MetadataResult<T>>))
                .ToArray();
            status.CompatibleProvidersFound += targets.Length;
            var postfix = typeof(DoubanAssistPatches).GetMethod(postfixName,
                BindingFlags.Static | BindingFlags.Public);
            foreach (var target in targets)
            {
                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                status.PatchedProviders++;
                status.Targets.Add(type.FullName + "." + target);
            }
        }
    }

    public static class DoubanAssistPatches
    {
        internal static DoubanAssistBridge Bridge { get; set; }

        public static void MovieMetadataPostfix(object[] __args, ref Task<MetadataResult<Movie>> __result)
        {
            __result = ApplyAsync(__result, __args, "movie");
        }

        public static void SeriesMetadataPostfix(object[] __args, ref Task<MetadataResult<Series>> __result)
        {
            __result = ApplyAsync(__result, __args, "tv");
        }

        private static async Task<MetadataResult<T>> ApplyAsync<T>(Task<MetadataResult<T>> source,
            object[] args, string type) where T : BaseItem, new()
        {
            var options = DoubanAssistRuntimeSettings.GetSnapshot();
            if (!options.Enabled || Bridge == null ||
                type == "movie" && !options.EnableMovies || type == "tv" && !options.EnableSeries)
                return source == null ? new MetadataResult<T>() : await source.ConfigureAwait(false);

            MetadataResult<T> result;
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

            var identity = result.Item != null ? Bridge.ResolveIdentity(result.Item) : null;
            if (identity == null ||
                string.IsNullOrWhiteSpace(identity.TmdbId) && string.IsNullOrWhiteSpace(identity.ImdbId))
                identity = DoubanAssistBridge.ResolveIdentityFromLookup(args, type);

            var document = await Bridge.FetchAsync(identity, CancellationToken.None).ConfigureAwait(false);
            if (document == null)
            {
                if (remoteError != null) throw remoteError;
                return result;
            }

            if (result.Item == null) result.Item = new T();
            ApplyDocument(result.Item, document, options.OnlyFillMissingFields);
            result.HasMetadata = true;
            if (remoteError != null && Plugin.Instance?.DebugMode == true)
                Plugin.Instance.Logger.Debug("Douban Assist recovered metadata after MovieDb failure: " + remoteError.Message);
            return result;
        }

        private static void ApplyDocument(BaseItem item, DoubanAssistDocument document, bool onlyFillMissing)
        {
            if (ShouldSet(item.Name, document.Name, onlyFillMissing)) item.Name = document.Name;
            if (ShouldSet(item.OriginalTitle, document.OriginalTitle, onlyFillMissing))
                item.OriginalTitle = document.OriginalTitle;
            if (ShouldSet(item.Overview, document.Overview, onlyFillMissing)) item.Overview = document.Overview;
            SetStringProperty(item, "Tagline", document.Tagline, onlyFillMissing);
            SetValueProperty(item, "ProductionYear", document.ProductionYear, onlyFillMissing);
            SetPremiereDate(item, document.PremiereDate, onlyFillMissing);
            SetStringCollectionProperty(item, "Genres", document.Genres, onlyFillMissing);
            MergeProviderIds(item, document.ProviderIds, onlyFillMissing);
            if (!string.IsNullOrWhiteSpace(document.DoubanId))
                MergeProviderIds(item, new Dictionary<string, string> { ["Douban"] = document.DoubanId }, onlyFillMissing);
        }

        private static bool ShouldSet(string current, string incoming, bool onlyFillMissing)
        {
            return !string.IsNullOrWhiteSpace(incoming) && (!onlyFillMissing || string.IsNullOrWhiteSpace(current));
        }

        private static void SetStringProperty(object target, string propertyName, string incoming, bool onlyFillMissing)
        {
            if (string.IsNullOrWhiteSpace(incoming)) return;
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
            if (incoming == null) return;
            try
            {
                var property = target.GetType().GetProperty(propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.CanRead != true || property.CanWrite != true) return;
                var current = property.GetValue(target);
                if (onlyFillMissing && !IsDefault(current)) return;
                var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                property.SetValue(target, Convert.ChangeType(incoming, type));
            }
            catch { }
        }

        private static void SetPremiereDate(object target, string incoming, bool onlyFillMissing)
        {
            if (string.IsNullOrWhiteSpace(incoming) || !DateTimeOffset.TryParse(incoming, out var parsed)) return;
            try
            {
                var property = target.GetType().GetProperty("PremiereDate",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.CanRead != true || property.CanWrite != true) return;
                var current = property.GetValue(target);
                if (onlyFillMissing && !IsDefault(current)) return;
                var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (type == typeof(DateTimeOffset)) property.SetValue(target, parsed);
                else if (type == typeof(DateTime)) property.SetValue(target, parsed.UtcDateTime);
            }
            catch { }
        }

        private static void SetStringCollectionProperty(object target, string propertyName,
            IEnumerable<string> incoming, bool onlyFillMissing)
        {
            var clean = incoming?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (clean == null || clean.Count == 0) return;
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
            if (incoming == null || incoming.Count == 0) return;
            try
            {
                var property = item.GetType().GetProperty("ProviderIds",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var dictionary = property?.GetValue(item) as IDictionary<string, string>;
                if (dictionary == null) return;
                foreach (var pair in incoming)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
                    if (onlyFillMissing && dictionary.TryGetValue(pair.Key, out var current) &&
                        !string.IsNullOrWhiteSpace(current)) continue;
                    dictionary[pair.Key] = pair.Value;
                }
            }
            catch { }
        }

        private static bool IsDefault(object value)
        {
            if (value == null) return true;
            var type = value.GetType();
            return type.IsValueType && value.Equals(Activator.CreateInstance(type));
        }
    }
}
