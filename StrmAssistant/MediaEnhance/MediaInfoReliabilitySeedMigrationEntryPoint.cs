using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.MediaEnhance
{
    public sealed class MediaInfoReliabilitySeedMigrationStatus
    {
        public int SchemaVersion { get; set; }
        public string MarkerPath { get; set; }
        public bool MarkerAlreadyPresent { get; set; }
        public bool Started { get; set; }
        public bool Completed { get; set; }
        public bool Cancelled { get; set; }
        public int CompleteStrmCount { get; set; }
        public int ProtectedStrmCount { get; set; }
        public int MissingShadowCount { get; set; }
        public string StartedUtc { get; set; }
        public string CompletedUtc { get; set; }
        public string Error { get; set; }
    }

    public static class MediaInfoReliabilitySeedMigrationState
    {
        public static MediaInfoReliabilitySeedMigrationStatus Status { get; internal set; } =
            new MediaInfoReliabilitySeedMigrationStatus();
    }

    /// <summary>
    /// One-time schema migration for installations that already have complete STRM MediaInfo in Emby's
    /// local repository. It invokes the existing reliability seed task only after startup settles and
    /// never probes the STRM target. The v3 marker is written only after every currently complete STRM
    /// has a valid v3 shadow; partial filesystem failures therefore retry on a future startup.
    /// </summary>
    public sealed class MediaInfoReliabilitySeedMigrationEntryPoint : IServerEntryPoint
    {
        private const int ShadowSchemaVersion = 3;
        private readonly ILibraryManager _libraryManager;
        private CancellationTokenSource _cts;

        public MediaInfoReliabilitySeedMigrationEntryPoint(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public void Run()
        {
            var marker = GetMarkerPath();
            var status = new MediaInfoReliabilitySeedMigrationStatus
            {
                SchemaVersion = ShadowSchemaVersion,
                MarkerPath = marker,
                MarkerAlreadyPresent = File.Exists(marker)
            };
            MediaInfoReliabilitySeedMigrationState.Status = status;
            if (status.MarkerAlreadyPresent) return;

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => RunMigrationAsync(marker, _cts.Token));
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = null;
        }

        private async Task RunMigrationAsync(string marker, CancellationToken cancellationToken)
        {
            var status = MediaInfoReliabilitySeedMigrationState.Status;
            try
            {
                status.Started = true;
                status.StartedUtc = DateTimeOffset.UtcNow.ToString("O");

                await Task.Delay(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
                for (var attempt = 0; attempt < 60 && IsLibraryScanRunning(); attempt++)
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                var task = new MediaInfoReliabilitySeedTask(_libraryManager);
                await task.Execute(cancellationToken, null).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var completeStrms = (_libraryManager.GetItemList(new InternalItemsQuery
                {
                    HasPath = true,
                    MediaTypes = new[] { MediaType.Video, MediaType.Audio }
                }) ?? Array.Empty<BaseItem>())
                    .Where(MediaInfoReliabilityShadowStore.AppliesTo)
                    .Where(MediaInfoIntegrityService.IsCoreMediaInfoComplete)
                    .GroupBy(item => item.InternalId)
                    .Select(group => group.First())
                    .ToList();

                status.CompleteStrmCount = completeStrms.Count;
                status.ProtectedStrmCount = completeStrms.Count(MediaInfoReliabilityShadowStore.Exists);
                status.MissingShadowCount = status.CompleteStrmCount - status.ProtectedStrmCount;
                if (status.MissingShadowCount > 0)
                {
                    throw new InvalidDataException(
                        "Schema-v3 shadow verification failed for " + status.MissingShadowCount +
                        " of " + status.CompleteStrmCount + " complete STRM items; no migration marker was written.");
                }

                var parent = Path.GetDirectoryName(marker);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                File.WriteAllText(marker,
                    "schema=" + ShadowSchemaVersion + Environment.NewLine +
                    "completedUtc=" + DateTimeOffset.UtcNow.ToString("O") + Environment.NewLine +
                    "completeStrms=" + status.CompleteStrmCount + Environment.NewLine +
                    "protectedStrms=" + status.ProtectedStrmCount + Environment.NewLine +
                    "identity=http(s)-authority-and-decoded-path-without-query-or-fragment" + Environment.NewLine);

                status.Completed = true;
                status.CompletedUtc = DateTimeOffset.UtcNow.ToString("O");
                status.Error = null;
                Plugin.Instance?.Logger?.Info(
                    "STRM MediaInfo reliability schema-v{0} startup seed verified: protected={1}/{2}; marker={3}",
                    ShadowSchemaVersion, status.ProtectedStrmCount, status.CompleteStrmCount, marker);
            }
            catch (OperationCanceledException)
            {
                status.Cancelled = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetBaseException().Message;
                Plugin.Instance?.Logger?.Warn("STRM MediaInfo reliability startup migration failed: " + status.Error);
            }
        }

        private bool IsLibraryScanRunning()
        {
            try
            {
                var property = _libraryManager.GetType().GetProperty("IsScanRunning",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return property?.PropertyType == typeof(bool) && property.GetValue(_libraryManager) is bool running && running;
            }
            catch
            {
                return false;
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
