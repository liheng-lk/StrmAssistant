using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using StrmAssistant.Api;
using StrmAssistant.Common;
using StrmAssistant.MediaEnhance;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class MediaInfoPersistenceReliabilityStatus
    {
        public bool HasMediaInfoPatched { get; set; }
        public bool DeleteGuardPatched { get; set; }
        public bool SerializeBackupPatched { get; set; }
        public bool DeserializeBackupPatched { get; set; }
        public bool ExplicitDeepDeleteContextPatched { get; set; }
        public long RemoteSizeZeroAccepted { get; set; }
        public long RemovalSnapshotsPreserved { get; set; }
        public long BackupSnapshotsCreated { get; set; }
        public long BackupRestoresSucceeded { get; set; }
        public long BackupRestoresFailed { get; set; }
        public long InvalidSnapshotsRejected { get; set; }
        public long ExplicitDeleteShadowsCleared { get; set; }
        public string Error { get; set; }
    }

    public static class MediaInfoPersistenceReliabilityState
    {
        public static MediaInfoPersistenceReliabilityStatus Status { get; internal set; } =
            new MediaInfoPersistenceReliabilityStatus();
    }

    public sealed class MediaInfoPersistenceReliabilityRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.mediainfo-reliability";
        private Harmony _harmony;

        public void Run()
        {
            var status = new MediaInfoPersistenceReliabilityStatus();
            MediaInfoPersistenceReliabilityState.Status = status;

            try
            {
                _harmony = new Harmony(HarmonyId);

                var hasMediaInfo = typeof(LibraryApi).GetMethod(nameof(LibraryApi.HasMediaInfo),
                    BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(BaseItem) }, null);
                if (hasMediaInfo != null)
                {
                    _harmony.Patch(hasMediaInfo, postfix: new HarmonyMethod(
                        typeof(MediaInfoPersistenceReliabilityPatches).GetMethod(
                            nameof(MediaInfoPersistenceReliabilityPatches.HasMediaInfoPostfix),
                            BindingFlags.Public | BindingFlags.Static)));
                    status.HasMediaInfoPatched = true;
                }

                var deleteSnapshot = typeof(MediaInfoApi).GetMethod(nameof(MediaInfoApi.DeleteMediaInfoJson),
                    BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(BaseItem), typeof(IDirectoryService), typeof(string) }, null);
                if (deleteSnapshot != null)
                {
                    _harmony.Patch(deleteSnapshot, prefix: new HarmonyMethod(
                        typeof(MediaInfoPersistenceReliabilityPatches).GetMethod(
                            nameof(MediaInfoPersistenceReliabilityPatches.DeleteMediaInfoJsonPrefix),
                            BindingFlags.Public | BindingFlags.Static)));
                    status.DeleteGuardPatched = true;
                }

                var privateSerialize = typeof(MediaInfoApi).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .FirstOrDefault(method => method.Name == "SerializeMediaInfo" &&
                                              method.GetParameters().Length == 4 &&
                                              method.GetParameters()[0].ParameterType == typeof(BaseItem));
                if (privateSerialize != null)
                {
                    _harmony.Patch(privateSerialize,
                        prefix: new HarmonyMethod(typeof(MediaInfoPersistenceReliabilityPatches).GetMethod(
                            nameof(MediaInfoPersistenceReliabilityPatches.SerializeMediaInfoPrefix),
                            BindingFlags.Public | BindingFlags.Static)),
                        postfix: new HarmonyMethod(typeof(MediaInfoPersistenceReliabilityPatches).GetMethod(
                            nameof(MediaInfoPersistenceReliabilityPatches.SerializeMediaInfoPostfix),
                            BindingFlags.Public | BindingFlags.Static)));
                    status.SerializeBackupPatched = true;
                }

                var deserialize = typeof(MediaInfoApi).GetMethod(nameof(MediaInfoApi.DeserializeMediaInfo),
                    BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(BaseItem), typeof(IDirectoryService), typeof(string), typeof(bool) }, null);
                if (deserialize != null)
                {
                    _harmony.Patch(deserialize, postfix: new HarmonyMethod(
                        typeof(MediaInfoPersistenceReliabilityPatches).GetMethod(
                            nameof(MediaInfoPersistenceReliabilityPatches.DeserializeMediaInfoPostfix),
                            BindingFlags.Public | BindingFlags.Static)));
                    status.DeserializeBackupPatched = true;
                }

                var explicitDeepDelete = typeof(DeepDeleteApiService).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(method => method.Name == "Delete" &&
                                              method.GetParameters().Length == 1 &&
                                              method.GetParameters()[0].ParameterType == typeof(ExecuteDeepDelete));
                if (explicitDeepDelete != null)
                {
                    _harmony.Patch(explicitDeepDelete,
                        prefix: new HarmonyMethod(typeof(MediaInfoPersistenceReliabilityPatches).GetMethod(
                            nameof(MediaInfoPersistenceReliabilityPatches.ExplicitDeepDeletePrefix),
                            BindingFlags.Public | BindingFlags.Static)),
                        finalizer: new HarmonyMethod(typeof(MediaInfoPersistenceReliabilityPatches).GetMethod(
                            nameof(MediaInfoPersistenceReliabilityPatches.ExplicitDeepDeleteFinalizer),
                            BindingFlags.Public | BindingFlags.Static)));
                    status.ExplicitDeepDeleteContextPatched = true;
                }
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Error("MediaInfo reliability patch failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
        }
    }

    public static class MediaInfoPersistenceReliabilityPatches
    {
        private static readonly AsyncLocal<int> ExplicitDeleteDepth = new AsyncLocal<int>();
        private static readonly AsyncLocal<bool> BackupRetry = new AsyncLocal<bool>();
        private static readonly object CounterSync = new object();

        public static void HasMediaInfoPostfix(BaseItem item, ref bool __result)
        {
            if (__result || item == null) return;
            try
            {
                if ((item.IsShortcut || !item.IsFileProtocol) &&
                    MediaInfoIntegrityService.IsCoreMediaInfoComplete(item))
                {
                    __result = true;
                    IncrementCounter(Counter.RemoteSizeZeroAccepted);
                }
            }
            catch
            {
            }
        }

        public static bool DeleteMediaInfoJsonPrefix(BaseItem item, string source)
        {
            if (ExplicitDeleteDepth.Value > 0)
            {
                try
                {
                    if (item != null && MediaInfoReliabilityShadowStore.AppliesTo(item))
                    {
                        MediaInfoReliabilityShadowStore.Delete(item);
                        IncrementCounter(Counter.ExplicitDeleteShadowsCleared);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Instance?.Logger?.Warn("MediaInfo reliability - explicit shadow cleanup failed: " + ex.Message);
                }
                return true;
            }
            if (string.IsNullOrWhiteSpace(source)) return true;

            if (source.IndexOf("Item Removed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                source.IndexOf("ItemRemoved", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                IncrementCounter(Counter.RemovalSnapshotsPreserved);
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("MediaInfo reliability - preserve snapshot on non-explicit item removal: " + source);
                return false;
            }
            return true;
        }

        public static void ExplicitDeepDeletePrefix()
        {
            ExplicitDeleteDepth.Value = ExplicitDeleteDepth.Value + 1;
        }

        public static Exception ExplicitDeepDeleteFinalizer(Exception __exception)
        {
            ExplicitDeleteDepth.Value = Math.Max(0, ExplicitDeleteDepth.Value - 1);
            return __exception;
        }

        public static void SerializeMediaInfoPrefix(BaseItem item, bool overwrite)
        {
            if (!overwrite || item == null) return;
            TryBackupCurrentSnapshot(item);
        }

        public static void SerializeMediaInfoPostfix(BaseItem item, ref Task<bool> __result)
        {
            if (__result == null || item == null) return;
            __result = ValidateAndSeedBackupAsync(item, __result);
        }

        public static void DeserializeMediaInfoPostfix(MediaInfoApi __instance, BaseItem item,
            IDirectoryService directoryService, string source, bool ignoreFileChange, ref Task<bool> __result)
        {
            if (__result == null || item == null || BackupRetry.Value) return;
            __result = RetryFromBackupAsync(__instance, item, directoryService, source, ignoreFileChange, __result);
        }

        private static async Task<bool> ValidateAndSeedBackupAsync(BaseItem item, Task<bool> original)
        {
            var success = await original.ConfigureAwait(false);
            if (!success) return false;

            if (MediaInfoIntegrityService.RefreshValidatedBackup(item))
                IncrementCounter(Counter.BackupSnapshotsCreated);
            else
            {
                IncrementCounter(Counter.InvalidSnapshotsRejected);
                Plugin.Instance?.Logger?.Warn("MediaInfo persistence produced an invalid snapshot; previous backup was preserved: {0}", item.Path);
            }
            return true;
        }

        private static async Task<bool> RetryFromBackupAsync(MediaInfoApi api, BaseItem item,
            IDirectoryService directoryService, string source, bool ignoreFileChange, Task<bool> original)
        {
            var success = await original.ConfigureAwait(false);
            if (success) return true;

            var primary = MediaInfoApi.GetMediaInfoJsonPath(item);
            var backup = BackupPath(primary);
            if (!MediaInfoIntegrityService.IsSnapshotValid(item, backup))
            {
                if (File.Exists(backup)) IncrementCounter(Counter.InvalidSnapshotsRejected);
                return false;
            }

            try
            {
                var parent = Path.GetDirectoryName(primary);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                File.Copy(backup, primary, true);

                BackupRetry.Value = true;
                var retried = await api.DeserializeMediaInfo(item, directoryService,
                    source + " BackupRetry", ignoreFileChange).ConfigureAwait(false);
                IncrementCounter(retried ? Counter.BackupRestoresSucceeded : Counter.BackupRestoresFailed);
                return retried;
            }
            catch (Exception ex)
            {
                IncrementCounter(Counter.BackupRestoresFailed);
                Plugin.Instance?.Logger?.Warn("MediaInfo reliability - backup restore failed: " + ex.Message);
                return false;
            }
            finally
            {
                BackupRetry.Value = false;
            }
        }

        private static void TryBackupCurrentSnapshot(BaseItem item)
        {
            try
            {
                var primary = MediaInfoApi.GetMediaInfoJsonPath(item);
                if (!File.Exists(primary)) return;
                if (!MediaInfoIntegrityService.IsSnapshotValid(item, primary))
                {
                    IncrementCounter(Counter.InvalidSnapshotsRejected);
                    return;
                }

                var backup = BackupPath(primary);
                File.Copy(primary, backup, true);
                IncrementCounter(Counter.BackupSnapshotsCreated);
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("MediaInfo reliability - snapshot backup failed: " + ex.Message);
            }
        }

        public static string BackupPath(string primary)
        {
            return string.IsNullOrWhiteSpace(primary) ? null : primary + ".bak";
        }

        private enum Counter
        {
            RemoteSizeZeroAccepted,
            RemovalSnapshotsPreserved,
            BackupSnapshotsCreated,
            BackupRestoresSucceeded,
            BackupRestoresFailed,
            InvalidSnapshotsRejected,
            ExplicitDeleteShadowsCleared
        }

        private static void IncrementCounter(Counter counter)
        {
            lock (CounterSync)
            {
                var status = MediaInfoPersistenceReliabilityState.Status;
                if (status == null) return;
                switch (counter)
                {
                    case Counter.RemoteSizeZeroAccepted: status.RemoteSizeZeroAccepted++; break;
                    case Counter.RemovalSnapshotsPreserved: status.RemovalSnapshotsPreserved++; break;
                    case Counter.BackupSnapshotsCreated: status.BackupSnapshotsCreated++; break;
                    case Counter.BackupRestoresSucceeded: status.BackupRestoresSucceeded++; break;
                    case Counter.BackupRestoresFailed: status.BackupRestoresFailed++; break;
                    case Counter.InvalidSnapshotsRejected: status.InvalidSnapshotsRejected++; break;
                    case Counter.ExplicitDeleteShadowsCleared: status.ExplicitDeleteShadowsCleared++; break;
                }
            }
        }
    }
}
