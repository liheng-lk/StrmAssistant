using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class OriginalPosterCapabilityStatus
    {
        public bool ProviderManagerLoaded { get; set; }
        public bool RemoteImagesTargetFound { get; set; }
        public bool RemoteImagesPatched { get; set; }
        public string RemoteImagesTarget { get; set; }
        public string Error { get; set; }
    }

    public static class OriginalPosterModState
    {
        public static OriginalPosterCapabilityStatus Status { get; internal set; } =
            new OriginalPosterCapabilityStatus();
    }

    /// <summary>
    /// Reorders remote-image results only. It never removes an image and never writes a local image.
    /// </summary>
    public sealed class OriginalPosterRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.original-poster";
        private Harmony _harmony;

        public void Run()
        {
            var status = new OriginalPosterCapabilityStatus();
            OriginalPosterModState.Status = status;

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var providerManager = embyProviders.GetType("Emby.Providers.Manager.ProviderManager");
                status.ProviderManagerLoaded = providerManager != null;

                var target = providerManager?.GetMethod("GetAvailableRemoteImages",
                    BindingFlags.Instance | BindingFlags.Public, null,
                    new[]
                    {
                        typeof(BaseItem), typeof(LibraryOptions), typeof(RemoteImageQuery),
                        typeof(IDirectoryService), typeof(CancellationToken)
                    }, null);

                status.RemoteImagesTargetFound = target != null;
                status.RemoteImagesTarget = target?.ToString();
                if (target == null)
                {
                    status.Error = "ProviderManager.GetAvailableRemoteImages target was not found.";
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                var prefix = typeof(OriginalPosterPatches).GetMethod(
                    nameof(OriginalPosterPatches.GetAvailableRemoteImagesPrefix),
                    BindingFlags.Static | BindingFlags.Public);
                var postfix = typeof(OriginalPosterPatches).GetMethod(
                    nameof(OriginalPosterPatches.GetAvailableRemoteImagesPostfix),
                    BindingFlags.Static | BindingFlags.Public);
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
                status.RemoteImagesPatched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Original poster runtime mod unavailable: " + status.Error);
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

    public static class OriginalPosterPatches
    {
        public static void GetAvailableRemoteImagesPrefix(object[] __args)
        {
            try
            {
                var options = Plugin.Instance?.GetPluginOptions()?.MetadataEnhanceOptions;
                if (options?.PreferOriginalPoster != true || __args == null) return;

                var item = __args.OfType<BaseItem>().FirstOrDefault();
                var query = __args.OfType<RemoteImageQuery>().FirstOrDefault();
                if (item == null || query == null || string.IsNullOrWhiteSpace(GetOriginalLanguage(item))) return;

                query.IncludeAllLanguages = true;
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Original poster query widening skipped: " + ex.Message);
            }
        }

        public static void GetAvailableRemoteImagesPostfix(object[] __args,
            ref Task<IEnumerable<RemoteImageInfo>> __result)
        {
            try
            {
                var options = Plugin.Instance?.GetPluginOptions()?.MetadataEnhanceOptions;
                if (options?.PreferOriginalPoster != true || __args == null || __result == null) return;

                var item = __args.OfType<BaseItem>().FirstOrDefault();
                var libraryOptions = __args.OfType<LibraryOptions>().FirstOrDefault();
                if (item == null) return;

                var originalLanguage = NormalizeLanguage(GetOriginalLanguage(item));
                if (string.IsNullOrWhiteSpace(originalLanguage)) return;
                var preferredLanguage = NormalizeLanguage(libraryOptions?.PreferredImageLanguage);
                var preferBackdrop = options.PreferOriginalBackdrop;

                __result = ReorderAsync(__result, originalLanguage, preferredLanguage, preferBackdrop);
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Original poster reorder skipped: " + ex.Message);
            }
        }

        private static async Task<IEnumerable<RemoteImageInfo>> ReorderAsync(
            Task<IEnumerable<RemoteImageInfo>> sourceTask, string originalLanguage,
            string preferredLanguage, bool preferBackdrop)
        {
            var source = await sourceTask.ConfigureAwait(false);
            if (source == null) return Enumerable.Empty<RemoteImageInfo>();

            var indexed = source.Select((image, index) => new { image, index }).ToList();
            return indexed
                .OrderBy(entry => GetPriority(entry.image, originalLanguage, preferredLanguage, preferBackdrop))
                .ThenBy(entry => entry.index)
                .Select(entry => entry.image)
                .ToList();
        }

        private static int GetPriority(RemoteImageInfo image, string originalLanguage,
            string preferredLanguage, bool preferBackdrop)
        {
            if (image == null) return 40;
            var targetType = image.Type == ImageType.Primary ||
                             (preferBackdrop && image.Type == ImageType.Backdrop);
            if (!targetType) return 30;

            var language = NormalizeLanguage(image.Language);
            if (!string.IsNullOrWhiteSpace(language) &&
                string.Equals(language, originalLanguage, StringComparison.OrdinalIgnoreCase))
                return 0;
            if (!string.IsNullOrWhiteSpace(language) && !string.IsNullOrWhiteSpace(preferredLanguage) &&
                string.Equals(language, preferredLanguage, StringComparison.OrdinalIgnoreCase))
                return 10;
            return 20;
        }

        private static string GetOriginalLanguage(BaseItem item)
        {
            try
            {
                if (item is BoxSet boxSet)
                    return Plugin.MetadataApi?.GetCollectionOriginalLanguage(boxSet);

                BaseItem languageItem = item;
                if (item is Season season) languageItem = season.Series;
                else if (item is Episode episode) languageItem = episode.Series;

                if (languageItem == null) return null;
                var language = LanguageUtility.GetLanguageByTitle(languageItem.OriginalTitle);
                return string.IsNullOrWhiteSpace(language) ? null : language;
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeLanguage(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var normalized = value.Trim().Replace('_', '-');
            var separator = normalized.IndexOf('-');
            return (separator > 0 ? normalized.Substring(0, separator) : normalized).ToLowerInvariant();
        }
    }
}
