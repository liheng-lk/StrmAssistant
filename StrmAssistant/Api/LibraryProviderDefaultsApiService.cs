using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Metadata;
using System.Collections.Generic;

namespace StrmAssistant.Api
{
    public sealed class LibraryProviderDefaultsSettingsStatus
    {
        public LibraryProviderDefaultsOptions Options { get; set; }
        public string SettingsPath { get; set; }
        public List<string> KnownLibraryIds { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/Metadata/LibraryProviderDefaults", "GET",
        Summary = "Get default metadata/image provider policy settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetLibraryProviderDefaultsSettings : IReturn<LibraryProviderDefaultsSettingsStatus>
    {
    }

    [Route("/StrmAssistant/Metadata/LibraryProviderDefaults", "POST",
        Summary = "Update default metadata/image provider policy settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class SaveLibraryProviderDefaultsSettings : IReturn<LibraryProviderDefaultsSettingsStatus>
    {
        public bool Enabled { get; set; }
        public string ProviderName { get; set; } = "TheMovieDb";
        public bool ApplyMetadataFetcher { get; set; } = true;
        public bool ApplyImageFetcher { get; set; } = true;
        public bool OnlyWhenFetcherListEmpty { get; set; } = true;
        public string CollectionTypes { get; set; } = "movies,tvshows";
    }

    [Route("/StrmAssistant/Metadata/LibraryProviderDefaults/{Id}/Plan", "GET",
        Summary = "Preview default-provider changes for one virtual library")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetLibraryProviderDefaultsPlan : IReturn<LibraryProviderDefaultsPlan>
    {
        public string Id { get; set; }
    }

    [Route("/StrmAssistant/Metadata/LibraryProviderDefaults/{Id}/Apply", "POST",
        Summary = "Apply default-provider policy to one virtual library after explicit confirmation")]
    [Authenticated(Roles = "Admin")]
    public sealed class ApplyLibraryProviderDefaults : IReturn<LibraryProviderDefaultsApplyResult>
    {
        public string Id { get; set; }
        public bool Confirm { get; set; }
    }

    public sealed class LibraryProviderDefaultsApiService : BaseApiService
    {
        private readonly LibraryProviderDefaultsService _service;

        public LibraryProviderDefaultsApiService(ILibraryManager libraryManager)
        {
            _service = new LibraryProviderDefaultsService(libraryManager);
        }

        public object Get(GetLibraryProviderDefaultsSettings request)
        {
            return BuildStatus();
        }

        public object Post(SaveLibraryProviderDefaultsSettings request)
        {
            LibraryProviderDefaultsRuntimeSettings.Save(new LibraryProviderDefaultsOptions
            {
                Enabled = request?.Enabled == true,
                ProviderName = request?.ProviderName,
                ApplyMetadataFetcher = request?.ApplyMetadataFetcher != false,
                ApplyImageFetcher = request?.ApplyImageFetcher != false,
                OnlyWhenFetcherListEmpty = request?.OnlyWhenFetcherListEmpty != false,
                CollectionTypes = request?.CollectionTypes
            });
            return BuildStatus();
        }

        public object Get(GetLibraryProviderDefaultsPlan request)
        {
            return _service.BuildPlan(request?.Id);
        }

        public object Post(ApplyLibraryProviderDefaults request)
        {
            return _service.Apply(request?.Id, request?.Confirm == true);
        }

        private LibraryProviderDefaultsSettingsStatus BuildStatus()
        {
            var options = LibraryProviderDefaultsRuntimeSettings.GetSnapshot();
            var status = new LibraryProviderDefaultsSettingsStatus
            {
                Options = options,
                SettingsPath = LibraryProviderDefaultsRuntimeSettings.SettingsPath,
                KnownLibraryIds = new List<string>(_service.GetVirtualFolderIds())
            };
            if (options.Enabled && string.IsNullOrWhiteSpace(options.ProviderName))
                status.Warnings.Add("The automatic default-provider policy is enabled but ProviderName is empty.");
            if (options.Enabled)
                status.Warnings.Add("Automatic mode applies only to libraries first observed after the monitor baseline; existing libraries require explicit Plan/Apply.");
            return status;
        }
    }
}
