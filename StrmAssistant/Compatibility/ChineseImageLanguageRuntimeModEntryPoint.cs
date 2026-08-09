using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using StrmAssistant.Common;
using StrmAssistant.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class ChineseImageLanguageCapabilityStatus
    {
        public bool TargetFound { get; set; }
        public bool Patched { get; set; }
        public string Target { get; set; }
        public string Error { get; set; }
    }

    public static class ChineseImageLanguageModState
    {
        public static ChineseImageLanguageCapabilityStatus Status { get; internal set; } =
            new ChineseImageLanguageCapabilityStatus();
    }

    public sealed class ChineseImageLanguageRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.chinese-image-language";
        private Harmony _harmony;

        public void Run()
        {
            var status = new ChineseImageLanguageCapabilityStatus();
            ChineseImageLanguageModState.Status = status;
            try
            {
                var assembly = Assembly.Load("Emby.Providers");
                var type = assembly.GetType("Emby.Providers.Manager.ProviderManager");
                var target = type?.GetMethod("GetAvailableRemoteImages",
                    BindingFlags.Instance | BindingFlags.Public, null,
                    new[]
                    {
                        typeof(BaseItem), typeof(LibraryOptions), typeof(RemoteImageQuery),
                        typeof(IDirectoryService), typeof(CancellationToken)
                    }, null);
                status.TargetFound = target != null;
                status.Target = target?.ToString();
                if (target == null)
                {
                    status.Error = "ProviderManager.GetAvailableRemoteImages target was not found.";
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                var prefix = typeof(ChineseImageLanguagePatches).GetMethod(
                    nameof(ChineseImageLanguagePatches.GetAvailableRemoteImagesPrefix),
                    BindingFlags.Static | BindingFlags.Public);
                var postfix = typeof(ChineseImageLanguagePatches).GetMethod(
                    nameof(ChineseImageLanguagePatches.GetAvailableRemoteImagesPostfix),
                    BindingFlags.Static | BindingFlags.Public);
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
                status.Patched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Chinese image-language runtime mod unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }
    }

    public static class ChineseImageLanguagePatches
    {
        public static void GetAvailableRemoteImagesPrefix(object[] __args)
        {
            try
            {
                var settings = ChineseImageLanguageRuntimeSettings.GetSnapshot();
                if (!settings.Enabled || __args == null) return;

                var item = __args.OfType<BaseItem>().FirstOrDefault();
                var query = __args.OfType<RemoteImageQuery>().FirstOrDefault();
                if (item == null || query == null || !ShouldApply(item)) return;
                query.IncludeAllLanguages = true;
            }
            catch
            {
                // Query widening is optional; keep native behavior on any error.
            }
        }

        public static void GetAvailableRemoteImagesPostfix(object[] __args,
            ref Task<IEnumerable<RemoteImageInfo>> __result)
        {
            try
            {
                var settings = ChineseImageLanguageRuntimeSettings.GetSnapshot();
                if (!settings.Enabled || __args == null || __result == null) return;
                var item = __args.OfType<BaseItem>().FirstOrDefault();
                if (item == null || !ShouldApply(item)) return;

                var priority = ChineseImageLanguageRuntimeSettings.GetPriorityLanguages(settings);
                if (priority.Count == 0) return;
                __result = ReorderAsync(__result, settings, priority);
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Chinese image-language sorting skipped: " + ex.Message);
            }
        }

        private static bool ShouldApply(BaseItem item)
        {
            var metadata = Plugin.Instance?.GetPluginOptions()?.MetadataEnhanceOptions;
            if (metadata?.PreferOriginalPoster != true) return true;

            try
            {
                BaseItem languageItem = item;
                if (item is MediaBrowser.Controller.Entities.TV.Season season) languageItem = season.Series;
                else if (item is MediaBrowser.Controller.Entities.TV.Episode episode) languageItem = episode.Series;

                var originalLanguage = item is MediaBrowser.Controller.Entities.Movies.BoxSet boxSet
                    ? Plugin.MetadataApi?.GetCollectionOriginalLanguage(boxSet)
                    : LanguageUtility.GetLanguageByTitle(languageItem?.OriginalTitle);

                // If the work is clearly non-Chinese and Original Poster is enabled, let the
                // original-language patch keep precedence. Chinese language priority remains useful
                // for Chinese works or when original language cannot be inferred.
                return string.IsNullOrWhiteSpace(originalLanguage) ||
                       originalLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return true;
            }
        }

        private static async Task<IEnumerable<RemoteImageInfo>> ReorderAsync(
            Task<IEnumerable<RemoteImageInfo>> sourceTask, ChineseImageLanguageOptions settings,
            IReadOnlyList<string> priorityLanguages)
        {
            var source = await sourceTask.ConfigureAwait(false);
            if (source == null) return Enumerable.Empty<RemoteImageInfo>();

            return source.Select((image, index) => new { image, index })
                .OrderBy(entry => GetPriority(entry.image, settings, priorityLanguages))
                .ThenBy(entry => entry.index)
                .Select(entry => entry.image)
                .ToList();
        }

        private static int GetPriority(RemoteImageInfo image, ChineseImageLanguageOptions settings,
            IReadOnlyList<string> priorityLanguages)
        {
            if (image == null) return 1000;
            var applies = settings.ApplyPrimary && image.Type == ImageType.Primary ||
                          settings.ApplyLogo && image.Type == ImageType.Logo;
            if (!applies) return 900;

            var language = ChineseImageLanguageRuntimeSettings.Normalize(image.Language);
            if (string.IsNullOrWhiteSpace(language)) return 800;
            for (var i = 0; i < priorityLanguages.Count; i++)
            {
                if (string.Equals(language,
                        ChineseImageLanguageRuntimeSettings.Normalize(priorityLanguages[i]),
                        StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return 700;
        }
    }
}
