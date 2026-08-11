using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Metadata
{
    public sealed class DoubanAssistDocument
    {
        public string Name { get; set; }
        public string OriginalTitle { get; set; }
        public string Overview { get; set; }
        public string Tagline { get; set; }
        public int? ProductionYear { get; set; }
        public string PremiereDate { get; set; }
        public List<string> Genres { get; set; } = new List<string>();
        public Dictionary<string, string> ProviderIds { get; set; } = new Dictionary<string, string>();
        public string DoubanId { get; set; }
        public string SourceUrl { get; set; }
    }

    public sealed class DoubanAssistRequestIdentity
    {
        public string Type { get; set; }
        public string TmdbId { get; set; }
        public string ImdbId { get; set; }
        public string ItemName { get; set; }
    }

    public sealed class DoubanAssistBridge
    {
        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _jsonSerializer;

        public DoubanAssistBridge(IHttpClient httpClient, IJsonSerializer jsonSerializer)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
        }

        public DoubanAssistRequestIdentity ResolveIdentity(BaseItem item)
        {
            if (item == null) return null;
            var type = item is Movie ? "movie" : item is Series ? "tv" : null;
            if (type == null) return null;
            return new DoubanAssistRequestIdentity
            {
                Type = type,
                TmdbId = SafeProviderId(item, MetadataProviders.Tmdb.ToString()),
                ImdbId = SafeProviderId(item, MetadataProviders.Imdb.ToString()),
                ItemName = item.Name
            };
        }

        public static DoubanAssistRequestIdentity ResolveIdentityFromLookup(object[] args, string type)
        {
            if (args == null) return null;
            ItemLookupInfo lookup = null;
            foreach (var arg in args)
            {
                if (arg is ItemLookupInfo direct)
                {
                    lookup = direct;
                    break;
                }
                if (arg == null) continue;
                try
                {
                    var candidate = arg.GetType().GetProperty("SearchInfo")?.GetValue(arg);
                    if (candidate is ItemLookupInfo typed)
                    {
                        lookup = typed;
                        break;
                    }
                }
                catch { }
            }
            if (lookup == null) return null;

            return new DoubanAssistRequestIdentity
            {
                Type = type,
                TmdbId = ReadProviderId(lookup.ProviderIds, MetadataProviders.Tmdb.ToString()),
                ImdbId = ReadProviderId(lookup.ProviderIds, MetadataProviders.Imdb.ToString()),
                ItemName = lookup.Name
            };
        }

        public async Task<DoubanAssistDocument> FetchAsync(DoubanAssistRequestIdentity identity,
            CancellationToken cancellationToken)
        {
            var options = DoubanAssistRuntimeSettings.GetSnapshot();
            if (!options.Enabled || identity == null) return null;
            if (identity.Type == "movie" && !options.EnableMovies) return null;
            if (identity.Type == "tv" && !options.EnableSeries) return null;

            var url = BuildUrl(options.EndpointTemplate, identity, out var error);
            if (url == null)
            {
                if (Plugin.Instance?.DebugMode == true && !string.IsNullOrWhiteSpace(error))
                    Plugin.Instance.Logger.Debug("Douban Assist URL skipped: " + error);
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
                return _jsonSerializer.DeserializeFromStream<DoubanAssistDocument>(stream);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Douban Assist request failed: " + ex.Message);
                return null;
            }
        }

        public static string BuildUrl(string template, DoubanAssistRequestIdentity identity, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(template))
            {
                error = "EndpointTemplate is empty.";
                return null;
            }
            if (identity == null ||
                (string.IsNullOrWhiteSpace(identity.TmdbId) && string.IsNullOrWhiteSpace(identity.ImdbId)))
            {
                error = "No TMDB or IMDb provider ID is available.";
                return null;
            }

            var url = template.Trim()
                .Replace("{type}", Escape(identity.Type))
                .Replace("{tmdb}", Escape(identity.TmdbId))
                .Replace("{imdb}", Escape(identity.ImdbId))
                .Replace("{name}", Escape(identity.ItemName));

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = "EndpointTemplate did not produce an HTTP/HTTPS absolute URL.";
                return null;
            }
            return url;
        }

        private static string Escape(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty);
        }

        private static string SafeProviderId(BaseItem item, string key)
        {
            try { return item.GetProviderId(key); } catch { return null; }
        }

        private static string ReadProviderId(IDictionary<string, string> ids, string key)
        {
            if (ids == null || string.IsNullOrWhiteSpace(key)) return null;
            if (ids.TryGetValue(key, out var direct)) return direct;
            return ids.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        }
    }
}
