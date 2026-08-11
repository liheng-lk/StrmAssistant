using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using Microsoft.International.Converters.TraditionalChineseToSimplifiedConverter;
using StrmAssistant.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class ChineseMetadataConversionCapabilityStatus
    {
        public bool MovieDbAssemblyLoaded { get; set; }
        public int CompatibleProvidersFound { get; set; }
        public int PatchedProviders { get; set; }
        public List<string> Targets { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    public static class ChineseMetadataConversionModState
    {
        public static ChineseMetadataConversionCapabilityStatus Status { get; internal set; } =
            new ChineseMetadataConversionCapabilityStatus();
    }

    public sealed class ChineseMetadataConversionRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.chinese-metadata-conversion";
        private Harmony _harmony;

        public void Run()
        {
            var status = new ChineseMetadataConversionCapabilityStatus();
            ChineseMetadataConversionModState.Status = status;

            try
            {
                var movieDb = Assembly.Load("MovieDb");
                status.MovieDbAssemblyLoaded = true;
                _harmony = new Harmony(HarmonyId);

                PatchProvider<Movie>(movieDb, "MovieDbMovieProvider",
                    nameof(ChineseMetadataConversionPatches.MovieMetadataPostfix), status);
                PatchProvider<Series>(movieDb, "MovieDbSeriesProvider",
                    nameof(ChineseMetadataConversionPatches.SeriesMetadataPostfix), status);
                PatchProvider<Season>(movieDb, "MovieDbSeasonProvider",
                    nameof(ChineseMetadataConversionPatches.SeasonMetadataPostfix), status);
                PatchProvider<Episode>(movieDb, "MovieDbEpisodeProvider",
                    nameof(ChineseMetadataConversionPatches.EpisodeMetadataPostfix), status);
                PatchProvider<Person>(movieDb, "MovieDbPersonProvider",
                    nameof(ChineseMetadataConversionPatches.PersonMetadataPostfix), status);
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Chinese metadata conversion runtime mod unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }

        private void PatchProvider<T>(Assembly movieDb, string typeName, string postfixName,
            ChineseMetadataConversionCapabilityStatus status) where T : BaseItem
        {
            var type = movieDb.GetTypes().FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.Ordinal));
            if (type == null) return;

            var targets = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => string.Equals(method.Name, "GetMetadata", StringComparison.Ordinal) &&
                                 method.ReturnType == typeof(Task<MetadataResult<T>>))
                .ToArray();
            status.CompatibleProvidersFound += targets.Length;
            if (targets.Length == 0) return;

            var postfix = typeof(ChineseMetadataConversionPatches).GetMethod(postfixName,
                BindingFlags.Static | BindingFlags.Public);
            foreach (var target in targets)
            {
                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                status.Targets.Add(type.FullName + "." + target);
                status.PatchedProviders++;
            }
        }
    }

    public static class ChineseMetadataConversionPatches
    {
        public static void MovieMetadataPostfix(object[] __args, ref Task<MetadataResult<Movie>> __result)
        {
            if (__result != null) __result = ConvertAsync(__result, __args, false);
        }

        public static void SeriesMetadataPostfix(object[] __args, ref Task<MetadataResult<Series>> __result)
        {
            if (__result != null) __result = ConvertAsync(__result, __args, false);
        }

        public static void SeasonMetadataPostfix(object[] __args, ref Task<MetadataResult<Season>> __result)
        {
            if (__result != null) __result = ConvertAsync(__result, __args, false);
        }

        public static void EpisodeMetadataPostfix(object[] __args, ref Task<MetadataResult<Episode>> __result)
        {
            if (__result != null) __result = ConvertAsync(__result, __args, false);
        }

        public static void PersonMetadataPostfix(object[] __args, ref Task<MetadataResult<Person>> __result)
        {
            if (__result != null) __result = ConvertAsync(__result, __args, true);
        }

        private static async Task<MetadataResult<T>> ConvertAsync<T>(Task<MetadataResult<T>> source,
            object[] args, bool person) where T : BaseItem
        {
            var result = await source.ConfigureAwait(false);
            try
            {
                var settings = ChineseMetadataConversionRuntimeSettings.GetSnapshot();
                if (!settings.Enabled || result?.HasMetadata != true || result.Item == null) return result;

                var language = GetMetadataLanguage(args);
                if (settings.OnlyForSimplifiedChineseRequests && !IsSimplifiedChineseRequest(language))
                    return result;

                var item = result.Item;
                if (settings.ConvertName && (!person || settings.ConvertPersonName))
                    item.Name = Convert(item.Name);
                if (settings.ConvertOverview)
                    item.Overview = Convert(item.Overview);
                if (settings.ConvertTagline)
                    ConvertStringProperty(item, "Tagline");

                return result;
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Chinese metadata conversion skipped: " + ex.Message);
                return result;
            }
        }

        private static string GetMetadataLanguage(object[] args)
        {
            if (args == null) return null;
            foreach (var arg in args)
            {
                if (arg is ItemLookupInfo direct) return direct.MetadataLanguage;
                if (arg == null) continue;
                try
                {
                    var searchInfo = arg.GetType().GetProperty("SearchInfo", BindingFlags.Instance | BindingFlags.Public)
                        ?.GetValue(arg);
                    if (searchInfo is ItemLookupInfo lookup) return lookup.MetadataLanguage;
                }
                catch { }
            }
            return null;
        }

        private static bool IsSimplifiedChineseRequest(string language)
        {
            if (string.IsNullOrWhiteSpace(language)) return false;
            var normalized = language.Replace('_', '-').ToLowerInvariant();
            return normalized == "zh-cn" || normalized == "zh-sg" || normalized == "zh-hans" ||
                   normalized.StartsWith("zh-hans-", StringComparison.Ordinal);
        }

        private static string Convert(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            try
            {
                return ChineseConverter.Convert(value, ChineseConversionDirection.TraditionalToSimplified);
            }
            catch
            {
                return value;
            }
        }

        private static void ConvertStringProperty(object item, string propertyName)
        {
            var property = item?.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property?.CanRead != true || property.CanWrite != true || property.PropertyType != typeof(string)) return;
            var current = property.GetValue(item) as string;
            property.SetValue(item, Convert(current));
        }
    }
}
