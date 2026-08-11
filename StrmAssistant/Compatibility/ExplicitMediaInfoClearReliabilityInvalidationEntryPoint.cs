using HarmonyLib;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.Api;
using StrmAssistant.Common;
using StrmAssistant.MediaEnhance;
using System;
using System.IO;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class ExplicitMediaInfoClearReliabilityStatus
    {
        public bool TargetPatched { get; set; }
        public long ClearsObserved { get; set; }
        public long ShadowsDeleted { get; set; }
        public long BackupSnapshotsDeleted { get; set; }
        public string LastItemPath { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class ExplicitMediaInfoClearReliabilityState
    {
        public static ExplicitMediaInfoClearReliabilityStatus Status { get; internal set; } =
            new ExplicitMediaInfoClearReliabilityStatus();
    }

    /// <summary>
    /// An explicit administrator MediaInfo clear must invalidate last-known-good recovery data when
    /// DeletePersistedJson=true; otherwise the playback pre-read guard would correctly (but unexpectedly)
    /// restore the state the administrator just asked to discard. Background refreshes never call this API.
    /// </summary>
    public sealed class ExplicitMediaInfoClearReliabilityInvalidationEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.explicit-mediainfo-clear-invalidation";
        private Harmony _harmony;

        public void Run()
        {
            var status = new ExplicitMediaInfoClearReliabilityStatus();
            ExplicitMediaInfoClearReliabilityState.Status = status;
            try
            {
                var target = typeof(MediaMaintenanceApiService).GetMethod("Post",
                    BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(ClearMediaInfoMaintenance) }, null);
                if (target == null)
                {
                    status.Error = "MediaMaintenanceApiService.Post(ClearMediaInfoMaintenance) was not found.";
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(target, postfix: new HarmonyMethod(
                    typeof(ExplicitMediaInfoClearReliabilityPatches).GetMethod(
                        nameof(ExplicitMediaInfoClearReliabilityPatches.Postfix), BindingFlags.Public | BindingFlags.Static)));
                status.TargetPatched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }
    }

    public static class ExplicitMediaInfoClearReliabilityPatches
    {
        private static readonly object StatusSync = new object();

        public static void Postfix(ClearMediaInfoMaintenance request, object __result)
        {
            if (request?.Confirm != true || request.DeletePersistedJson != true) return;
            if (!(__result is MediaMaintenanceResult result) || !result.Executed) return;

            var status = ExplicitMediaInfoClearReliabilityState.Status;
            lock (StatusSync) status.ClearsObserved++;

            try
            {
                if (!long.TryParse(request.Id, out var internalId)) return;
                var manager = Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>();
                var item = manager?.GetItemById(internalId);
                if (item == null) return;

                lock (StatusSync) status.LastItemPath = item.Path;

                if (MediaInfoReliabilityShadowStore.AppliesTo(item))
                {
                    MediaInfoReliabilityShadowStore.Delete(item);
                    lock (StatusSync) status.ShadowsDeleted++;
                }

                var primary = MediaInfoApi.GetMediaInfoJsonPath(item);
                var backup = MediaInfoPersistenceReliabilityPatches.BackupPath(primary);
                if (!string.IsNullOrWhiteSpace(backup) && File.Exists(backup))
                {
                    File.Delete(backup);
                    lock (StatusSync) status.BackupSnapshotsDeleted++;
                }
            }
            catch (Exception ex)
            {
                lock (StatusSync) status.LastError = ex.GetBaseException().Message;
                Plugin.Instance?.Logger?.Warn("Explicit MediaInfo clear reliability invalidation failed: " + ex.Message);
            }
        }
    }
}
