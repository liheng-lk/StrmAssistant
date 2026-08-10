using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using System;
using System.IO;
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
    /// never probes the STRM target. The v3 marker is intentionally new because v3 changed target
    /// identity to ignore expiring URL query/fragment tokens.
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

                // Do not compete with Emby's initial startup/scan. IsScanRunning is read by reflection so
                // this migration does not add a compile-time dependency on a version-specific member.
                await Task.Delay(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
                for (var attempt = 0; attempt < 60 && IsLibraryScanRunning(); attempt++)
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                var task = new MediaInfoReliabilitySeedTask(_libraryManager);
                await task.Execute(cancellationToken, null).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var parent = Path.GetDirectoryName(marker);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                File.WriteAllText(marker,
                    "schema=" + ShadowSchemaVersion + Environment.NewLine +
                    "completedUtc=" + DateTimeOffset.UtcNow.ToString("O") + Environment.NewLine +
                    "identity=http(s)-authority-and-decoded-path-without-query-or-fragment" + Environment.NewLine);

                status.Completed = true;
                status.CompletedUtc = DateTimeOffset.UtcNow.ToString("O");
                status.Error = null;
                Plugin.Instance?.Logger?.Info(
                    "STRM MediaInfo reliability schema-v{0} startup seed completed; marker={1}",
                    ShadowSchemaVersion, marker);
            }
            catch (OperationCanceledException)
            {
                status.Cancelled = true;
                // No marker: a future startup retries.
            }
            catch (Exception ex)
            {
                status.Error = ex.GetBaseException().Message;
                // No marker: a future startup retries.
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
