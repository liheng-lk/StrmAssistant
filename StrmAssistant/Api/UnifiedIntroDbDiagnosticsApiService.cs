using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.IntroSkip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    public sealed class UnifiedIntroDbProviderProbeResult
    {
        public string Provider { get; set; }
        public bool Enabled { get; set; }
        public string Attempt { get; set; }
        public string Endpoint { get; set; }
        public int? StatusCode { get; set; }
        public bool Success { get; set; }
        public string Outcome { get; set; }
        public string BodyPreview { get; set; }
        public string Error { get; set; }
    }

    public sealed class UnifiedIntroDbDiagnosticsResult
    {
        public bool Success { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public UnifiedIntroDbIdentity Identity { get; set; }
        public List<UnifiedIntroDbProviderProbeResult> Providers { get; set; } =
            new List<UnifiedIntroDbProviderProbeResult>();
        public string Error { get; set; }
    }

    [Route("/StrmAssistant/IntroDb/{Id}/Diagnostics", "GET",
        Summary = "Probe Unified IntroDb providers from the Emby server without browser CORS restrictions")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetUnifiedIntroDbDiagnostics : IReturn<UnifiedIntroDbDiagnosticsResult>
    {
        public string Id { get; set; }
    }

    public sealed class UnifiedIntroDbDiagnosticsApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IHttpClient _httpClient;
        private readonly UnifiedIntroDbBridge _bridge;

        public UnifiedIntroDbDiagnosticsApiService(ILibraryManager libraryManager, IHttpClient httpClient,
            MediaBrowser.Model.Serialization.IJsonSerializer jsonSerializer)
        {
            _libraryManager = libraryManager;
            _httpClient = httpClient;
            _bridge = new UnifiedIntroDbBridge(httpClient, jsonSerializer);
        }

        public async Task<object> Get(GetUnifiedIntroDbDiagnostics request)
        {
            var result = new UnifiedIntroDbDiagnosticsResult { ItemId = request?.Id };
            var episode = ResolveEpisode(request?.Id);
            if (episode == null)
            {
                result.Error = "Episode was not found.";
                return result;
            }

            result.ItemName = episode.Name;
            result.Identity = _bridge.ResolveIdentity(episode);
            var options = UnifiedIntroDbRuntimeSettings.GetSnapshot();

            await ProbeIntroDbAppAsync(result, options, CancellationToken.None).ConfigureAwait(false);
            await ProbeTheIntroDbAsync(result, options, CancellationToken.None).ConfigureAwait(false);
            await ProbeCustomAsync(result, options, CancellationToken.None).ConfigureAwait(false);

            result.Success = result.Providers.Exists(v => v.Enabled && v.Success);
            if (!result.Success)
                result.Error = "No enabled provider probe returned HTTP 2xx. Review Providers for per-source details.";
            return result;
        }

        private async Task ProbeIntroDbAppAsync(UnifiedIntroDbDiagnosticsResult result, UnifiedIntroDbOptions options,
            CancellationToken cancellationToken)
        {
            if (!options.IntroDbAppEnabled)
            {
                result.Providers.Add(Disabled("IntroDB.app"));
                return;
            }

            var identity = result.Identity;
            if (identity == null || string.IsNullOrWhiteSpace(identity.SeriesImdbId) ||
                !identity.SeasonNumber.HasValue || !identity.EpisodeNumber.HasValue)
            {
                result.Providers.Add(IdentityError("IntroDB.app",
                    "Series IMDb ID + season + episode is required."));
                return;
            }

            var query = "?imdb_id=" + Uri.EscapeDataString(identity.SeriesImdbId) +
                        "&season=" + identity.SeasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        "&episode=" + identity.EpisodeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var primary = await ProbeAsync("IntroDB.app", "segments",
                    "https://api.introdb.app/segments" + query, options.TimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);
            result.Providers.Add(primary);

            if (!primary.Success)
            {
                var legacy = await ProbeAsync("IntroDB.app", "legacy-intro",
                        "https://api.introdb.app/intro" + query, options.TimeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
                result.Providers.Add(legacy);
            }
        }

        private async Task ProbeTheIntroDbAsync(UnifiedIntroDbDiagnosticsResult result, UnifiedIntroDbOptions options,
            CancellationToken cancellationToken)
        {
            if (!options.TheIntroDbEnabled)
            {
                result.Providers.Add(Disabled("TheIntroDB.org"));
                return;
            }

            var identity = result.Identity;
            if (identity == null || !identity.SeasonNumber.HasValue || !identity.EpisodeNumber.HasValue)
            {
                result.Providers.Add(IdentityError("TheIntroDB.org", "Season + episode is required."));
                return;
            }

            var hasSeriesTmdb = long.TryParse(identity.SeriesTmdbId, out var seriesTmdb) && seriesTmdb > 0;
            var hasSeriesImdb = !string.IsNullOrWhiteSpace(identity.SeriesImdbId);
            if (!hasSeriesTmdb && !hasSeriesImdb)
            {
                result.Providers.Add(IdentityError("TheIntroDB.org",
                    "Series TMDB or Series IMDb ID is required."));
                return;
            }

            var baseQuery = hasSeriesTmdb
                ? "?tmdb_id=" + seriesTmdb.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "?imdb_id=" + Uri.EscapeDataString(identity.SeriesImdbId);
            baseQuery += "&season=" + identity.SeasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                         "&episode=" + identity.EpisodeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var v3Query = baseQuery;
            if (identity.DurationMs.HasValue && identity.DurationMs.Value > 0)
                v3Query += "&duration_ms=" + identity.DurationMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var primary = await ProbeAsync("TheIntroDB.org", "v3-series-id",
                    "https://api.theintrodb.org/v3/media" + v3Query, options.TimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);
            result.Providers.Add(primary);

            if (!primary.Success && hasSeriesTmdb)
            {
                var v2 = await ProbeAsync("TheIntroDB.org", "v2-series-id-fallback",
                        "https://api.theintrodb.org/v2/media" + baseQuery, options.TimeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
                result.Providers.Add(v2);
            }
        }

        private async Task ProbeCustomAsync(UnifiedIntroDbDiagnosticsResult result, UnifiedIntroDbOptions options,
            CancellationToken cancellationToken)
        {
            if (!options.CustomProviderEnabled)
            {
                result.Providers.Add(Disabled("Custom"));
                return;
            }
            if (string.IsNullOrWhiteSpace(options.EndpointTemplate))
            {
                result.Providers.Add(IdentityError("Custom", "EndpointTemplate is empty."));
                return;
            }

            var url = UnifiedIntroDbBridge.BuildUrl(options.EndpointTemplate, result.Identity, out var error);
            if (url == null)
            {
                result.Providers.Add(IdentityError("Custom", error ?? "The custom endpoint could not be built."));
                return;
            }
            result.Providers.Add(await ProbeAsync("Custom", "configured-endpoint", url, options.TimeoutSeconds,
                    cancellationToken).ConfigureAwait(false));
        }

        private async Task<UnifiedIntroDbProviderProbeResult> ProbeAsync(string provider, string attempt, string url,
            int timeoutSeconds, CancellationToken cancellationToken)
        {
            var result = new UnifiedIntroDbProviderProbeResult
            {
                Provider = provider,
                Enabled = true,
                Attempt = attempt,
                Endpoint = url
            };

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
                using var response = await _httpClient.SendAsync(request, "GET").ConfigureAwait(false);
                result.StatusCode = (int)response.StatusCode;
                result.Success = result.StatusCode >= 200 && result.StatusCode < 300;
                result.Outcome = result.Success ? "HTTP 2xx" : "HTTP " + result.StatusCode;
                await using var stream = response.Content;
                using var reader = new StreamReader(stream);
                result.BodyPreview = Trim(await reader.ReadToEndAsync().ConfigureAwait(false), 2000);
            }
            catch (OperationCanceledException)
            {
                result.Outcome = "Timeout";
                result.Error = "Request timed out.";
            }
            catch (Exception ex)
            {
                result.Outcome = "Request error";
                result.Error = ex.Message;
            }
            return result;
        }

        private static UnifiedIntroDbProviderProbeResult Disabled(string provider)
        {
            return new UnifiedIntroDbProviderProbeResult
            {
                Provider = provider,
                Enabled = false,
                Success = false,
                Outcome = "Disabled"
            };
        }

        private static UnifiedIntroDbProviderProbeResult IdentityError(string provider, string error)
        {
            return new UnifiedIntroDbProviderProbeResult
            {
                Provider = provider,
                Enabled = true,
                Success = false,
                Outcome = "Identity/configuration incomplete",
                Error = error
            };
        }

        private static string Trim(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            return value.Substring(0, maxLength) + "...";
        }

        private Episode ResolveEpisode(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !long.TryParse(id, out var internalId)) return null;
            return _libraryManager.GetItemById(internalId) as Episode;
        }
    }
}
