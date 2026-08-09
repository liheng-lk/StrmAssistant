using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using System;
using System.Collections.Generic;

namespace StrmAssistant.Api
{
    public sealed class RuntimeModDiagnosticsResult
    {
        public string GeneratedUtc { get; set; }
        public string EmbyVersion { get; set; }
        public bool PluginModSupported { get; set; }
        public RuntimeModCapabilityStatus CoreMods { get; set; }
        public PinyinSortCapabilityStatus PinyinSort { get; set; }
        public MovieDbFallbackCapabilityStatus MovieDbFallback { get; set; }
        public AlternateMovieDbCapabilityStatus AlternateMovieDb { get; set; }
        public OriginalPosterCapabilityStatus OriginalPoster { get; set; }
        public MovieDbEpisodeGroupCapabilityStatus EpisodeGroup { get; set; }
        public MultiVersionDisplayCapabilityStatus MultiVersionDisplay { get; set; }
        public MultiVersionUserDataIsolationCapabilityStatus MultiVersionUserDataIsolation { get; set; }
        public MissingEpisodesCapabilityStatus MissingEpisodes { get; set; }
        public EpisodeDtoBeautifyCapabilityStatus EpisodeDtoBeautify { get; set; }
        public ForcedUserPreferencesCapabilityStatus ForcedUserPreferences { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/Diagnostics/RuntimeMods", "GET",
        Summary = "Return consolidated read-only runtime Harmony/compatibility status")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetRuntimeModDiagnostics : IReturn<RuntimeModDiagnosticsResult> { }

    public sealed class RuntimeModDiagnosticsApiService : BaseApiService
    {
        public object Get(GetRuntimeModDiagnostics request)
        {
            var result = new RuntimeModDiagnosticsResult
            {
                GeneratedUtc = DateTimeOffset.UtcNow.ToString("O"),
                EmbyVersion = Plugin.Instance?.ApplicationHost?.ApplicationVersion?.ToString(),
                PluginModSupported = Plugin.Instance?.IsModSupported == true,
                CoreMods = RuntimeModState.Status,
                PinyinSort = PinyinSortModState.Status,
                MovieDbFallback = MovieDbFallbackModState.Status,
                AlternateMovieDb = AlternateMovieDbModState.Status,
                OriginalPoster = OriginalPosterModState.Status,
                EpisodeGroup = MovieDbEpisodeGroupModState.Status,
                MultiVersionDisplay = MultiVersionDisplayModState.Status,
                MultiVersionUserDataIsolation = MultiVersionUserDataIsolationModState.Status,
                MissingEpisodes = MissingEpisodesModState.Status,
                EpisodeDtoBeautify = EpisodeDtoBeautifyModState.Status,
                ForcedUserPreferences = ForcedUserPreferencesModState.Status
            };

            if (!result.PluginModSupported)
                result.Warnings.Add("Plugin runtime mod support is disabled for this Emby runtime.");
            if (result.MultiVersionUserDataIsolation?.Patched != true)
                result.Warnings.Add("Per-version UserData isolation target is not currently patched; keep the isolation option disabled until runtime verification.");
            if (result.MissingEpisodes?.Patched != true)
                result.Warnings.Add("Missing-episode provider injection target is not currently patched.");
            if ((result.EpisodeDtoBeautify?.SingleTargetsPatched ?? 0) == 0 &&
                (result.EpisodeDtoBeautify?.BatchTargetsPatched ?? 0) == 0)
                result.Warnings.Add("Episode DTO beautification did not resolve a DtoService target on this runtime.");
            if (result.ForcedUserPreferences?.LibraryOrderPatched != true)
                result.Warnings.Add("Forced library order did not resolve UserViewManager.GetUserViews on this runtime.");

            return result;
        }
    }
}
