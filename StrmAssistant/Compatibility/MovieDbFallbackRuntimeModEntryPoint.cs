using HarmonyLib;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class MovieDbFallbackCapabilityStatus
    {
        public bool MovieDbAssemblyLoaded { get; set; }
        public string MovieDbAssemblyVersion { get; set; }
        public bool MetadataLanguagesTargetFound { get; set; }
        public bool MetadataLanguagesPatched { get; set; }
        public string MetadataLanguagesTarget { get; set; }
        public bool LanguageMapperFound { get; set; }
        public string LanguageMapperTarget { get; set; }
        public bool ImageLanguagesTargetFound { get; set; }
        public bool ImageLanguagesPatched { get; set; }
        public string ImageLanguagesTarget { get; set; }
        public string Error { get; set; }
    }

    public static class MovieDbFallbackModState
    {
        public static MovieDbFallbackCapabilityStatus Status { get; internal set; } =
            new MovieDbFallbackCapabilityStatus();
    }

    /// <summary>
    /// Extends MovieDb's own language chain without replacing providers or changing cached metadata.
    /// Every patch reads live options and becomes a no-op when disabled.
    /// </summary>
    public sealed class MovieDbFallbackRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.moviedb-fallback";
        private Harmony _harmony;

        public void Run()
        {
            var status = new MovieDbFallbackCapabilityStatus();
            MovieDbFallbackModState.Status = status;

            try
            {
                Assembly movieDbAssembly;
                try
                {
                    movieDbAssembly = Assembly.Load("MovieDb");
                }
                catch
                {
                    movieDbAssembly = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => string.Equals(a.GetName().Name, "MovieDb", StringComparison.Ordinal));
                }

                if (movieDbAssembly == null)
                {
                    status.Error = "MovieDb plugin assembly is not loaded.";
                    return;
                }

                status.MovieDbAssemblyLoaded = true;
                status.MovieDbAssemblyVersion = movieDbAssembly.GetName().Version?.ToString();

                var providerBase = movieDbAssembly.GetType("MovieDb.MovieDbProviderBase");
                if (providerBase == null)
                {
                    status.Error = "MovieDb.MovieDbProviderBase was not found.";
                    return;
                }

                var metadataLanguages = providerBase.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => string.Equals(m.Name, "GetMovieDbMetadataLanguages", StringComparison.Ordinal) &&
                                         m.ReturnType == typeof(string[]));
                var languageMapper = providerBase.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .FirstOrDefault(m => string.Equals(m.Name, "MapLanguageToProviderLanguage", StringComparison.Ordinal) &&
                                         m.ReturnType == typeof(string));
                var imageLanguages = providerBase.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .FirstOrDefault(m => string.Equals(m.Name, "GetImageLanguagesParam", StringComparison.Ordinal) &&
                                         m.ReturnType == typeof(string));

                status.MetadataLanguagesTargetFound = metadataLanguages != null;
                status.MetadataLanguagesTarget = metadataLanguages?.ToString();
                status.LanguageMapperFound = languageMapper != null;
                status.LanguageMapperTarget = languageMapper?.ToString();
                status.ImageLanguagesTargetFound = imageLanguages != null;
                status.ImageLanguagesTarget = imageLanguages?.ToString();

                MovieDbFallbackPatches.LanguageMapper = languageMapper;
                _harmony = new Harmony(HarmonyId);

                if (metadataLanguages != null && languageMapper != null)
                {
                    var postfix = typeof(MovieDbFallbackPatches).GetMethod(
                        nameof(MovieDbFallbackPatches.MetadataLanguagesPostfix),
                        BindingFlags.Static | BindingFlags.Public);
                    _harmony.Patch(metadataLanguages, postfix: new HarmonyMethod(postfix));
                    status.MetadataLanguagesPatched = true;
                }

                if (imageLanguages != null)
                {
                    var postfix = typeof(MovieDbFallbackPatches).GetMethod(
                        nameof(MovieDbFallbackPatches.ImageLanguagesPostfix),
                        BindingFlags.Static | BindingFlags.Public);
                    _harmony.Patch(imageLanguages, postfix: new HarmonyMethod(postfix));
                    status.ImageLanguagesPatched = true;
                }
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("MovieDb fallback runtime mod unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try
            {
                _harmony?.UnpatchAll(HarmonyId);
            }
            catch
            {
                // Best effort during plugin shutdown.
            }
        }
    }

    public static class MovieDbFallbackPatches
    {
        internal static MethodInfo LanguageMapper { get; set; }

        public static void MetadataLanguagesPostfix(object __instance, object[] __args, ref string[] __result)
        {
            try
            {
                var options = Plugin.Instance?.GetPluginOptions()?.MetadataEnhanceOptions;
                if (options?.EnableMovieDbFallbackLanguages != true || __result == null || LanguageMapper == null)
                    return;

                var lookup = __args?.OfType<ItemLookupInfo>().FirstOrDefault();
                var preferredLanguage = lookup?.MetadataLanguage;
                if (options.MovieDbFallbackOnlyForChinese &&
                    (string.IsNullOrWhiteSpace(preferredLanguage) ||
                     !preferredLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)))
                    return;

                var providerLanguages = __args?.OfType<string[]>().LastOrDefault();
                if (providerLanguages == null || providerLanguages.Length == 0) return;

                var list = __result.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
                var englishIndex = list.FindIndex(v =>
                    string.Equals(v, "en", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(v, "en-us", StringComparison.OrdinalIgnoreCase));

                foreach (var configured in SplitLanguages(options.MovieDbFallbackLanguages))
                {
                    if (list.Contains(configured, StringComparer.OrdinalIgnoreCase)) continue;

                    string mapped = null;
                    try
                    {
                        var parameters = LanguageMapper.GetParameters();
                        if (parameters.Length == 4)
                        {
                            mapped = LanguageMapper.Invoke(__instance,
                                new object[] { configured, null, false, providerLanguages }) as string;
                        }
                        else
                        {
                            // Unknown mapper shape: do not guess argument semantics.
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(mapped) ||
                        list.Contains(mapped, StringComparer.OrdinalIgnoreCase))
                        continue;

                    if (englishIndex >= 0)
                    {
                        list.Insert(englishIndex, mapped);
                        englishIndex++;
                    }
                    else
                    {
                        list.Add(mapped);
                    }
                }

                __result = list.ToArray();
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("MovieDb fallback language expansion skipped: " + ex.Message);
            }
        }

        public static void ImageLanguagesPostfix(ref string __result)
        {
            try
            {
                var options = Plugin.Instance?.GetPluginOptions()?.MetadataEnhanceOptions;
                if (options?.EnableMovieDbFallbackLanguages != true ||
                    options.IncludeGenericChineseImageLanguage != true ||
                    string.IsNullOrWhiteSpace(__result))
                    return;

                var list = __result.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => v.Trim())
                    .Where(v => v.Length > 0)
                    .ToList();

                var firstChinese = list.FindIndex(v => v.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
                if (firstChinese >= 0 && !list.Contains("zh", StringComparer.OrdinalIgnoreCase))
                    list.Insert(firstChinese + 1, "zh");

                __result = string.Join(",", list);
            }
            catch
            {
                // Image-language widening is optional and must never break MovieDb image queries.
            }
        }

        private static IEnumerable<string> SplitLanguages(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }
}
