using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Concurrent;
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
        public double? IntroConfidence { get; set; }
        public double? RecapStartSeconds { get; set; }
        public double? RecapEndSeconds { get; set; }
        public double? RecapConfidence { get; set; }
        public double? CreditsStartSeconds { get; set; }
        public double? CreditsEndSeconds { get; set; }
        public double? CreditsConfidence { get; set; }
        public double? PreviewStartSeconds { get; set; }
        public double? PreviewEndSeconds { get; set; }
        public double? PreviewConfidence { get; set; }
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
        private sealed class CacheEntry
        {
            public DateTime ExpiresUtc { get; set; }
            public UnifiedIntroDbDocument Document { get; set; }
        }

        private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
            new ConcurrentDictionary<string, CacheEntry>(StringComparer.Ordinal);

        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IntroDbAppProvider _introDbApp;
        private readonly TheIntroDbProvider _theIntroDb;

        public UnifiedIntroDbBridge(IHttpClient httpClient, IJsonSerializer jsonSerializer)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            _introDbApp = new IntroDbAppProvider(httpClient, jsonSerializer);
            _theIntroDb = new TheIntroDbProvider(httpClient, jsonSerializer);
        }

        public static void ClearCache() => Cache.Clear();

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

        public async Task<UnifiedIntroDbDocument> FetchAsync(Episode episode, CancellationToken cancellationToken,
            bool bypassCache = false)
        {
            var options = UnifiedIntroDbRuntimeSettings.GetSnapshot();
            if (!options.Enabled) return null;
            var identity = ResolveIdentity(episode);
            if (identity == null || !identity.SeasonNumber.HasValue || !identity.EpisodeNumber.HasValue) return null;

            var cacheKey = BuildCacheKey(options, identity);
            if (!bypassCache && options.CacheMinutes > 0 && Cache.TryGetValue(cacheKey, out var cached))
            {
                if (cached.ExpiresUtc > DateTime.UtcNow) return Clone(cached.Document);
                Cache.TryRemove(cacheKey, out _);
            }

            UnifiedIntroDbDocument merged = null;
            var sources = new List<string>();
            foreach (var provider in ParseProviderOrder(options.ProviderOrder))
            {
                UnifiedIntroDbDocument current = null;
                if (provider == "introdbapp" && options.IntroDbAppEnabled)
                    current = await _introDbApp.FetchAsync(identity, options.TimeoutSeconds, cancellationToken).ConfigureAwait(false);
                else if (provider == "theintrodb" && options.TheIntroDbEnabled)
                    current = await _theIntroDb.FetchAsync(identity, options.TimeoutSeconds, cancellationToken).ConfigureAwait(false);
                else if (provider == "custom" && options.CustomProviderEnabled)
                    current = await FetchCustomAsync(options.EndpointTemplate, identity, options.TimeoutSeconds, cancellationToken)
                        .ConfigureAwait(false);

                if (current == null) continue;
                var before = Clone(merged);
                merged = Merge(merged, current);
                if (Contributed(before, merged) && !string.IsNullOrWhiteSpace(current.Source)) sources.Add(current.Source);
                if (merged?.IntroStartSeconds.HasValue == true && merged.IntroEndSeconds.HasValue && merged.CreditsStartSeconds.HasValue)
                    break;
            }

            merged = Validate(merged, episode);
            if (merged != null && sources.Count > 0)
                merged.Source = string.Join(" + ", sources.Distinct(StringComparer.OrdinalIgnoreCase));

            if (options.CacheMinutes > 0)
                Cache[cacheKey] = new CacheEntry
                {
                    ExpiresUtc = DateTime.UtcNow.AddMinutes(options.CacheMinutes),
                    Document = Clone(merged)
                };
            return Clone(merged);
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
            if (string.IsNullOrWhiteSpace(identity.SeriesTmdbId) && string.IsNullOrWhiteSpace(identity.SeriesImdbId))
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

        private async Task<UnifiedIntroDbDocument> FetchCustomAsync(string endpointTemplate,
            UnifiedIntroDbIdentity identity, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var url = BuildUrl(endpointTemplate, identity, out var error);
            if (url == null)
            {
                if (Plugin.Instance?.DebugMode == true && !string.IsNullOrWhiteSpace(error))
                    Plugin.Instance.Logger.Debug("Unified IntroDb custom URL skipped: " + error);
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
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
                if (document != null && string.IsNullOrWhiteSpace(document.Source)) document.Source = "Custom";
                if (document != null && !document.IntroConfidence.HasValue) document.IntroConfidence = document.Confidence;
                return document;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Unified IntroDb custom request failed: " + ex.Message);
                return null;
            }
        }

        private static UnifiedIntroDbDocument Merge(UnifiedIntroDbDocument target, UnifiedIntroDbDocument source)
        {
            if (source == null) return target;
            target ??= new UnifiedIntroDbDocument();
            if (!target.IntroStartSeconds.HasValue && source.IntroStartSeconds.HasValue && source.IntroEndSeconds.HasValue)
            {
                target.IntroStartSeconds = source.IntroStartSeconds;
                target.IntroEndSeconds = source.IntroEndSeconds;
                target.IntroConfidence = source.IntroConfidence ?? source.Confidence;
                target.Confidence = target.IntroConfidence;
                target.ExternalId = source.ExternalId;
            }
            if (!target.CreditsStartSeconds.HasValue && source.CreditsStartSeconds.HasValue)
            {
                target.CreditsStartSeconds = source.CreditsStartSeconds;
                target.CreditsEndSeconds = source.CreditsEndSeconds;
                target.CreditsConfidence = source.CreditsConfidence;
            }
            if (!target.RecapEndSeconds.HasValue && source.RecapEndSeconds.HasValue)
            {
                target.RecapStartSeconds = source.RecapStartSeconds;
                target.RecapEndSeconds = source.RecapEndSeconds;
                target.RecapConfidence = source.RecapConfidence;
            }
            if (!target.PreviewStartSeconds.HasValue && source.PreviewStartSeconds.HasValue)
            {
                target.PreviewStartSeconds = source.PreviewStartSeconds;
                target.PreviewEndSeconds = source.PreviewEndSeconds;
                target.PreviewConfidence = source.PreviewConfidence;
            }
            return target;
        }

        private static UnifiedIntroDbDocument Validate(UnifiedIntroDbDocument document, Episode episode)
        {
            if (document == null || !document.IntroStartSeconds.HasValue || !document.IntroEndSeconds.HasValue ||
                document.IntroStartSeconds.Value < 0 || document.IntroEndSeconds.Value <= document.IntroStartSeconds.Value)
                return null;

            var runtime = episode?.RunTimeTicks.HasValue == true
                ? TimeSpan.FromTicks(episode.RunTimeTicks.Value).TotalSeconds
                : (double?)null;
            if (runtime.HasValue && document.IntroEndSeconds.Value >= runtime.Value) return null;

            NormalizeOptionalRange(document.RecapStartSeconds, document.RecapEndSeconds, runtime,
                out var recapStart, out var recapEnd);
            document.RecapStartSeconds = recapStart;
            document.RecapEndSeconds = recapEnd;

            NormalizeOptionalRange(document.PreviewStartSeconds, document.PreviewEndSeconds, runtime,
                out var previewStart, out var previewEnd);
            document.PreviewStartSeconds = previewStart;
            document.PreviewEndSeconds = previewEnd;

            if (document.CreditsStartSeconds.HasValue &&
                (document.CreditsStartSeconds.Value < 0 || runtime.HasValue && document.CreditsStartSeconds.Value >= runtime.Value))
            {
                document.CreditsStartSeconds = null;
                document.CreditsEndSeconds = null;
                document.CreditsConfidence = null;
            }
            else if (document.CreditsStartSeconds.HasValue && document.CreditsEndSeconds.HasValue &&
                     (document.CreditsEndSeconds.Value <= document.CreditsStartSeconds.Value ||
                      runtime.HasValue && document.CreditsEndSeconds.Value > runtime.Value))
            {
                document.CreditsEndSeconds = null;
            }

            document.IntroConfidence = NormalizeConfidence(document.IntroConfidence ?? document.Confidence);
            document.Confidence = document.IntroConfidence;
            document.CreditsConfidence = NormalizeConfidence(document.CreditsConfidence);
            document.RecapConfidence = NormalizeConfidence(document.RecapConfidence);
            document.PreviewConfidence = NormalizeConfidence(document.PreviewConfidence);
            return document;
        }

        private static void NormalizeOptionalRange(double? start, double? end, double? runtime,
            out double? normalizedStart, out double? normalizedEnd)
        {
            normalizedStart = null;
            normalizedEnd = null;
            if (!end.HasValue) return;
            var candidateStart = start ?? 0;
            if (candidateStart < 0 || end.Value <= candidateStart || runtime.HasValue && end.Value >= runtime.Value) return;
            normalizedStart = candidateStart;
            normalizedEnd = end;
        }

        private static double? NormalizeConfidence(double? value)
        {
            if (!value.HasValue) return null;
            var confidence = value.Value;
            if (confidence > 1 && confidence <= 100) confidence /= 100d;
            return Math.Max(0, Math.Min(confidence, 1));
        }

        private static bool Contributed(UnifiedIntroDbDocument before, UnifiedIntroDbDocument after)
        {
            if (after == null) return false;
            if (before == null) return true;
            return before.IntroStartSeconds != after.IntroStartSeconds || before.IntroEndSeconds != after.IntroEndSeconds ||
                   before.CreditsStartSeconds != after.CreditsStartSeconds || before.RecapEndSeconds != after.RecapEndSeconds ||
                   before.PreviewStartSeconds != after.PreviewStartSeconds;
        }

        private static IEnumerable<string> ParseProviderOrder(string value)
        {
            var known = new[] { "introdbapp", "theintrodb", "custom" };
            var result = new List<string>();
            foreach (var raw in (value ?? string.Empty).Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var item = raw.Trim().Replace(".", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
                if (item == "introdb" || item == "introdbapp") item = "introdbapp";
                else if (item == "theintrodb" || item == "tidb") item = "theintrodb";
                if (known.Contains(item) && !result.Contains(item)) result.Add(item);
            }
            foreach (var item in known) if (!result.Contains(item)) result.Add(item);
            return result;
        }

        private static string BuildCacheKey(UnifiedIntroDbOptions options, UnifiedIntroDbIdentity identity)
        {
            return string.Join("|", new[]
            {
                identity.SeriesTmdbId ?? string.Empty,
                identity.SeriesImdbId ?? string.Empty,
                identity.SeasonNumber?.ToString() ?? string.Empty,
                identity.EpisodeNumber?.ToString() ?? string.Empty,
                options.IntroDbAppEnabled.ToString(), options.TheIntroDbEnabled.ToString(),
                options.CustomProviderEnabled.ToString(), options.ProviderOrder ?? string.Empty,
                options.EndpointTemplate ?? string.Empty
            });
        }

        private static UnifiedIntroDbDocument Clone(UnifiedIntroDbDocument value)
        {
            if (value == null) return null;
            return new UnifiedIntroDbDocument
            {
                IntroStartSeconds = value.IntroStartSeconds, IntroEndSeconds = value.IntroEndSeconds,
                IntroConfidence = value.IntroConfidence, RecapStartSeconds = value.RecapStartSeconds,
                RecapEndSeconds = value.RecapEndSeconds, RecapConfidence = value.RecapConfidence,
                CreditsStartSeconds = value.CreditsStartSeconds, CreditsEndSeconds = value.CreditsEndSeconds,
                CreditsConfidence = value.CreditsConfidence, PreviewStartSeconds = value.PreviewStartSeconds,
                PreviewEndSeconds = value.PreviewEndSeconds, PreviewConfidence = value.PreviewConfidence,
                Confidence = value.Confidence, Source = value.Source, ExternalId = value.ExternalId
            };
        }

        private static string SafeProviderId(MediaBrowser.Controller.Entities.BaseItem item, string key)
        {
            try { return item?.GetProviderId(key); } catch { return null; }
        }

        private static string Escape(string value) => Uri.EscapeDataString(value ?? string.Empty);
    }
}
