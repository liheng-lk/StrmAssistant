using MediaBrowser.Common.Net;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

        protected UnifiedIntroDbHttpProviderBase(IHttpClient httpClient, IJsonSerializer jsonSerializer)
        {
            HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _ = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
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
            if (identity == null || string.IsNullOrWhiteSpace(identity.SeriesImdbId) ||
                !identity.SeasonNumber.HasValue || !identity.EpisodeNumber.HasValue)
                return null;

            var query = "?imdb_id=" + Uri.EscapeDataString(identity.SeriesImdbId) +
                        "&season=" + identity.SeasonNumber.Value.ToString(CultureInfo.InvariantCulture) +
                        "&episode=" + identity.EpisodeNumber.Value.ToString(CultureInfo.InvariantCulture);

            var json = await GetJsonAsync(BaseUrl + "/segments" + query, timeoutSeconds, cancellationToken)
                .ConfigureAwait(false);
            var current = UnifiedIntroDbRawParser.ParseIntroDbSegments(json);
            if (current?.IntroStartSeconds.HasValue == true && current.IntroEndSeconds.HasValue)
                return current;

            json = await GetJsonAsync(BaseUrl + "/intro" + query, timeoutSeconds, cancellationToken)
                .ConfigureAwait(false);
            var legacy = UnifiedIntroDbRawParser.ParseIntroDbLegacy(json);
            var merged = UnifiedIntroDbRawParser.MergePreferExisting(current, legacy);
            if (merged != null && string.IsNullOrWhiteSpace(merged.Source)) merged.Source = Name;
            return merged;
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

            var tmdbText = !string.IsNullOrWhiteSpace(identity.SeriesTmdbId)
                ? identity.SeriesTmdbId
                : identity.EpisodeTmdbId;
            var imdbText = !string.IsNullOrWhiteSpace(identity.SeriesImdbId)
                ? identity.SeriesImdbId
                : identity.EpisodeImdbId;
            var hasTmdb = long.TryParse(tmdbText, out var tmdbId) && tmdbId > 0;
            var hasImdb = !string.IsNullOrWhiteSpace(imdbText);
            if (!hasTmdb && !hasImdb) return null;

            UnifiedIntroDbDocument merged = null;

            if (hasTmdb)
            {
                var baseQuery = "?tmdb_id=" + tmdbId.ToString(CultureInfo.InvariantCulture) +
                                "&season=" + identity.SeasonNumber.Value.ToString(CultureInfo.InvariantCulture) +
                                "&episode=" + identity.EpisodeNumber.Value.ToString(CultureInfo.InvariantCulture);
                var v3Query = AddDuration(baseQuery, identity.DurationMs);

                await WaitForRateGateAsync(cancellationToken).ConfigureAwait(false);
                var json = await GetJsonAsync(V3Endpoint + v3Query, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                merged = UnifiedIntroDbRawParser.MergePreferExisting(merged,
                    UnifiedIntroDbRawParser.ParseTheIntroDb(json, identity, "v3"));
                if (HasIntro(merged)) return merged;

                await WaitForRateGateAsync(cancellationToken).ConfigureAwait(false);
                json = await GetJsonAsync(V2Endpoint + baseQuery, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                merged = UnifiedIntroDbRawParser.MergePreferExisting(merged,
                    UnifiedIntroDbRawParser.ParseTheIntroDb(json, identity, "v2"));
                if (HasIntro(merged)) return merged;
            }

            if (hasImdb)
            {
                var baseQuery = "?imdb_id=" + Uri.EscapeDataString(imdbText) +
                                "&season=" + identity.SeasonNumber.Value.ToString(CultureInfo.InvariantCulture) +
                                "&episode=" + identity.EpisodeNumber.Value.ToString(CultureInfo.InvariantCulture);
                var v3Query = AddDuration(baseQuery, identity.DurationMs);

                await WaitForRateGateAsync(cancellationToken).ConfigureAwait(false);
                var json = await GetJsonAsync(V3Endpoint + v3Query, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                merged = UnifiedIntroDbRawParser.MergePreferExisting(merged,
                    UnifiedIntroDbRawParser.ParseTheIntroDb(json, identity, "v3"));
            }

            return merged;
        }

        private static string AddDuration(string query, long? durationMs)
        {
            if (durationMs.HasValue && durationMs.Value > 0)
                return query + "&duration_ms=" + durationMs.Value.ToString(CultureInfo.InvariantCulture);
            return query;
        }

        private static bool HasIntro(UnifiedIntroDbDocument document)
        {
            return document?.IntroStartSeconds.HasValue == true && document.IntroEndSeconds.HasValue;
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
    }

    internal static class UnifiedIntroDbRawParser
    {
        private sealed class SegmentCandidate
        {
            public string Type { get; set; }
            public double? StartSeconds { get; set; }
            public double? EndSeconds { get; set; }
            public double? Confidence { get; set; }
            public int SubmissionCount { get; set; }
        }

        public static UnifiedIntroDbDocument ParseIntroDbSegments(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var intro = ParseNamedObject(json, "intro", "intro");
                var recap = ParseNamedObject(json, "recap", "recap");
                var outro = ParseNamedObject(json, "outro", "outro");

                var typed = new List<SegmentCandidate>();
                foreach (Match match in Regex.Matches(json, "\\{(?<body>[^{}]*)\\}", RegexOptions.Singleline))
                {
                    var body = match.Groups["body"].Value;
                    var type = GetString(body, "segment_type");
                    if (string.IsNullOrWhiteSpace(type)) continue;
                    var candidate = ParseSegmentObject(body, type);
                    if (candidate != null) typed.Add(candidate);
                }

                intro ??= Best(typed, "intro");
                recap ??= Best(typed, "recap");
                outro ??= Best(typed, "outro");
                if (intro == null && recap == null && outro == null) return null;

                var result = new UnifiedIntroDbDocument { Source = "IntroDB.app" };
                Apply(result, intro, "intro");
                Apply(result, recap, "recap");
                Apply(result, outro, "outro");
                result.IntroConfidence = NormalizeConfidence(result.IntroConfidence);
                result.CreditsConfidence = NormalizeConfidence(result.CreditsConfidence);
                result.RecapConfidence = NormalizeConfidence(result.RecapConfidence);
                result.Confidence = result.IntroConfidence;
                result.ExternalId = GetString(json, "imdb_id");
                return result;
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("IntroDB.app raw /segments parse failed: " + ex.Message);
                return null;
            }
        }

        public static UnifiedIntroDbDocument ParseIntroDbLegacy(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var start = ReadSeconds(json, "start", "start_ms") ?? ReadSeconds(json, "start_sec", "start_ms");
                var end = ReadSeconds(json, "end", "end_ms") ?? ReadSeconds(json, "end_sec", "end_ms");
                if (!start.HasValue || !end.HasValue) return null;
                var confidence = NormalizeConfidence(GetDouble(json, "confidence"));
                return new UnifiedIntroDbDocument
                {
                    IntroStartSeconds = start,
                    IntroEndSeconds = end,
                    IntroConfidence = confidence,
                    Confidence = confidence,
                    Source = "IntroDB.app",
                    ExternalId = GetString(json, "imdb_id")
                };
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("IntroDB.app raw /intro parse failed: " + ex.Message);
                return null;
            }
        }

        public static UnifiedIntroDbDocument ParseTheIntroDb(string json, UnifiedIntroDbIdentity identity, string apiVersion)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var intro = Best(ParseArray(json, "intro"), null);
                var recap = Best(ParseArray(json, "recap"), null);
                var preview = Best(ParseArray(json, "preview"), null);
                var credits = ParseArray(json, "credits")
                    .Where(v => v?.StartSeconds.HasValue == true)
                    .OrderByDescending(v => v.StartSeconds.Value)
                    .ThenByDescending(v => NormalizeConfidence(v.Confidence) ?? -1)
                    .ThenByDescending(v => v.SubmissionCount)
                    .FirstOrDefault();

                if (intro == null && recap == null && credits == null && preview == null) return null;

                var tmdb = GetLong(json, "tmdb_id");
                var imdb = GetString(json, "imdb_id");
                var result = new UnifiedIntroDbDocument
                {
                    Source = "TheIntroDB.org " + apiVersion,
                    ExternalId = tmdb.HasValue && tmdb.Value > 0
                        ? tmdb.Value.ToString(CultureInfo.InvariantCulture)
                        : (!string.IsNullOrWhiteSpace(imdb)
                            ? imdb
                            : (identity?.SeriesTmdbId ?? identity?.SeriesImdbId ?? identity?.EpisodeTmdbId ?? identity?.EpisodeImdbId))
                };
                Apply(result, intro, "intro");
                Apply(result, recap, "recap");
                Apply(result, credits, "credits");
                Apply(result, preview, "preview");
                result.IntroConfidence = NormalizeConfidence(result.IntroConfidence);
                result.CreditsConfidence = NormalizeConfidence(result.CreditsConfidence);
                result.RecapConfidence = NormalizeConfidence(result.RecapConfidence);
                result.PreviewConfidence = NormalizeConfidence(result.PreviewConfidence);
                result.Confidence = result.IntroConfidence;
                return result;
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("TheIntroDB raw " + apiVersion + " parse failed: " + ex.Message);
                return null;
            }
        }

        public static UnifiedIntroDbDocument MergePreferExisting(UnifiedIntroDbDocument existing,
            UnifiedIntroDbDocument incoming)
        {
            if (incoming == null) return existing;
            if (existing == null) return incoming;

            if (!existing.IntroStartSeconds.HasValue && incoming.IntroStartSeconds.HasValue && incoming.IntroEndSeconds.HasValue)
            {
                existing.IntroStartSeconds = incoming.IntroStartSeconds;
                existing.IntroEndSeconds = incoming.IntroEndSeconds;
                existing.IntroConfidence = incoming.IntroConfidence ?? incoming.Confidence;
                existing.Confidence = existing.IntroConfidence;
                if (string.IsNullOrWhiteSpace(existing.ExternalId)) existing.ExternalId = incoming.ExternalId;
            }
            if (!existing.CreditsStartSeconds.HasValue && incoming.CreditsStartSeconds.HasValue)
            {
                existing.CreditsStartSeconds = incoming.CreditsStartSeconds;
                existing.CreditsEndSeconds = incoming.CreditsEndSeconds;
                existing.CreditsConfidence = incoming.CreditsConfidence;
            }
            if (!existing.RecapEndSeconds.HasValue && incoming.RecapEndSeconds.HasValue)
            {
                existing.RecapStartSeconds = incoming.RecapStartSeconds;
                existing.RecapEndSeconds = incoming.RecapEndSeconds;
                existing.RecapConfidence = incoming.RecapConfidence;
            }
            if (!existing.PreviewStartSeconds.HasValue && incoming.PreviewStartSeconds.HasValue)
            {
                existing.PreviewStartSeconds = incoming.PreviewStartSeconds;
                existing.PreviewEndSeconds = incoming.PreviewEndSeconds;
                existing.PreviewConfidence = incoming.PreviewConfidence;
            }

            if (!string.IsNullOrWhiteSpace(incoming.Source) &&
                !string.Equals(existing.Source, incoming.Source, StringComparison.OrdinalIgnoreCase))
                existing.Source = string.IsNullOrWhiteSpace(existing.Source)
                    ? incoming.Source
                    : existing.Source + " + " + incoming.Source;
            return existing;
        }

        private static SegmentCandidate ParseNamedObject(string json, string propertyName, string type)
        {
            var pattern = "\\\"" + Regex.Escape(propertyName) + "\\\"\\s*:\\s*\\{(?<body>[^{}]*)\\}";
            var match = Regex.Match(json, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? ParseSegmentObject(match.Groups["body"].Value, type) : null;
        }

        private static List<SegmentCandidate> ParseArray(string json, string propertyName)
        {
            var result = new List<SegmentCandidate>();
            var pattern = "\\\"" + Regex.Escape(propertyName) + "\\\"\\s*:\\s*\\[(?<body>.*?)\\]";
            var section = Regex.Match(json, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!section.Success) return result;

            foreach (Match match in Regex.Matches(section.Groups["body"].Value,
                         "\\{(?<body>[^{}]*)\\}", RegexOptions.Singleline))
            {
                var candidate = ParseSegmentObject(match.Groups["body"].Value, propertyName);
                if (candidate != null) result.Add(candidate);
            }
            return result;
        }

        private static SegmentCandidate ParseSegmentObject(string body, string type)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            var start = ReadSeconds(body, "start_sec", "start_ms") ?? ReadSeconds(body, "start", "start_ms");
            var end = ReadSeconds(body, "end_sec", "end_ms") ?? ReadSeconds(body, "end", "end_ms");
            if (!start.HasValue && !end.HasValue) return null;
            return new SegmentCandidate
            {
                Type = type,
                StartSeconds = start,
                EndSeconds = end,
                Confidence = GetDouble(body, "confidence"),
                SubmissionCount = GetInt(body, "submission_count") ?? 0
            };
        }

        private static SegmentCandidate Best(IEnumerable<SegmentCandidate> items, string type)
        {
            var query = (items ?? Enumerable.Empty<SegmentCandidate>()).Where(v => v != null);
            if (!string.IsNullOrWhiteSpace(type))
                query = query.Where(v => string.Equals(v.Type, type, StringComparison.OrdinalIgnoreCase));
            return query
                .OrderByDescending(v => NormalizeConfidence(v.Confidence) ?? -1)
                .ThenByDescending(v => v.SubmissionCount)
                .FirstOrDefault();
        }

        private static void Apply(UnifiedIntroDbDocument target, SegmentCandidate item, string type)
        {
            if (target == null || item == null) return;
            var confidence = NormalizeConfidence(item.Confidence);
            switch (type)
            {
                case "intro":
                    target.IntroStartSeconds = item.StartSeconds ?? 0;
                    target.IntroEndSeconds = item.EndSeconds;
                    target.IntroConfidence = confidence;
                    break;
                case "recap":
                    target.RecapStartSeconds = item.StartSeconds ?? 0;
                    target.RecapEndSeconds = item.EndSeconds;
                    target.RecapConfidence = confidence;
                    break;
                case "outro":
                case "credits":
                    target.CreditsStartSeconds = item.StartSeconds;
                    target.CreditsEndSeconds = item.EndSeconds;
                    target.CreditsConfidence = confidence;
                    break;
                case "preview":
                    target.PreviewStartSeconds = item.StartSeconds;
                    target.PreviewEndSeconds = item.EndSeconds;
                    target.PreviewConfidence = confidence;
                    break;
            }
        }

        private static double? ReadSeconds(string json, string secondsKey, string millisecondsKey)
        {
            var rawSeconds = GetRaw(json, secondsKey);
            if (TryParseSeconds(rawSeconds, out var seconds)) return seconds;

            var rawMilliseconds = GetRaw(json, millisecondsKey);
            if (TryParseNumber(rawMilliseconds, out var milliseconds) && milliseconds >= 0)
                return milliseconds / 1000d;
            return null;
        }

        private static bool TryParseSeconds(string raw, out double seconds)
        {
            seconds = 0;
            var text = Unquote(raw);
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
                return false;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                return seconds >= 0;

            var parts = text.Split(':');
            if (parts.Length != 2 && parts.Length != 3) return false;
            var values = new double[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || values[i] < 0)
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

        private static double? GetDouble(string json, string key)
        {
            return TryParseNumber(GetRaw(json, key), out var value) ? value : (double?)null;
        }

        private static int? GetInt(string json, string key)
        {
            return TryParseNumber(GetRaw(json, key), out var value) && value >= int.MinValue && value <= int.MaxValue
                ? (int)Math.Round(value)
                : (int?)null;
        }

        private static long? GetLong(string json, string key)
        {
            return TryParseNumber(GetRaw(json, key), out var value) && value >= long.MinValue && value <= long.MaxValue
                ? (long)Math.Round(value)
                : (long?)null;
        }

        private static string GetString(string json, string key)
        {
            var raw = GetRaw(json, key);
            var value = Unquote(raw);
            return string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) ? null : value;
        }

        private static string GetRaw(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key)) return null;
            var pattern = "\\\"" + Regex.Escape(key) +
                          "\\\"\\s*:\\s*(?<value>\\\"(?:\\\\.|[^\\\"\\\\])*\\\"|null|-?\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?)";
            var match = Regex.Match(json, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? match.Groups["value"].Value : null;
        }

        private static bool TryParseNumber(string raw, out double value)
        {
            value = 0;
            var text = Unquote(raw);
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
                return false;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string Unquote(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var text = raw.Trim();
            if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
                text = text.Substring(1, text.Length - 2);
            return text.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        private static double? NormalizeConfidence(double? value)
        {
            if (!value.HasValue) return null;
            var confidence = value.Value;
            if (confidence > 1 && confidence <= 100) confidence /= 100d;
            return Math.Max(0, Math.Min(confidence, 1));
        }
    }
}
