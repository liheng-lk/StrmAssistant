using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.MediaEnhance
{
    /// <summary>
    /// One-time, schema-versioned migration for STRM items that already had complete MediaInfo before
    /// the reliability shadow feature was installed. It reads only Emby's local item repository and the
    /// local .strm text file used to compute the target fingerprint; it never probes the remote media.
    /// </summary>
    public sealed class MediaInfoReliabilitySeedEntryPoint : IServerEntryPoint
    {
        private const int ShadowSchemaVersion = 2;
        private readonly ILibraryManager _libraryManager;
        private CancellationTokenSource _cts;

        public MediaInfoReliabilitySeedEntryPoint(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public void Run()
        {
            var marker = GetMarkerPath();
            if (File.Exists(marker)) return;

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => SeedAsync(marker, _cts.Token));
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = null;
        }

        private async Task SeedAsync(string marker, CancellationToken cancellationToken)
        {
            try
            {
                // Avoid competing with Emby's startup library initialization.
                await Task.Delay(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
                for (var wait = 0; wait < 60 && _libraryManager.IsScanRunning; wait++)
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    HasPath = true,
                    MediaTypes = new[] { MediaType.Video, MediaType.Audio }
                }) ?? Array.Empty<BaseItem>();

                var candidates = items
                    .Where(MediaInfoReliabilityShadowStore.AppliesTo)
                    .GroupBy(item => item.InternalId)
                    .Select(group => group.First())
                    .ToList();

                var captured = 0;
                var incomplete = 0;
                var failed = 0;
                for (var index = 0; index < candidates.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = candidates[index];
                    if (!MediaInfoIntegrityService.IsCoreMediaInfoComplete(item))
                    {
                        incomplete++;
                    }
                    else if (MediaInfoReliabilityShadowStore.Capture(item, true))
                    {
                        captured++;
                    }
                    else
                    {
                        failed++;
                    }

                    if ((index + 1) % 100 == 0) await Task.Yield();
                }

                var parent = Path.GetDirectoryName(marker);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                File.WriteAllText(marker,
                    "schema=" + ShadowSchemaVersion + Environment.NewLine +
                    "utc=" + DateTimeOffset.UtcNow.ToString("O") + Environment.NewLine +
                    "candidates=" + candidates.Count + Environment.NewLine +
                    "captured=" + captured + Environment.NewLine +
                    "incomplete=" + incomplete + Environment.NewLine +
                    "failed=" + failed + Environment.NewLine);

                Plugin.Instance?.Logger?.Info(
                    "STRM MediaInfo reliability startup seed completed: candidates={0}, captured={1}, incomplete={2}, failed={3}",
                    candidates.Count, captured, incomplete, failed);
            }
            catch (OperationCanceledException)
            {
                // No marker is written; a future startup can retry.
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("STRM MediaInfo reliability startup seed failed: " + ex.Message);
            }
        }

        private static string GetMarkerPath()
        {
            var root = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrWhiteSpace(root)) root = Plugin.Instance?.ApplicationPaths?.PluginConfigurationsPath;
            if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
            return Path.Combine(root, "mediainfo-reliability-shadow", "seed-v" + ShadowSchemaVersion + ".done");
        }
    }
}
