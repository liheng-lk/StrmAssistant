using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.IntroSkip
{
    public sealed class UnifiedIntroDbDocument
    {
        public double? IntroStartSeconds { get; set; }
        public double? IntroEndSeconds { get; set; }
        public double? CreditsStartSeconds { get; set; }
        public double? Confidence { get; set; }
        public string Source { get; set; }
        public string ExternalId { get; set; }
    }

    public sealed class UnifiedIntroDbIdentity
    {
        public string SeriesTmdbId { get; set; }
        public string SeriesImdbId { get; set; }
        public string EpisodeTmdbId { get; set; }
        public string EpisodeImdbId { get; set; }
        public int? SeasonNumber { get; set; }
        public int? EpisodeNumber { get; set; }
        public string SeriesName { get; set; }
        public string EpisodeName { get; set; }
    }

    public sealed class UnifiedIntroDbBridge
    {
        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _jsonSerializer;

        public UnifiedIntroDbBridge(IHttpClient httpClient, IJsonSerializer jsonSerializer)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
        }

        public UnifiedIntroDbIdentity ResolveIdentity(Episode episode)
        {
            if (episode == null) return null;
            return new UnifiedIntroDbIdentity
            {
                SeriesTmdbId = SafeProviderId(episode.Series, MetadataProviders.Tmdb.ToString()),
                SeriesImdbId = SafeProviderId(episode.Series, MetadataProviders.Imdb.ToString()),
                EpisodeTmdbId = SafeProviderId(episode, MetadataProviders.Tmdb.ToString()),
                EpisodeImdbId = SafeProviderId(episode, MetadataProviders.Imdb.ToString()),
                SeasonNumber = episode.ParentIndexNumber,
                EpisodeNumber = episode.IndexNumber,
                SeriesName = episode.Series?.Name,
                EpisodeName = episode.Name
            };
        }

        public async Task<UnifiedIntroDbDocument> FetchAsync(Episode episode, CancellationToken cancellationToken)
        {
            var options = UnifiedIntroDbRuntimeSettings.GetSnapshot();
            if (!options.Enabled) return null;
            var identity = ResolveIdentity(episode);
            var url = BuildUrl(options.EndpointTemplate, identity, out var error);
            if (url == null)
            {
                if (Plugin.Instance?.DebugMode == true && !string.IsNullOrWhiteSpace(error))
                    Plugin.Instance.Logger.Debug("Unified IntroDb URL skipped: " + error);
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            var request = new HttpRequestOptions
            {
                Url = url,
                CancellationToken = timeout.Token,
                AcceptHeader = "application/json",
                BufferContent = true,
                UserAgent = Plugin.Instance.UserAgent
            };

            try
            {
                using var response = await _httpClient.SendAsync(request, "GET").ConfigureAwait(false);
                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300) return null;
                await using var stream = response.Content;
                var document = _jsonSerializer.DeserializeFromStream<UnifiedIntroDbDocument>(stream);
                return Validate(document, episode);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Unified IntroDb request failed: " + ex.Message);
                return null;
            }
        }

        public static string BuildUrl(string template, UnifiedIntroDbIdentity identity, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(template))
            {
                error = "EndpointTemplate is empty.";
                return null;
            }
            if (identity == null || !identity.SeasonNumber.HasValue || !identity.EpisodeNumber.HasValue)
            {
                error = "Episode season/index identity is incomplete.";
                return null;
            }
            if (string.IsNullOrWhiteSpace(identity.SeriesTmdbId) &&
                string.IsNullOrWhiteSpace(identity.SeriesImdbId))
            {
                error = "The parent Series has no TMDB or IMDb provider ID.";
                return null;
            }

            var url = template.Trim()
                .Replace("{series_tmdb}", Escape(identity.SeriesTmdbId))
                .Replace("{series_imdb}", Escape(identity.SeriesImdbId))
                .Replace("{episode_tmdb}", Escape(identity.EpisodeTmdbId))
                .Replace("{episode_imdb}", Escape(identity.EpisodeImdbId))
                .Replace("{season}", identity.SeasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("{episode}", identity.EpisodeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("{series_name}", Escape(identity.SeriesName))
                .Replace("{episode_name}", Escape(identity.EpisodeName));

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = "EndpointTemplate did not produce an HTTP/HTTPS absolute URL.";
                return null;
            }
            return url;
        }

        private static UnifiedIntroDbDocument Validate(UnifiedIntroDbDocument document, Episode episode)
        {
            if (document == null) return null;
            if (!document.IntroStartSeconds.HasValue || !document.IntroEndSeconds.HasValue ||
                document.IntroStartSeconds.Value < 0 ||
                document.IntroEndSeconds.Value <= document.IntroStartSeconds.Value)
                return null;

            var runtime = episode?.RunTimeTicks.HasValue == true
                ? TimeSpan.FromTicks(episode.RunTimeTicks.Value).TotalSeconds
                : (double?)null;
            if (runtime.HasValue && document.IntroEndSeconds.Value >= runtime.Value) return null;
            if (document.CreditsStartSeconds.HasValue &&
                (document.CreditsStartSeconds.Value < 0 || runtime.HasValue && document.CreditsStartSeconds.Value >= runtime.Value))
                document.CreditsStartSeconds = null;
            if (document.Confidence.HasValue)
                document.Confidence = Math.Max(0, Math.Min(document.Confidence.Value, 1));
            return document;
        }

        private static string SafeProviderId(MediaBrowser.Controller.Entities.BaseItem item, string key)
        {
            try { return item?.GetProviderId(key); } catch { return null; }
        }

        private static string Escape(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty);
        }
    }
}
