using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using StrmAssistant.Options;
using System;

namespace StrmAssistant.MediaEnhance
{
    /// <summary>
    /// Adds the Audio lifecycle that the community Plugin.cs only applied to Video items.
    /// MediaInfoApi already serializes Audio media streams and embeds/restores the primary image;
    /// this entry point safely wires that existing capability to library add/remove events.
    /// </summary>
    public sealed class MusicMediaInfoPersistMonitor : IServerEntryPoint
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private bool _started;

        public MusicMediaInfoPersistMonitor(ILibraryManager libraryManager, IFileSystem fileSystem)
        {
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
        }

        public void Run()
        {
            if (_started) return;
            _started = true;
            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemRemoved += OnItemRemoved;
        }

        public void Dispose()
        {
            if (!_started) return;
            _started = false;
            _libraryManager.ItemAdded -= OnItemAdded;
            _libraryManager.ItemRemoved -= OnItemRemoved;
        }

        private async void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            if (!(e?.Item is Audio audio)) return;

            var plugin = Plugin.Instance;
            var mediaInfoApi = Plugin.MediaInfoApi;
            var libraryApi = Plugin.LibraryApi;
            if (plugin == null || mediaInfoApi == null || libraryApi == null) return;

            var options = plugin.GetPluginOptions()?.MediaInfoExtractOptions;
            if (!IsEnabled(options)) return;

            if (MediaExtractionFilter.ShouldSkip(audio, options, out var reason))
            {
                plugin.Logger.Info("MediaInfoPersist - Music skipped by extraction blacklist: {0} ({1})",
                    audio.Path, reason);
                return;
            }

            try
            {
                var directoryService = new DirectoryService(plugin.Logger, _fileSystem);
                var hasMediaInfo = libraryApi.HasMediaInfo(audio);

                if (!hasMediaInfo)
                {
                    await mediaInfoApi.DeserializeMediaInfo(audio, directoryService,
                        "Music OnItemAdded Restore", true).ConfigureAwait(false);
                }
                else
                {
                    await mediaInfoApi.SerializeMediaInfo(audio.InternalId, directoryService, true,
                        "Music OnItemAdded Overwrite").ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                plugin.Logger.Error("Music MediaInfo persistence failed: {0}", ex.Message);
                plugin.Logger.Debug(ex.StackTrace);
            }
        }

        private void OnItemRemoved(object sender, ItemChangeEventArgs e)
        {
            if (!(e?.Item is Audio audio)) return;

            var plugin = Plugin.Instance;
            var mediaInfoApi = Plugin.MediaInfoApi;
            if (plugin == null || mediaInfoApi == null) return;

            var options = plugin.GetPluginOptions()?.MediaInfoExtractOptions;
            if (!IsEnabled(options) || IsRestoreMode(options)) return;

            try
            {
                var directoryService = new DirectoryService(plugin.Logger, _fileSystem);
                mediaInfoApi.DeleteMediaInfoJson(audio, directoryService, "Music Item Removed Event");
            }
            catch (Exception ex)
            {
                plugin.Logger.Error("Music MediaInfo JSON cleanup failed: {0}", ex.Message);
                plugin.Logger.Debug(ex.StackTrace);
            }
        }

        private static bool IsEnabled(MediaInfoExtractOptions options)
        {
            return options != null && options.PersistMusicMediaInfo &&
                   options.PersistMediaInfoMode != MediaInfoExtractOptions.PersistMediaInfoOption.None.ToString();
        }

        private static bool IsRestoreMode(MediaInfoExtractOptions options)
        {
            return options?.PersistMediaInfoMode == MediaInfoExtractOptions.PersistMediaInfoOption.Restore.ToString();
        }
    }
}
