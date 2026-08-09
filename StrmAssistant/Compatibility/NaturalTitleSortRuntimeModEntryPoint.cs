using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.Experience;
using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace StrmAssistant.Compatibility
{
    public sealed class NaturalTitleSortCapabilityStatus
    {
        public int TargetsFound { get; set; }
        public int TargetsPatched { get; set; }
        public string Error { get; set; }
    }

    public static class NaturalTitleSortModState
    {
        public static NaturalTitleSortCapabilityStatus Status { get; internal set; } =
            new NaturalTitleSortCapabilityStatus();
    }

    public sealed class NaturalTitleSortRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.natural-title-sort";
        private Harmony _harmony;

        public void Run()
        {
            var status = new NaturalTitleSortCapabilityStatus();
            NaturalTitleSortModState.Status = status;
            try
            {
                var targets = typeof(BaseItem)
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method => string.Equals(method.Name, "CreateSortName", StringComparison.Ordinal) &&
                                     method.ReturnType == typeof(string))
                    .ToArray();
                status.TargetsFound = targets.Length;
                if (targets.Length == 0) return;

                _harmony = new Harmony(HarmonyId);
                var postfix = typeof(NaturalTitleSortPatches).GetMethod(
                    nameof(NaturalTitleSortPatches.CreateSortNamePostfix),
                    BindingFlags.Public | BindingFlags.Static);
                foreach (var target in targets)
                {
                    _harmony.Patch(target, postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
                    status.TargetsPatched++;
                }
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Natural title sort mod unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
        }
    }

    public static class NaturalTitleSortPatches
    {
        private static readonly Regex NumberPattern = new Regex(@"\d+", RegexOptions.Compiled);

        [HarmonyPriority(Priority.Last)]
        public static void CreateSortNamePostfix(ref string __result)
        {
            try
            {
                var options = UiSortRuntimeSettings.GetSnapshot();
                if (!options.Enabled || !options.NaturalTitleSort || string.IsNullOrEmpty(__result)) return;
                __result = NumberPattern.Replace(__result, match =>
                {
                    var raw = match.Value.TrimStart('0');
                    if (raw.Length == 0) raw = "0";
                    return raw.Length.ToString("D4") + ":" + raw.PadLeft(32, '0');
                });
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Natural title sort skipped: " + ex.Message);
            }
        }
    }
}
