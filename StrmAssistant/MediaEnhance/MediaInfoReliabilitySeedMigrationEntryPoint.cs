using MediaBrowser.Controller.Plugins;
using System;
using System.IO;

namespace StrmAssistant.MediaEnhance
{
    public sealed class MediaInfoReliabilitySeedMigrationStatus
    {
        public int SchemaVersion { get; set; }
        public string MarkerPath { get; set; }
        public bool MarkerAlreadyPresent { get; set; }
        public bool ManualSeedRequired { get; set; }
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
    /// Lightweight startup state only. Schema migration is intentionally NOT run automatically because
    /// a large STRM library can turn an otherwise healthy Emby restart into a database/filesystem I/O
    /// burst. The manual "构建 STRM 媒体信息可靠性缓存" scheduled task performs and verifies migration.
    /// </summary>
    public sealed class MediaInfoReliabilitySeedMigrationEntryPoint : IServerEntryPoint
    {
        public const int ShadowSchemaVersion = 3;

        public void Run()
        {
            var marker = GetMarkerPath();
            var present = File.Exists(marker);
            MediaInfoReliabilitySeedMigrationState.Status = new MediaInfoReliabilitySeedMigrationStatus
            {
                SchemaVersion = ShadowSchemaVersion,
                MarkerPath = marker,
                MarkerAlreadyPresent = present,
                ManualSeedRequired = !present,
                Completed = present,
                CompletedUtc = present ? SafeReadCompletedUtc(marker) : null
            };

            if (!present)
            {
                Plugin.Instance?.Logger?.Info(
                    "STRM MediaInfo reliability schema-v{0} migration is pending. No automatic full-library scan will run; execute the manual reliability seed scheduled task when convenient.",
                    ShadowSchemaVersion);
            }
        }

        public void Dispose()
        {
        }

        public static string GetMarkerPath()
        {
            var root = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrWhiteSpace(root)) root = Plugin.Instance?.ApplicationPaths?.PluginConfigurationsPath;
            if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
            return Path.Combine(root, "mediainfo-reliability-shadow", "seed-v" + ShadowSchemaVersion + ".done");
        }

        public static void MarkCompleted(int completeCount, int protectedCount)
        {
            var marker = GetMarkerPath();
            var missing = Math.Max(0, completeCount - protectedCount);
            var status = MediaInfoReliabilitySeedMigrationState.Status ?? new MediaInfoReliabilitySeedMigrationStatus();
            status.SchemaVersion = ShadowSchemaVersion;
            status.MarkerPath = marker;
            status.Started = true;
            status.StartedUtc = status.StartedUtc ?? DateTimeOffset.UtcNow.ToString("O");
            status.CompleteStrmCount = completeCount;
            status.ProtectedStrmCount = protectedCount;
            status.MissingShadowCount = missing;
            status.ManualSeedRequired = missing > 0;

            if (missing > 0)
            {
                status.Completed = false;
                status.Error = "Schema-v" + ShadowSchemaVersion + " shadow verification is still missing " +
                               missing + " complete STRM items.";
                MediaInfoReliabilitySeedMigrationState.Status = status;
                return;
            }

            var parent = Path.GetDirectoryName(marker);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            var completedUtc = DateTimeOffset.UtcNow.ToString("O");
            File.WriteAllText(marker,
                "schema=" + ShadowSchemaVersion + Environment.NewLine +
                "completedUtc=" + completedUtc + Environment.NewLine +
                "completeStrms=" + completeCount + Environment.NewLine +
                "protectedStrms=" + protectedCount + Environment.NewLine +
                "identity=http(s)-authority-and-decoded-path-without-query-or-fragment" + Environment.NewLine);

            status.MarkerAlreadyPresent = true;
            status.ManualSeedRequired = false;
            status.Completed = true;
            status.CompletedUtc = completedUtc;
            status.Error = null;
            MediaInfoReliabilitySeedMigrationState.Status = status;
        }

        private static string SafeReadCompletedUtc(string marker)
        {
            try
            {
                foreach (var line in File.ReadAllLines(marker))
                    if (line.StartsWith("completedUtc=", StringComparison.Ordinal))
                        return line.Substring("completedUtc=".Length).Trim();
            }
            catch
            {
            }
            return null;
        }
    }
}
