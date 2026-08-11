using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using StrmAssistant.Metadata;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    public sealed class DoubanAssistSettingsStatus
    {
        public DoubanAssistOptions Options { get; set; }
        public string SettingsPath { get; set; }
        public DoubanAssistCapabilityStatus RuntimePatch { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class DoubanAssistPreviewResult
    {
        public bool Success { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public DoubanAssistRequestIdentity Identity { get; set; }
        public string RequestUrl { get; set; }
        public DoubanAssistDocument Document { get; set; }
        public string Error { get; set; }
    }

    [Route("/StrmAssistant/Metadata/DoubanAssist", "GET",
        Summary = "Get configurable Douban-assist bridge settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetDoubanAssistSettings : IReturn<DoubanAssistSettingsStatus>
    {
    }

    [Route("/StrmAssistant/Metadata/DoubanAssist", "POST",
        Summary = "Update configurable Douban-assist bridge settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class SaveDoubanAssistSettings : IReturn<DoubanAssistSettingsStatus>
    {
        public bool Enabled { get; set; }
        public string EndpointTemplate { get; set; }
        public int TimeoutSeconds { get; set; } = 20;
        public bool OnlyFillMissingFields { get; set; } = true;
        public bool EnableMovies { get; set; } = true;
        public bool EnableSeries { get; set; } = true;
    }

    [Route("/StrmAssistant/Metadata/DoubanAssist/{Id}/Preview", "GET",
        Summary = "Call the configured Douban-assist endpoint for one item without writing metadata")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetDoubanAssistPreview : IReturn<DoubanAssistPreviewResult>
    {
        public string Id { get; set; }
    }

    public sealed class DoubanAssistApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly DoubanAssistBridge _bridge;

        public DoubanAssistApiService(ILibraryManager libraryManager, IHttpClient httpClient,
            IJsonSerializer jsonSerializer)
        {
            _libraryManager = libraryManager;
            _bridge = new DoubanAssistBridge(httpClient, jsonSerializer);
        }

        public object Get(GetDoubanAssistSettings request)
        {
            return BuildStatus();
        }

        public object Post(SaveDoubanAssistSettings request)
        {
            DoubanAssistRuntimeSettings.Save(new DoubanAssistOptions
            {
                Enabled = request?.Enabled == true,
                EndpointTemplate = request?.EndpointTemplate,
                TimeoutSeconds = request?.TimeoutSeconds ?? 20,
                OnlyFillMissingFields = request?.OnlyFillMissingFields != false,
                EnableMovies = request?.EnableMovies != false,
                EnableSeries = request?.EnableSeries != false
            });
            return BuildStatus();
        }

        public async Task<object> Get(GetDoubanAssistPreview request)
        {
            var result = new DoubanAssistPreviewResult { ItemId = request?.Id };
            var item = ResolveItem(request?.Id);
            if (item == null)
            {
                result.Error = "Movie or Series item was not found.";
                return result;
            }

            result.ItemName = item.Name;
            result.Identity = _bridge.ResolveIdentity(item);
            if (result.Identity == null)
            {
                result.Error = "Douban Assist currently supports Movie and Series items.";
                return result;
            }

            var options = DoubanAssistRuntimeSettings.GetSnapshot();
            result.RequestUrl = DoubanAssistBridge.BuildUrl(options.EndpointTemplate, result.Identity, out var error);
            if (result.RequestUrl == null)
            {
                result.Error = error;
                return result;
            }

            result.Document = await _bridge.FetchAsync(result.Identity, CancellationToken.None).ConfigureAwait(false);
            result.Success = result.Document != null;
            if (!result.Success) result.Error = "The configured bridge did not return a usable JSON document.";
            return result;
        }

        private DoubanAssistSettingsStatus BuildStatus()
        {
            var options = DoubanAssistRuntimeSettings.GetSnapshot();
            var status = new DoubanAssistSettingsStatus
            {
                Options = options,
                SettingsPath = DoubanAssistRuntimeSettings.SettingsPath,
                RuntimePatch = DoubanAssistModState.Status
            };
            if (options.Enabled && string.IsNullOrWhiteSpace(options.EndpointTemplate))
                status.Warnings.Add("Douban Assist is enabled but EndpointTemplate is empty.");
            if (options.Enabled && status.RuntimePatch?.PatchedProviders == 0)
                status.Warnings.Add("Douban Assist is enabled but no MovieDb Movie/Series GetMetadata provider was patched.");
            return status;
        }

        private BaseItem ResolveItem(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !long.TryParse(id, out var internalId)) return null;
            return _libraryManager.GetItemById(internalId);
        }
    }
}
