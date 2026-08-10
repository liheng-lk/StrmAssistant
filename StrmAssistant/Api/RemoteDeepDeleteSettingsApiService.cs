using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Experience;
using System;
using System.Collections.Generic;

namespace StrmAssistant.Api
{
    public sealed class RemoteDeepDeleteSettingsView
    {
        public bool Enabled { get; set; }
        public string Provider { get; set; }
        public string BaseUrl { get; set; }
        public bool HasAccessToken { get; set; }
        public string Username { get; set; }
        public bool HasPassword { get; set; }
        public string PathMappings { get; set; }
        public string AllowedRemoteRoots { get; set; }
        public int TimeoutSeconds { get; set; }
        public bool TreatNotFoundAsSuccess { get; set; }
        public bool DeleteAssociatedSidecars { get; set; }
        public string SettingsPath { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/DeepDelete/Remote/Settings", "GET",
        Summary = "Read redacted remote deep-delete provider settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetRemoteDeepDeleteSettings : IReturn<RemoteDeepDeleteSettingsView> { }

    [Route("/StrmAssistant/DeepDelete/Remote/Settings", "POST",
        Summary = "Save remote deep-delete provider settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class SaveRemoteDeepDeleteSettings : IReturn<RemoteDeepDeleteSettingsView>
    {
        public bool Confirm { get; set; }
        public bool Enabled { get; set; }
        public string Provider { get; set; }
        public string BaseUrl { get; set; }
        public string AccessToken { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string PathMappings { get; set; }
        public string AllowedRemoteRoots { get; set; }
        public int TimeoutSeconds { get; set; } = 30;
        public bool TreatNotFoundAsSuccess { get; set; } = true;
        public bool DeleteAssociatedSidecars { get; set; }
        public bool ClearAccessToken { get; set; }
        public bool ClearPassword { get; set; }
    }

    [Route("/StrmAssistant/DeepDelete/{Id}/RemotePlan", "GET",
        Summary = "Preview remote provider path mapping without deleting anything")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetRemoteDeepDeletePlan : IReturn<RemoteDeepDeletePlan>
    {
        public string Id { get; set; }
    }

    public sealed class RemoteDeepDeleteSettingsApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly RemoteDeepDeleteService _remoteService = new RemoteDeepDeleteService();

        public RemoteDeepDeleteSettingsApiService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public object Get(GetRemoteDeepDeleteSettings request)
        {
            return ToView(RemoteDeepDeleteRuntimeSettings.GetSnapshot());
        }

        public object Post(SaveRemoteDeepDeleteSettings request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (!Enum.TryParse(request.Provider ?? string.Empty, true, out RemoteDeepDeleteProviderType provider))
                provider = RemoteDeepDeleteProviderType.None;

            var current = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            var next = new RemoteDeepDeleteOptions
            {
                Enabled = request.Enabled,
                Provider = provider,
                BaseUrl = request.BaseUrl,
                AccessToken = request.ClearAccessToken ? string.Empty : request.AccessToken ?? current.AccessToken,
                Username = request.Username ?? current.Username,
                Password = request.ClearPassword ? string.Empty : request.Password ?? current.Password,
                PathMappings = request.PathMappings,
                AllowedRemoteRoots = request.AllowedRemoteRoots,
                TimeoutSeconds = request.TimeoutSeconds,
                TreatNotFoundAsSuccess = request.TreatNotFoundAsSuccess,
                DeleteAssociatedSidecars = request.DeleteAssociatedSidecars
            };

            ValidateBeforeSave(next, request.Confirm);
            return ToView(RemoteDeepDeleteRuntimeSettings.Save(next));
        }

        public object Get(GetRemoteDeepDeletePlan request)
        {
            var item = ResolveItem(request?.Id);
            return item == null
                ? new RemoteDeepDeletePlan { Error = "Item was not found or id is invalid." }
                : _remoteService.BuildPlan(item);
        }

        private static void ValidateBeforeSave(RemoteDeepDeleteOptions next, bool confirm)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            if (!next.Enabled)
            {
                if (next.DeleteAssociatedSidecars)
                    throw new InvalidOperationException("Remote sidecar deletion cannot remain enabled while remote deep delete is disabled.");
                return;
            }

            if (!confirm)
                throw new InvalidOperationException("Enabling or changing active remote deletion settings requires Confirm=true.");
            if (Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions?.EnableDeepDelete != true)
                throw new InvalidOperationException("The master Deep Delete option must be enabled before remote deletion can be enabled.");
            if (next.Provider == RemoteDeepDeleteProviderType.None)
                throw new InvalidOperationException("A remote deletion provider must be selected.");
            if (!Uri.TryCreate(next.BaseUrl, UriKind.Absolute, out var baseUri) ||
                (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("Remote deletion BaseUrl must be an absolute HTTP/HTTPS URL.");
            if (RemoteDeepDeleteRuntimeSettings.ParseAllowedRoots(next.AllowedRemoteRoots).Count == 0)
                throw new InvalidOperationException("At least one allowed remote root must be configured before enabling destructive remote calls.");
            if (next.Provider == RemoteDeepDeleteProviderType.OpenList && string.IsNullOrWhiteSpace(next.AccessToken))
                throw new InvalidOperationException("OpenList remote deletion requires a non-empty AccessToken.");

            if (next.DeleteAssociatedSidecars)
            {
                if (next.Provider != RemoteDeepDeleteProviderType.OpenList)
                    throw new InvalidOperationException("Associated remote sidecar deletion is supported only with the OpenList provider.");
                if (!next.TreatNotFoundAsSuccess)
                    throw new InvalidOperationException(
                        "Associated remote sidecar deletion requires TreatNotFoundAsSuccess=true so a partially completed transaction can be retried idempotently after the main remote file is already gone.");
            }
        }

        private MediaBrowser.Controller.Entities.BaseItem ResolveItem(string id)
        {
            return long.TryParse(id, out var internalId) ? _libraryManager.GetItemById(internalId) : null;
        }

        private static RemoteDeepDeleteSettingsView ToView(RemoteDeepDeleteOptions options)
        {
            var view = new RemoteDeepDeleteSettingsView
            {
                Enabled = options.Enabled,
                Provider = options.Provider.ToString(),
                BaseUrl = options.BaseUrl,
                HasAccessToken = !string.IsNullOrWhiteSpace(options.AccessToken),
                Username = options.Username,
                HasPassword = !string.IsNullOrEmpty(options.Password),
                PathMappings = options.PathMappings,
                AllowedRemoteRoots = options.AllowedRemoteRoots,
                TimeoutSeconds = options.TimeoutSeconds,
                TreatNotFoundAsSuccess = options.TreatNotFoundAsSuccess,
                DeleteAssociatedSidecars = options.DeleteAssociatedSidecars,
                SettingsPath = RemoteDeepDeleteRuntimeSettings.SettingsPath
            };

            if (options.Enabled && options.Provider == RemoteDeepDeleteProviderType.OpenList && !view.HasAccessToken)
                view.Warnings.Add("OpenList is enabled but no AccessToken is stored.");
            if (options.Enabled && RemoteDeepDeleteRuntimeSettings.ParseMappings(options.PathMappings).Count == 0)
                view.Warnings.Add("No valid manual path mappings are configured. Same-origin OpenList /d/ targets may auto-map; other remote STRM targets remain blocked.");
            if (options.Enabled && RemoteDeepDeleteRuntimeSettings.ParseAllowedRoots(options.AllowedRemoteRoots).Count == 0)
                view.Warnings.Add("No allowed remote roots are configured; destructive remote calls remain blocked.");
            if (options.DeleteAssociatedSidecars && options.Provider != RemoteDeepDeleteProviderType.OpenList)
                view.Warnings.Add("Invalid saved state: remote sidecar deletion is supported only for OpenList. Disable it before destructive execution.");
            if (options.DeleteAssociatedSidecars && !options.TreatNotFoundAsSuccess)
                view.Warnings.Add("Invalid saved state: sidecar cleanup requires TreatNotFoundAsSuccess=true for safe idempotent retry.");
            if (options.DeleteAssociatedSidecars)
                view.Warnings.Add("Remote sidecar cleanup is destructive and intentionally conservative: only same-stem metadata/subtitle/image files from the actual OpenList directory listing are eligible, and the candidate set is frozen before main-file deletion.");
            return view;
        }
    }
}
