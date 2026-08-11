using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using StrmAssistant.Metadata;
using System.Collections.Generic;

namespace StrmAssistant.Api
{
    public sealed class LocalTmdbMetadataSettingsStatus
    {
        public LocalTmdbMetadataOptions Options { get; set; }
        public string SettingsPath { get; set; }
        public LocalTmdbMetadataCapabilityStatus RuntimePatch { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class LocalTmdbMetadataPreviewResult
    {
        public bool Success { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public LocalTmdbMetadataIdentity Identity { get; set; }
        public string ResolvedPath { get; set; }
        public bool Exists { get; set; }
        public LocalTmdbMetadataDocument Document { get; set; }
        public string Error { get; set; }
    }

    [Route("/StrmAssistant/Metadata/LocalTmdb", "GET",
        Summary = "Get Local TMDB metadata-source settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetLocalTmdbMetadataSettings : IReturn<LocalTmdbMetadataSettingsStatus>
    {
    }

    [Route("/StrmAssistant/Metadata/LocalTmdb", "POST",
        Summary = "Update Local TMDB metadata-source settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class SaveLocalTmdbMetadataSettings : IReturn<LocalTmdbMetadataSettingsStatus>
    {
        public bool Enabled { get; set; }
        public string RootPath { get; set; }
        public bool OnlyFillMissingFields { get; set; } = true;
        public bool EnableMovies { get; set; } = true;
        public bool EnableSeries { get; set; } = true;
        public bool EnableSeasons { get; set; } = true;
        public bool EnableEpisodes { get; set; } = true;
        public bool EnablePeople { get; set; }
    }

    [Route("/StrmAssistant/Metadata/LocalTmdb/{Id}/Preview", "GET",
        Summary = "Resolve and preview one item's Local TMDB JSON without refreshing metadata")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetLocalTmdbMetadataPreview : IReturn<LocalTmdbMetadataPreviewResult>
    {
        public string Id { get; set; }
    }

    public sealed class LocalTmdbMetadataApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly LocalTmdbMetadataStore _store;

        public LocalTmdbMetadataApiService(ILibraryManager libraryManager, IJsonSerializer jsonSerializer)
        {
            _libraryManager = libraryManager;
            _store = new LocalTmdbMetadataStore(jsonSerializer);
        }

        public object Get(GetLocalTmdbMetadataSettings request)
        {
            return BuildStatus();
        }

        public object Post(SaveLocalTmdbMetadataSettings request)
        {
            LocalTmdbMetadataRuntimeSettings.Save(new LocalTmdbMetadataOptions
            {
                Enabled = request?.Enabled == true,
                RootPath = request?.RootPath,
                OnlyFillMissingFields = request?.OnlyFillMissingFields != false,
                EnableMovies = request?.EnableMovies != false,
                EnableSeries = request?.EnableSeries != false,
                EnableSeasons = request?.EnableSeasons != false,
                EnableEpisodes = request?.EnableEpisodes != false,
                EnablePeople = request?.EnablePeople == true
            });
            return BuildStatus();
        }

        public object Get(GetLocalTmdbMetadataPreview request)
        {
            var result = new LocalTmdbMetadataPreviewResult { ItemId = request?.Id };
            var item = ResolveItem(request?.Id);
            if (item == null)
            {
                result.Error = "Media item was not found.";
                return result;
            }

            result.ItemName = item.Name;
            result.Identity = _store.ResolveIdentity(item);
            var found = _store.TryRead(item, out var identity, out var document, out var path, out var error);
            result.Identity = identity ?? result.Identity;
            result.ResolvedPath = path;
            result.Exists = found;
            result.Document = document;
            result.Error = found ? null : error;
            result.Success = found;
            return result;
        }

        private LocalTmdbMetadataSettingsStatus BuildStatus()
        {
            var options = LocalTmdbMetadataRuntimeSettings.GetSnapshot();
            var status = new LocalTmdbMetadataSettingsStatus
            {
                Options = options,
                SettingsPath = LocalTmdbMetadataRuntimeSettings.SettingsPath,
                RuntimePatch = LocalTmdbMetadataModState.Status
            };
            if (options.Enabled && string.IsNullOrWhiteSpace(options.RootPath))
                status.Warnings.Add("Local TMDB is enabled but RootPath is empty.");
            if (options.Enabled && status.RuntimePatch?.PatchedProviders == 0)
                status.Warnings.Add("Local TMDB is enabled but no compatible MovieDb metadata provider was patched.");
            return status;
        }

        private BaseItem ResolveItem(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !long.TryParse(id, out var internalId)) return null;
            return _libraryManager.GetItemById(internalId);
        }
    }
}
