using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using StrmAssistant.Experience;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Api
{
    public sealed class ForcedLibraryViewInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string CollectionType { get; set; }
        public int ConfiguredOrder { get; set; } = -1;
    }

    public sealed class ForcedUserPreferencesStatus
    {
        public ForcedUserPreferencesOptions Options { get; set; }
        public string SettingsPath { get; set; }
        public ForcedUserPreferencesCapabilityStatus RuntimePatch { get; set; }
        public List<ForcedLibraryViewInfo> Libraries { get; set; } = new List<ForcedLibraryViewInfo>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/UserPreferences/Forced", "GET",
        Summary = "Get forced user preference settings and available library order ids")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetForcedUserPreferences : IReturn<ForcedUserPreferencesStatus> { }

    [Route("/StrmAssistant/UserPreferences/Forced", "POST",
        Summary = "Update forced server-side user preference settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class SaveForcedUserPreferences : IReturn<ForcedUserPreferencesStatus>
    {
        public bool Enabled { get; set; }
        public bool ForceLibraryOrder { get; set; }
        public string LibraryOrderIds { get; set; }
        public bool ForceDisplayMissingEpisodes { get; set; }
        public bool DisplayMissingEpisodes { get; set; } = true;
    }

    public sealed class ForcedUserPreferencesApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;

        public ForcedUserPreferencesApiService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public object Get(GetForcedUserPreferences request) => BuildStatus();

        public object Post(SaveForcedUserPreferences request)
        {
            ForcedUserPreferencesRuntimeSettings.Save(new ForcedUserPreferencesOptions
            {
                Enabled = request?.Enabled == true,
                ForceLibraryOrder = request?.ForceLibraryOrder == true,
                LibraryOrderIds = request?.LibraryOrderIds,
                ForceDisplayMissingEpisodes = request?.ForceDisplayMissingEpisodes == true,
                DisplayMissingEpisodes = request?.DisplayMissingEpisodes != false
            });
            return BuildStatus();
        }

        private ForcedUserPreferencesStatus BuildStatus()
        {
            var options = ForcedUserPreferencesRuntimeSettings.GetSnapshot();
            var order = ForcedUserPreferencesRuntimeSettings.GetLibraryOrderIds();
            var rank = order.Select((id, index) => new { id, index })
                .ToDictionary(entry => entry.id, entry => entry.index, System.StringComparer.OrdinalIgnoreCase);

            var result = new ForcedUserPreferencesStatus
            {
                Options = options,
                SettingsPath = ForcedUserPreferencesRuntimeSettings.SettingsPath,
                RuntimePatch = ForcedUserPreferencesModState.Status
            };

            try
            {
                result.Libraries = (_libraryManager.GetVirtualFolders() ?? new List<MediaBrowser.Model.Entities.VirtualFolderInfo>())
                    .Select(folder =>
                    {
                        var id = Normalize(folder?.Id ?? folder?.ItemId ?? folder?.Guid);
                        return new ForcedLibraryViewInfo
                        {
                            Id = id,
                            Name = folder?.Name,
                            CollectionType = folder?.CollectionType,
                            ConfiguredOrder = id != null && rank.TryGetValue(id, out var value) ? value : -1
                        };
                    })
                    .OrderBy(view => view.ConfiguredOrder < 0 ? int.MaxValue : view.ConfiguredOrder)
                    .ThenBy(view => view.Name)
                    .ToList();
            }
            catch (System.Exception ex)
            {
                result.Warnings.Add("Unable to enumerate virtual folders: " + ex.Message);
            }

            if (options.Enabled && options.ForceLibraryOrder && result.RuntimePatch?.LibraryOrderPatched != true)
                result.Warnings.Add("Forced library order is enabled but UserViewManager.GetUserViews was not patched.");
            if (options.Enabled && options.ForceDisplayMissingEpisodes && result.RuntimePatch?.UpdateConfigurationPatched != true)
                result.Warnings.Add("DisplayMissingEpisodes is forced but IUserManager.UpdateConfiguration was not patched.");
            if (options.Enabled && options.ForceLibraryOrder && order.Length == 0)
                result.Warnings.Add("Forced library order is enabled but LibraryOrderIds is empty.");
            return result;
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (System.Guid.TryParse(value, out var guid)) return guid.ToString("N");
            return value.Trim().Replace("-", string.Empty);
        }
    }
}
