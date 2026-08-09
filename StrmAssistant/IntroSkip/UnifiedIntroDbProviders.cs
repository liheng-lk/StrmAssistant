using MediaBrowser.Common.Net;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.IntroSkip
{
    internal interface IUnifiedIntroDbProvider
    {
        string Name { get; }
        Task<UnifiedIntroDbDocument> FetchAsync(UnifiedIntroDbIdentity identity, int timeoutSeconds,
            CancellationToken cancellationToken);
    }

    internal abstract class UnifiedIntroDbHttpProviderBase : IUnifiedIntroDbProvider
    {
        protected readonly IHttpClient HttpClient;
        protected readonly IJsonSerializer JsonSerializer;

        protected UnifiedIntroDbHttpProviderBase(IHttpClient httpClient, IJsonSerializer jsonSerializer)
        {
            HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            JsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
        }

        public abstract string Name { get; }

        public abstract Task<UnifiedIntroDbDocument> FetchAsync(UnifiedIntroDbIdentity identity, int timeoutSeconds,
            CancellationToken cancellationToken);

        protected async Task<string> GetJsonAsync(string url, int timeoutSeconds, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, Math.Min(timeoutSeconds, 120))));
            var request = new HttpRequestOptions
            {
                Url = url,
                CancellationToken = timeout.Token,
                AcceptHeader = "application/json",
                BufferContent = true,
                UserAgent = Plugin.Instance?.UserAgent
            };

            try
            {
                using var response = await HttpClient.SendAsync(request, "GET").ConfigureAwait(false);
                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                {
                    if (Plugin.Instance?.DebugMode == true)
                        Plugin.Instance.Logger.Debug(Name + " request returned HTTP " + (int)response.StatusCode + ": " + url);
                    return null;
                }
                await using var stream = response.Content;
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug(Name + " request timed out: " + url);
                return null;
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug(Name + " request failed: " + ex.Message);
                return null;
            }
        }

        protected static double? NormalizeConfidence(double? value)
        {
            if (!value.HasValue) return null;
            var confidence = value.Value;
            if (confidence > 1 && confidence <= 100) confidence /= 100d;
            return Math.Max(0, Math.Min(confidence, 1));
        }

        protected static double? Seconds(double? seconds, long? milliseconds)
        {
            if (seconds.HasValue) return seconds.Value;
            return milliseconds.HasValue ? milliseconds.Value / 1000d : (double?)null;
        }
    }

    internal sealed class IntroDbAppProvider : UnifiedIntroDbHttpProviderBase
    {
        private const string BaseUrl = "https://api.introdb.app";

        public IntroDbAppProvider(IHttpClient httpClient, IJsonSerializer jsonSerializer)
            : base(httpClient, jsonSerializer) { }

        public override string Name => "IntroDB.app";

        public override async Task<UnifiedIntroDbDocument> FetchAsync(UnifiedIntroDbIdentity identity,
            int timeoutSeconds, CancellationToken cancellationToken)
        {
            // IntroDB.app keys TV episodes by parent-series IMDb + season + episode.
            if (identity == null || string.IsNullOrWhiteSpace(identity.SeriesImdbId) ||
                !identity.SeasonNumber.HasValue || !identity.EpisodeNumber.HasValue)
                return null;

            var query = "?imdb_id=" + Uri.EscapeDataString(identity.SeriesImdbId) +
                        "&season=" + identity.SeasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        "&episode=" + identity.EpisodeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // /segments is the current API and can return intro, recap and outro in one request.
            var json = await GetJsonAsync(BaseUrl + "/segments" + query, timeoutSeconds, cancellationToken)
                .ConfigureAwait(false);
            var fromSegments = ParseSegments(json);
            if (fromSegments?.IntroStartSeconds.HasValue == true && fromSegments.IntroEndSeconds.HasValue)
                return fromSegments;

            // Keep the legacy intro-only endpoint as a compatibility fallback.
            json = await GetJsonAsync(BaseUrl + "/intro" + query, timeoutSeconds, cancellationToken)
                .ConfigureAwait(false);
            var legacy = ParseLegacyIntro(json);
            if (legacy == null) return fromSegments;
            if (fromSegments?.CreditsStartSeconds.HasValue == true)
            {
                legacy.CreditsStartSeconds = fromSegments.CreditsStartSeconds;
                legacy.CreditsEndSeconds = fromSegments.CreditsEndSeconds;
                legacy.CreditsConfidence = fromSegments.CreditsConfidence;
            }
            if (fromSegments?.RecapEndSeconds.HasValue == true)
            {
                legacy.RecapStartSeconds = fromSegments.RecapStartSeconds;
                legacy.RecapEndSeconds = fromSegments.RecapEndSeconds;
                legacy.RecapConfidence = fromSegments.RecapConfidence;
            }
            legacy.Source = Name;
            return legacy;
        }

        private UnifiedIntroDbDocument ParseSegments(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                // IntroDB accepts clock-style timestamps (mm:ss / hh:mm:ss). Normalize those values
                // before handing the payload to Emby's serializer, while leaving numeric payloads untouched.
                json = NormalizeClockStyleSegmentFields(json);

                List<IntroDbAppSegment> segments = null;
                var trimmed = json.TrimStart();
                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                    segments = JsonSerializer.DeserializeFromString<List<IntroDbAppSegment>>(json);
                else
                {
                    var envelope = JsonSerializer.DeserializeFromString<IntroDbAppSegmentsEnvelope>(json);
                    if (envelope != null)
                    {
                        segments = envelope.segments ?? new List<IntroDbAppSegment>();
                        if (envelope.intro != null) { envelope.intro.segment_type = "intro"; segments.Add(envelope.intro); }
                        if (envelope.recap != null) { envelope.recap.segment_type = "recap"; segments.Add(envelope.recap); }
                        if (envelope.outro != null) { envelope.outro.segment_type = "outro"; segments.Add(envelope.outro); }
                    }
                }
                if (segments == null || segments.Count == 0) return null;

                var intro = Best(segments, "intro");
                var recap = Best(segments, "recap");
                var outro = Best(segments, "outro");
                var result = new UnifiedIntroDbDocument { Source = Name };
                ApplyIntroDbSegment(result, intro, "intro");
                ApplyIntroDbSegment(result, recap, "recap");
                ApplyIntroDbSegment(result, outro, "outro");
                result.Confidence = result.IntroConfidence;
                return result;
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("IntroDB.app /segments parse failed: " + ex.Message);
                return null;
            }
        }

        private UnifiedIntroDbDocument ParseLegacyIntro(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var item = JsonSerializer.DeserializeFromString<IntroDbAppLegacyIntro>(json);
                if (item == null) return null;
                var start = item.start ?? Seconds(null, item.start_ms);
                var end = item.end ?? Seconds(null, item.end_ms);
                if (!start.HasValue || !end.HasValue) return null;
                var confidence = NormalizeConfidence(item.confidence);
                return new UnifiedIntroDbDocument
                {
                    IntroStartSeconds = start,
                    IntroEndSeconds = end,
                    IntroConfidence = confidence,
                    Confidence = confidence,
                    Source = Name,
                    ExternalId = item.imdb_id
                };
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("IntroDB.app /intro parse failed: " + ex.Message);
                return null;
            }
        }

        private static IntroDbAppSegment Best(IEnumerable<IntroDbAppSegment> segments, string type)
        {
            return segments
                .Where(v => v != null && string.Equals(v.segment_type, type, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(v => NormalizeConfidence(v.confidence) ?? -1)
                .ThenByDescending(v => v.submission_count ?? 0)
                .FirstOrDefault();
        }

        private static void ApplyIntroDbSegment(UnifiedIntroDbDocument target, IntroDbAppSegment item, string type)
        {
            if (target == null || item == null) return;
            var start = Seconds(item.start_sec, item.start_ms);
            var end = Seconds(item.end_sec, item.end_ms);
            var confidence = NormalizeConfidence(item.confidence);
            switch (type)
            {
                case "intro":
                    target.IntroStartSeconds = start;
                    target.IntroEndSeconds = end;
                    target.IntroConfidence = confidence;
                    break;
                case "recap":
                    target.RecapStartSeconds = start;
                    target.RecapEndSeconds = end;
                    target.RecapConfidence = confidence;
                    break;
                case "outro":
                    target.CreditsStartSeconds = start;
                    target.CreditsEndSeconds = end;
                    target.CreditsConfidence = confidence;
                    break;
            }
        }

        private static string NormalizeClockStyleSegmentFields(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return json;
            return System.Text.RegularExpressions.Regex.Replace(
                json,
                "\\\"(?<key>start_sec|end_sec)\\\"\\s*:\\s*\\\"(?<value>[^\\\"]+)\\\"",
                match =>
                {
                    if (!TryParseClockOrSeconds(match.Groups["value"].Value, out var seconds)) return match.Value;
                    return "\"" + match.Groups["key"].Value + "\":" +
                           seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                },
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static bool TryParseClockOrSeconds(string value, out double seconds)
        {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var text = value.Trim();
            if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out seconds))
                return seconds >= 0;

            var parts = text.Split(':');
            if (parts.Length != 2 && parts.Length != 3) return false;
            var values = new double[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out values[i]) || values[i] < 0)
                    return false;
            }

            if (parts.Length == 2)
            {
                if (values[1] >= 60) return false;
                seconds = values[0] * 60d + values[1];
                return true;
            }

            if (values[1] >= 60 || values[2] >= 60) return false;
            seconds = values[0] * 3600d + values[1] * 60d + values[2];
            return true;
        }

        private sealed class IntroDbAppSegmentsEnvelope
        {
            public List<IntroDbAppSegment> segments { get; set; }
            public IntroDbAppSegment intro { get; set; }
            public IntroDbAppSegment recap { get; set; }
            public IntroDbAppSegment outro { get; set; }
        }

        private sealed class IntroDbAppSegment
        {
            public string segment_type { get; set; }
            public double? start_sec { get; set; }
            public double? end_sec { get; set; }
            public long? start_ms { get; set; }
            public long? end_ms { get; set; }
            public double? confidence { get; set; }
            public int? submission_count { get; set; }
        }

        private sealed class IntroDbAppLegacyIntro
        {
            public string imdb_id { get; set; }
            public double? start { get; set; }
            public double? end { get; set; }
            public long? start_ms { get; set; }
            public long? end_ms { get; set; }
            public double? confidence { get; set; }
            public int? submission_count { get; set; }
        }
    }

    internal sealed class TheIntroDbProvider : UnifiedIntroDbHttpProviderBase
    {
        private const string V3Endpoint = "https://api.theintrodb.org/v3/media";
        private const string V2Endpoint = "https://api.theintrodb.org/v2/media";
        private static readonly SemaphoreSlim RateGate = new SemaphoreSlim(1, 1);
        private static DateTime _lastRequestUtc = DateTime.MinValue;
        private static readonly TimeSpan MinimumSpacing = TimeSpan.FromMilliseconds(350);

        public TheIntroDbProvider(IHttpClient httpClient, IJsonSerializer jsonSerializer)
            : base(httpClient, jsonSerializer) { }

        public override string Name => "TheIntroDB.org";

        public override async Task<UnifiedIntroDbDocument> FetchAsync(UnifiedIntroDbIdentity identity,
            int timeoutSeconds, CancellationToken cancellationToken)
        {
            if (identity == null || !identity.SeasonNumber.HasValue || !identity.EpisodeNumber.HasValue)
                return null;

            // TheIntroDB TV lookups use the parent series external ID plus season/episode.
            // Episode-level TMDB IDs return media-not-found for normal TV queries, so only use them as a fallback.
            var tmdbText = !string.IsNullOrWhiteSpace(identity.SeriesTmdbId)
                ? identity.SeriesTmdbId
                : identity.EpisodeTmdbId;
            var imdbText = !string.IsNullOrWhiteSpace(identity.SeriesImdbId)
                ? identity.SeriesImdbId
                : identity.EpisodeImdbId;
            var hasTmdb = long.TryParse(tmdbText, out var tmdbId) && tmdbId > 0;
            var hasImdb = !string.IsNullOrWhiteSpace(imdbText);
            if (!hasTmdb && !hasImdb) return null;

            var baseQuery = hasTmdb
                ? "?tmdb_id=" + tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "?imdb_id=" + Uri.EscapeDataString(imdbText);
            baseQuery += "&season=" + identity.SeasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                         "&episode=" + identity.EpisodeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var v3Query = baseQuery;
            if (identity.DurationMs.HasValue && identity.DurationMs.Value > 0)
                v3Query += "&duration_ms=" + identity.DurationMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

            await WaitForRateGateAsync(cancellationToken).ConfigureAwait(false);
            var json = await GetJsonAsync(V3Endpoint + v3Query, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            var result = Parse(json, identity, "v3");
            if (result != null) return result;

            if (!hasTmdb) return null;
            await WaitForRateGateAsync(cancellationToken).ConfigureAwait(false);
            json = await GetJsonAsync(V2Endpoint + baseQuery, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            return Parse(json, identity, "v2");
        }

        private UnifiedIntroDbDocument Parse(string json, UnifiedIntroDbIdentity identity, string apiVersion)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var response = JsonSerializer.DeserializeFromString<TheIntroDbResponse>(json);
                if (response == null) return null;
                var intro = Best(response.intro);
                var recap = Best(response.recap);
                var credits = BestCredits(response.credits);
                var preview = Best(response.preview);
                if (intro == null && recap == null && credits == null && preview == null) return null;

                var result = new UnifiedIntroDbDocument
                {
                    Source = Name + " " + apiVersion,
                    ExternalId = response.tmdb_id > 0 ? response.tmdb_id.ToString() :
                        (!string.IsNullOrWhiteSpace(response.imdb_id) ? response.imdb_id :
                            (identity.SeriesTmdbId ?? identity.EpisodeTmdbId ?? identity.SeriesImdbId ?? identity.EpisodeImdbId))
                };
                Apply(result, intro, "intro");
                Apply(result, recap, "recap");
                Apply(result, credits, "credits");
                Apply(result, preview, "preview");
                result.Confidence = result.IntroConfidence;
                return result;
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("TheIntroDB " + apiVersion + " parse failed: " + ex.Message);
                return null;
            }
        }

        private static async Task WaitForRateGateAsync(CancellationToken cancellationToken)
        {
            await RateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var elapsed = DateTime.UtcNow - _lastRequestUtc;
                if (elapsed < MinimumSpacing)
                    await Task.Delay(MinimumSpacing - elapsed, cancellationToken).ConfigureAwait(false);
                _lastRequestUtc = DateTime.UtcNow;
            }
            finally
            {
                RateGate.Release();
            }
        }

        private static TheIntroDbSegment Best(IEnumerable<TheIntroDbSegment> items)
        {
            return (items ?? Enumerable.Empty<TheIntroDbSegment>())
                .Where(v => v != null)
                .OrderByDescending(v => NormalizeConfidence(v.confidence) ?? -1)
                .ThenByDescending(v => v.submission_count ?? 0)
                .FirstOrDefault();
        }

        private static TheIntroDbSegment BestCredits(IEnumerable<TheIntroDbSegment> items)
        {
            return (items ?? Enumerable.Empty<TheIntroDbSegment>())
                .Where(v => v != null && v.start_ms.HasValue)
                .OrderByDescending(v => v.start_ms.Value)
                .ThenByDescending(v => NormalizeConfidence(v.confidence) ?? -1)
                .ThenByDescending(v => v.submission_count ?? 0)
                .FirstOrDefault();
        }

        private static void Apply(UnifiedIntroDbDocument target, TheIntroDbSegment item, string type)
        {
            if (target == null || item == null) return;
            var start = Seconds(null, item.start_ms);
            var end = Seconds(null, item.end_ms);
            var confidence = NormalizeConfidence(item.confidence);
            switch (type)
            {
                case "intro":
                    target.IntroStartSeconds = start ?? 0;
                    target.IntroEndSeconds = end;
                    target.IntroConfidence = confidence;
                    break;
                case "recap":
                    target.RecapStartSeconds = start ?? 0;
                    target.RecapEndSeconds = end;
                    target.RecapConfidence = confidence;
                    break;
                case "credits":
                    target.CreditsStartSeconds = start;
                    target.CreditsEndSeconds = end;
                    target.CreditsConfidence = confidence;
                    break;
                case "preview":
                    target.PreviewStartSeconds = start;
                    target.PreviewEndSeconds = end;
                    target.PreviewConfidence = confidence;
                    break;
            }
        }

        private sealed class TheIntroDbResponse
        {
            public long tmdb_id { get; set; }
            public string imdb_id { get; set; }
            public string type { get; set; }
            public List<TheIntroDbSegment> intro { get; set; }
            public List<TheIntroDbSegment> recap { get; set; }
            public List<TheIntroDbSegment> credits { get; set; }
            public List<TheIntroDbSegment> preview { get; set; }
        }

        private sealed class TheIntroDbSegment
        {
            public long? start_ms { get; set; }
            public long? end_ms { get; set; }
            public double? confidence { get; set; }
            public int? submission_count { get; set; }
        }
    }
}
