using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Configuration;
using StrmAssistant.Experience;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class ForcedUserPreferencesCapabilityStatus
    {
        public bool LibraryOrderTargetFound { get; set; }
        public bool LibraryOrderPatched { get; set; }
        public bool UpdateConfigurationTargetFound { get; set; }
        public bool UpdateConfigurationPatched { get; set; }
        public int ExistingUsersSynchronized { get; set; }
        public List<string> Targets { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    public static class ForcedUserPreferencesModState
    {
        public static ForcedUserPreferencesCapabilityStatus Status { get; internal set; } =
            new ForcedUserPreferencesCapabilityStatus();
    }

    public sealed class ForcedUserPreferencesRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.forced-user-preferences";
        private readonly IUserManager _userManager;
        private Harmony _harmony;

        public ForcedUserPreferencesRuntimeModEntryPoint(IUserManager userManager)
        {
            _userManager = userManager;
        }

        public void Run()
        {
            var status = new ForcedUserPreferencesCapabilityStatus();
            ForcedUserPreferencesModState.Status = status;
            try
            {
                _harmony = new Harmony(HarmonyId);
                PatchLibraryOrder(status);
                PatchUserConfiguration(status);
                SynchronizeExistingUsers(status);
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Forced user preferences mod unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
        }

        private void PatchLibraryOrder(ForcedUserPreferencesCapabilityStatus status)
        {
            try
            {
                var assembly = Assembly.Load("Emby.Server.Implementations");
                var type = assembly.GetType("Emby.Server.Implementations.Library.UserViewManager");
                var target = type?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(method => string.Equals(method.Name, "GetUserViews", StringComparison.Ordinal) &&
                                              method.ReturnType == typeof(Folder[]));
                status.LibraryOrderTargetFound = target != null;
                if (target == null) return;

                var postfix = typeof(ForcedUserPreferencesPatches).GetMethod(
                    nameof(ForcedUserPreferencesPatches.GetUserViewsPostfix), BindingFlags.Public | BindingFlags.Static);
                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                status.LibraryOrderPatched = true;
                status.Targets.Add(target.ToString());
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Forced library order patch unavailable: " + ex.Message);
            }
        }

        private void PatchUserConfiguration(ForcedUserPreferencesCapabilityStatus status)
        {
            try
            {
                var target = _userManager.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(method => string.Equals(method.Name, "UpdateConfiguration", StringComparison.Ordinal) &&
                                              method.GetParameters().Any(parameter => parameter.ParameterType == typeof(UserConfiguration)));
                status.UpdateConfigurationTargetFound = target != null;
                if (target == null) return;

                var prefix = typeof(ForcedUserPreferencesPatches).GetMethod(
                    nameof(ForcedUserPreferencesPatches.UpdateConfigurationPrefix), BindingFlags.Public | BindingFlags.Static);
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                status.UpdateConfigurationPatched = true;
                status.Targets.Add(target.ToString());
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Forced UserConfiguration patch unavailable: " + ex.Message);
            }
        }

        private void SynchronizeExistingUsers(ForcedUserPreferencesCapabilityStatus status)
        {
            var options = ForcedUserPreferencesRuntimeSettings.GetSnapshot();
            if (!options.Enabled || !options.ForceDisplayMissingEpisodes) return;

            try
            {
#pragma warning disable CS0618
                var users = _userManager.Users ?? Array.Empty<User>();
#pragma warning restore CS0618
                foreach (var user in users)
                {
                    var configuration = user?.Configuration;
                    if (configuration == null || configuration.DisplayMissingEpisodes == options.DisplayMissingEpisodes)
                        continue;
                    configuration.DisplayMissingEpisodes = options.DisplayMissingEpisodes;
                    _userManager.UpdateConfiguration(user, configuration);
                    status.ExistingUsersSynchronized++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Forced user preference startup synchronization failed: " + ex.Message);
            }
        }
    }

    public static class ForcedUserPreferencesPatches
    {
        public static void UpdateConfigurationPrefix(object[] __args)
        {
            try
            {
                var options = ForcedUserPreferencesRuntimeSettings.GetSnapshot();
                if (!options.Enabled || !options.ForceDisplayMissingEpisodes || __args == null) return;
                var configuration = __args.OfType<UserConfiguration>().FirstOrDefault();
                if (configuration != null)
                    configuration.DisplayMissingEpisodes = options.DisplayMissingEpisodes;
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Forced UserConfiguration prefix skipped: " + ex.Message);
            }
        }

        public static void GetUserViewsPostfix(ref Folder[] __result)
        {
            try
            {
                var options = ForcedUserPreferencesRuntimeSettings.GetSnapshot();
                if (!options.Enabled || !options.ForceLibraryOrder || __result == null || __result.Length <= 1)
                    return;

                var orderedIds = ForcedUserPreferencesRuntimeSettings.GetLibraryOrderIds();
                if (orderedIds.Length == 0) return;

                var rank = orderedIds
                    .Select((id, index) => new { id, index })
                    .ToDictionary(entry => entry.id, entry => entry.index, StringComparer.OrdinalIgnoreCase);
                __result = __result
                    .Select((folder, originalIndex) => new { folder, originalIndex })
                    .OrderBy(entry =>
                    {
                        if (entry.folder == null) return int.MaxValue;
                        var id = entry.folder.Id.ToString("N");
                        return rank.TryGetValue(id, out var value) ? value : int.MaxValue;
                    })
                    .ThenBy(entry => entry.originalIndex)
                    .Select(entry => entry.folder)
                    .ToArray();
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Forced library order postfix skipped: " + ex.Message);
            }
        }
    }
}
