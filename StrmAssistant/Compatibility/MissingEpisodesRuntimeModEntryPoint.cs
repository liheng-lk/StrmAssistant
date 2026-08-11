using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using StrmAssistant.Provider;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace StrmAssistant.Compatibility
{
    public sealed class MissingEpisodesCapabilityStatus
    {
        public bool TargetFound { get; set; }
        public bool Patched { get; set; }
        public string Target { get; set; }
        public string Error { get; set; }
    }

    public static class MissingEpisodesModState
    {
        public static MissingEpisodesCapabilityStatus Status { get; internal set; } =
            new MissingEpisodesCapabilityStatus();
    }

    public static class MissingEpisodesRuntimeContext
    {
        public static readonly AsyncLocal<string> CurrentSeriesContainingFolderPath = new AsyncLocal<string>();
    }

    public sealed class MissingEpisodesRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.missing-episodes";
        private Harmony _harmony;

        public void Run()
        {
            var status = new MissingEpisodesCapabilityStatus();
            MissingEpisodesModState.Status = status;
            try
            {
                var providers = Assembly.Load("Emby.Providers");
                var providerManager = providers.GetType("Emby.Providers.Manager.ProviderManager");
                var target = providerManager?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(method =>
                        string.Equals(method.Name, "GetEnabledMetadataProviders", StringComparison.Ordinal) &&
                        method.ReturnType.IsArray &&
                        typeof(IMetadataProvider).IsAssignableFrom(method.ReturnType.GetElementType()));

                status.TargetFound = target != null;
                status.Target = target?.ToString();
                if (target == null) return;

                _harmony = new Harmony(HarmonyId);
                var postfix = typeof(MissingEpisodesPatches).GetMethod(
                    nameof(MissingEpisodesPatches.GetEnabledMetadataProvidersPostfix),
                    BindingFlags.Static | BindingFlags.Public);
                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                status.Patched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Missing-episode runtime mod unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
        }
    }

    public static class MissingEpisodesPatches
    {
        public static void GetEnabledMetadataProvidersPostfix(object[] __args, ref IMetadataProvider[] __result)
        {
            try
            {
                if (Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions?.EnhanceMissingEpisodes != true ||
                    __args == null || __result == null)
                    return;

                var series = __args.OfType<Series>().FirstOrDefault();
                if (series == null || !series.HasProviderId(MetadataProviders.Tmdb)) return;

                MissingEpisodesRuntimeContext.CurrentSeriesContainingFolderPath.Value = series.ContainingFolderPath;

                var list = __result
                    .Where(provider => provider != null && provider.GetType() != typeof(MovieDbMissingEpisodeProvider))
                    .ToList();
                var custom = new MovieDbMissingEpisodeProvider();
                var nativeIndex = list.FindIndex(provider =>
                    string.Equals(provider.GetType().Name, "MovieDbSeriesProvider", StringComparison.Ordinal) ||
                    string.Equals(provider.GetType().FullName, "MovieDb.MovieDbSeriesProvider", StringComparison.Ordinal) ||
                    string.Equals(provider.GetType().FullName, "MovieDb.Providers.MovieDbSeriesProvider", StringComparison.Ordinal));

                if (nativeIndex >= 0)
                {
                    list.Insert(nativeIndex, custom);
                }
                else if (!list.OfType<ISeriesMetadataProvider>().Any())
                {
                    list.Add(custom);
                }
                else
                {
                    // Respect an existing non-TMDB series provider rather than overriding scraper priority.
                    return;
                }

                __result = list.ToArray();
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Missing-episode provider injection skipped: " + ex.Message);
            }
        }
    }
}
