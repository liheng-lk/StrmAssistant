using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using StrmAssistant.Common;
using StrmAssistant.Web.Api;
using System;

namespace StrmAssistant.Web.Service
{
    public class LibraryStructureService : IService, IRequiresRequest
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IServerConfigurationManager _configurationManager;

        public LibraryStructureService(
            ILibraryManager libraryManager,
            IServerConfigurationManager configurationManager)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _configurationManager = configurationManager;
        }

        public IRequest Request { get; set; }

        public void Post(CopyVirtualFolder request)
        {
            var sourceLibrary = _libraryManager.GetItemById(request.Id);
            var sourceOptions = _libraryManager.GetLibraryOptions(sourceLibrary);

            var targetOptions = LibraryApi.CopyLibraryOptions(sourceOptions);
            targetOptions.PathInfos = Array.Empty<MediaPathInfo>();

            var suffix = new Random().Next(100, 999).ToString();
            _libraryManager.AddVirtualFolder(sourceLibrary.Name + " #" + suffix, targetOptions, false);
        }

        public void Post(RemoveCollectionsVirtualFolder request)
        {
            var sourceLibrary = _libraryManager.GetItemById(request.Id);
            var collectionFolder = sourceLibrary as CollectionFolder;

            if (collectionFolder == null ||
                !string.Equals(collectionFolder.CollectionType, CollectionType.BoxSets.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The selected virtual folder is not the Emby collections library.");
            }

            // Keep the UI-hidden behavior enabled so a future BoxSets library remains hidden.
            var pluginOptions = Plugin.Instance.GetPluginOptions();
            pluginOptions.ExperienceEnhanceOptions.HideCollectionsLibrary = true;
            Plugin.Instance.SavePluginOptionsSuppress();

            // Emby's collection migration entry point uses this flag to decide whether it needs
            // to recreate the historical collections virtual folder during startup migration.
            _configurationManager.Configuration.CollectionsUpgraded = true;
            _configurationManager.SaveConfiguration();

            _libraryManager.RemoveVirtualFolder(collectionFolder.Name, false).GetAwaiter().GetResult();
            _logger.Info("Removed collections virtual folder and enabled HideCollectionsLibrary.");
        }
    }
}
