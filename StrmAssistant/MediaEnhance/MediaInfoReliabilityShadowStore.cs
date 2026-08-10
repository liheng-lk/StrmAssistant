using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace StrmAssistant.MediaEnhance
{
    public sealed class MediaInfoReliabilityShadowRecord
    {
        public int SchemaVersion { get; set; } = 2;
        public string SourcePath { get; set; }
        public string ShortcutTargetFingerprint { get; set; }
        public string CapturedUtc { get; set; }
        public long? RunTimeTicks { get; set; }
        public long Size { get; set; }
        public string Container { get; set; }
        public long TotalBitrate { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public List<MediaStream> MediaStreams { get; set; } = new List<MediaStream>();
    }

    public sealed class MediaInfoReliabilityShadowStatus
    {
        public string RootPath { get; set; }
        public long CapturesSucceeded { get; set; }
        public long CapturesSkipped { get; set; }
        public long CapturesFailed { get; set; }
        public long RestoresSucceeded { get; set; }
        public long RestoresFailed { get; set; }
        public long StaleTargetRejected { get; set; }
        public string LastItemPath { get; set; }
        public string LastError { get; set; }
    }

    /// <summary>
    /// Small plugin-owned shadow store for STRM core playback metadata. This is independent from the
    /// user's optional MediaInfo JSON persistence mode and stores no raw STRM target URL: only a
    /// SHA-256 fingerprint is persisted so URL query tokens are never copied into the shadow cache.
    /// </summary>
    public static class MediaInfoReliabilityShadowStore
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<long, DateTimeOffset> LastCapture = new Dictionary<long, DateTimeOffset>();
        public static MediaInfoReliabilityShadowStatus Status { get; } = new MediaInfoReliabilityShadowStatus();

        public static bool AppliesTo(BaseItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path)) return false;
            return item.IsShortcut || string.Equals(Path.GetExtension(item.Path), ".strm", StringComparison.OrdinalIgnoreCase);
        }

        public static string GetPath(BaseItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path)) return null;
            return Path.Combine(GetRoot(), ComputeHash(NormalizeSource(item.Path)) + ".json");
        }

        public static bool Exists(BaseItem item)
        {
            try
            {
                var path = GetPath(item);
                return !string.IsNullOrWhiteSpace(path) &&
                       (TryLoad(item, path, out _) || TryLoad(item, path + ".bak", out _));
            }
            catch { return false; }
        }

        public static bool Capture(BaseItem item, bool force = false)
        {
            if (!AppliesTo(item)) return false;
            try
            {
                var libraryManager = Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>();
                var serializer = Plugin.Instance?.ApplicationHost?.Resolve<IJsonSerializer>();
                if (libraryManager == null || serializer == null) return false;

                var fresh = libraryManager.GetItemById(item.InternalId) ?? item;
                if (!MediaInfoIntegrityService.IsCoreMediaInfoComplete(fresh))
                {
                    Increment(status => status.CapturesSkipped++);
                    return false;
                }

                var targetFingerprint = ComputeShortcutTargetFingerprint(fresh);
                if (string.IsNullOrWhiteSpace(targetFingerprint))
                {
                    Increment(status => status.CapturesSkipped++);
                    return false;
                }

                lock (Sync)
                {
                    if (!force && LastCapture.TryGetValue(fresh.InternalId, out var last) &&
                        DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(10))
                    {
                        Status.CapturesSkipped++;
                        return true;
                    }
                }

                var streams = fresh.GetMediaStreams()?.Where(stream => stream != null).ToList() ?? new List<MediaStream>();
                if (!HasExpectedCoreStream(fresh, streams)) return false;

                var record = new MediaInfoReliabilityShadowRecord
                {
                    SourcePath = fresh.Path,
                    ShortcutTargetFingerprint = targetFingerprint,
                    CapturedUtc = DateTimeOffset.UtcNow.ToString("O"),
                    RunTimeTicks = fresh.RunTimeTicks,
                    Size = fresh.Size,
                    Container = fresh.Container,
                    TotalBitrate = fresh.TotalBitrate,
                    Width = fresh.Width,
                    Height = fresh.Height,
                    MediaStreams = streams
                };

                var path = GetPath(fresh);
                var temp = path + ".tmp";
                var backup = path + ".bak";
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);

                if (File.Exists(path) && TryLoad(fresh, path, out _)) File.Copy(path, backup, true);
                serializer.SerializeToFile(record, temp);
                if (!TryLoad(fresh, temp, out _)) throw new InvalidDataException("Serialized shadow failed validation.");
                File.Copy(temp, path, true);
                TryDelete(temp);

                lock (Sync) LastCapture[fresh.InternalId] = DateTimeOffset.UtcNow;
                Increment(status =>
                {
                    status.CapturesSucceeded++;
                    status.LastItemPath = fresh.Path;
                    status.LastError = null;
                });
                return true;
            }
            catch (Exception ex)
            {
                Increment(status =>
                {
                    status.CapturesFailed++;
                    status.LastItemPath = item?.Path;
                    status.LastError = ex.GetBaseException().Message;
                });
                Plugin.Instance?.Logger?.Warn("MediaInfo shadow capture failed for {0}: {1}", item?.Path, ex.Message);
                return false;
            }
        }

        public static bool Restore(BaseItem item, string source)
        {
            if (!AppliesTo(item)) return false;
            try
            {
                var libraryManager = Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>();
                var repository = Plugin.Instance?.ApplicationHost?.Resolve<IItemRepository>();
                if (libraryManager == null || repository == null) return false;

                var fresh = libraryManager.GetItemById(item.InternalId) ?? item;
                if (MediaInfoIntegrityService.IsCoreMediaInfoComplete(fresh)) return true;

                var path = GetPath(fresh);
                if (!TryLoad(fresh, path, out var record))
                {
                    var backup = path + ".bak";
                    if (!TryLoad(fresh, backup, out record))
                    {
                        Increment(status => status.RestoresFailed++);
                        return false;
                    }
                    File.Copy(backup, path, true);
                }

                repository.SaveMediaStreams(fresh.InternalId, record.MediaStreams, CancellationToken.None);
                fresh.RunTimeTicks = record.RunTimeTicks;
                fresh.Size = record.Size;
                fresh.Container = record.Container;
                fresh.TotalBitrate = record.TotalBitrate;
                fresh.Width = record.Width;
                fresh.Height = record.Height;

                libraryManager.UpdateItems(new List<BaseItem> { fresh }, null,
                    ItemUpdateType.MetadataImport, false, false, null, CancellationToken.None);

                var restored = MediaInfoIntegrityService.IsCoreMediaInfoComplete(
                    libraryManager.GetItemById(fresh.InternalId) ?? fresh);
                Increment(status =>
                {
                    if (restored) status.RestoresSucceeded++;
                    else status.RestoresFailed++;
                    status.LastItemPath = fresh.Path;
                    status.LastError = restored ? null : "Core MediaInfo was still incomplete after shadow restore.";
                });
                if (restored)
                    Plugin.Instance?.Logger?.Info("MediaInfo shadow restore success ({0}): {1}", source, fresh.Path);
                return restored;
            }
            catch (Exception ex)
            {
                Increment(status =>
                {
                    status.RestoresFailed++;
                    status.LastItemPath = item?.Path;
                    status.LastError = ex.GetBaseException().Message;
                });
                Plugin.Instance?.Logger?.Warn("MediaInfo shadow restore failed ({0}): {1}", source, ex.Message);
                return false;
            }
        }

        public static void Delete(BaseItem item)
        {
            try
            {
                var path = GetPath(item);
                TryDelete(path);
                TryDelete(path + ".bak");
                TryDelete(path + ".tmp");
                lock (Sync) LastCapture.Remove(item?.InternalId ?? 0);
            }
            catch { }
        }

        public static bool TryLoad(BaseItem item, string path, out MediaInfoReliabilityShadowRecord record)
        {
            record = null;
            if (item == null || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            try
            {
                var info = new FileInfo(path);
                if (info.Length <= 2) return false;
                var serializer = Plugin.Instance?.ApplicationHost?.Resolve<IJsonSerializer>();
                if (serializer == null) return false;
                record = serializer.DeserializeFromFileAsync<MediaInfoReliabilityShadowRecord>(path)
                    .GetAwaiter().GetResult();
                if (record == null || record.SchemaVersion != 2 ||
                    record.RunTimeTicks.GetValueOrDefault() <= 0 || record.MediaStreams == null ||
                    !HasExpectedCoreStream(item, record.MediaStreams))
                {
                    record = null;
                    return false;
                }
                if (!string.Equals(NormalizeSource(record.SourcePath), NormalizeSource(item.Path),
                        StringComparison.Ordinal))
                {
                    record = null;
                    return false;
                }

                var currentTargetFingerprint = ComputeShortcutTargetFingerprint(item);
                if (string.IsNullOrWhiteSpace(currentTargetFingerprint) ||
                    !FixedTimeEquals(record.ShortcutTargetFingerprint, currentTargetFingerprint))
                {
                    Increment(status => status.StaleTargetRejected++);
                    record = null;
                    return false;
                }
                return true;
            }
            catch
            {
                record = null;
                return false;
            }
        }

        private static bool HasExpectedCoreStream(BaseItem item, IEnumerable<MediaStream> streams)
        {
            if (streams == null) return false;
            if (item is Audio) return streams.Any(stream => stream.Type == MediaStreamType.Audio && !stream.IsExternal);
            if (item is Video) return streams.Any(stream => stream.Type == MediaStreamType.Video && !stream.IsExternal);
            return streams.Any(stream => !stream.IsExternal &&
                                         (stream.Type == MediaStreamType.Video || stream.Type == MediaStreamType.Audio));
        }

        private static string GetRoot()
        {
            var root = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrWhiteSpace(root)) root = Plugin.Instance?.ApplicationPaths?.PluginConfigurationsPath;
            if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
            var path = Path.Combine(root, "mediainfo-reliability-shadow");
            lock (Sync) Status.RootPath = path;
            return path;
        }

        private static string ComputeShortcutTargetFingerprint(BaseItem item)
        {
            if (!AppliesTo(item) || string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path)) return null;
            try
            {
                var target = File.ReadLines(item.Path)
                    .Select(line => line?.Trim())
                    .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
                return string.IsNullOrWhiteSpace(target) ? null : ComputeHash(target);
            }
            catch { return null; }
        }

        private static string ComputeHash(string value)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var current in bytes) builder.Append(current.ToString("x2"));
            return builder.ToString();
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right) || left.Length != right.Length) return false;
            var diff = 0;
            for (var i = 0; i < left.Length; i++) diff |= left[i] ^ right[i];
            return diff == 0;
        }

        private static string NormalizeSource(string value)
        {
            return (value ?? string.Empty).Trim().Replace('\\', '/');
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static void Increment(Action<MediaInfoReliabilityShadowStatus> action)
        {
            lock (Sync) action(Status);
        }
    }
}
