using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TinyPinyin;

namespace StrmAssistant.Compatibility
{
    public sealed class PinyinSortCapabilityStatus
    {
        public bool SortNameTargetFound { get; set; }
        public bool SortNamePatched { get; set; }
        public string SortNameTarget { get; set; }
        public bool PrefixTargetsFound { get; set; }
        public bool PrefixTargetsPatched { get; set; }
        public string Error { get; set; }
    }

    public static class PinyinSortModState
    {
        public static PinyinSortCapabilityStatus Status { get; internal set; } = new PinyinSortCapabilityStatus();
    }

    public sealed class PinyinSortRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.pinyin-sort";
        private Harmony _harmony;

        public void Run()
        {
            var status = new PinyinSortCapabilityStatus();
            PinyinSortModState.Status = status;
            try
            {
                _harmony = new Harmony(HarmonyId);
                PatchSortName(status);
                PatchPrefixEndpoints(status);
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("PinyinSort runtime mod initialization failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try
            {
                _harmony?.UnpatchAll(HarmonyId);
            }
            catch
            {
                // Best effort during plugin shutdown.
            }
        }

        private void PatchSortName(PinyinSortCapabilityStatus status)
        {
            try
            {
                var target = typeof(BaseItem).GetMethod("CreateSortName",
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(ReadOnlySpan<char>) }, null);
                status.SortNameTargetFound = target != null;
                status.SortNameTarget = target?.ToString();
                if (target == null) return;

                var postfix = typeof(PinyinSortPatches).GetMethod(nameof(PinyinSortPatches.CreateSortNamePostfix),
                    BindingFlags.Static | BindingFlags.Public);
                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                status.SortNamePatched = true;
            }
            catch (Exception ex)
            {
                status.Error = "SortName patch: " + ex.Message;
            }
        }

        private void PatchPrefixEndpoints(PinyinSortCapabilityStatus status)
        {
            try
            {
                var embyApi = Assembly.Load("Emby.Api");
                var tagService = embyApi.GetType("Emby.Api.UserLibrary.TagService");
                var getPrefixesType = embyApi.GetType("Emby.Api.UserLibrary.GetPrefixes");
                var getArtistPrefixesType = embyApi.GetType("Emby.Api.UserLibrary.GetArtistPrefixes");
                var targets = new List<MethodInfo>();

                if (tagService != null && getPrefixesType != null)
                {
                    var method = tagService.GetMethod("Get", new[] { getPrefixesType });
                    if (method != null) targets.Add(method);
                }
                if (tagService != null && getArtistPrefixesType != null)
                {
                    var method = tagService.GetMethod("Get", new[] { getArtistPrefixesType });
                    if (method != null) targets.Add(method);
                }

                status.PrefixTargetsFound = targets.Count > 0;
                if (targets.Count == 0) return;

                var postfix = typeof(PinyinSortPatches).GetMethod(nameof(PinyinSortPatches.GetPrefixesPostfix),
                    BindingFlags.Static | BindingFlags.Public);
                foreach (var target in targets)
                    _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                status.PrefixTargetsPatched = true;
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Debug("PinyinSort prefix patch unavailable: " + ex.Message);
            }
        }
    }

    public static class PinyinSortPatches
    {
        private static readonly Regex DefaultCollectionSuffix =
            new Regex(@"（系列）$", RegexOptions.Compiled);

        public static void CreateSortNamePostfix(BaseItem __instance, ref ReadOnlySpan<char> __result)
        {
            try
            {
                if (Plugin.Instance?.GetPluginOptions()?.MetadataEnhanceOptions?.PinyinSortName != true ||
                    __instance == null) return;

                if (!__instance.SupportsUserData || !__instance.EnableAlphaNumericSorting ||
                    __instance is IHasSeries ||
                    !(__instance is Video) && !(__instance is Audio) &&
                    !(__instance is IItemByName) && !(__instance is Folder) ||
                    __instance.IsFieldLocked(MetadataFields.SortName))
                    return;

                var current = new string(__result);
                if (!ContainsCjkIdeograph(current)) return;

                if (__instance is BoxSet)
                    current = DefaultCollectionSuffix.Replace(current, string.Empty).Trim();

                var initials = PinyinHelper.GetPinyinInitials(current);
                if (!string.IsNullOrWhiteSpace(initials))
                    __result = initials.AsSpan();
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("PinyinSort skipped: " + ex.Message);
            }
        }

        public static void GetPrefixesPostfix(ref object __result)
        {
            try
            {
                if (Plugin.Instance?.GetPluginOptions()?.MetadataEnhanceOptions?.PinyinSortName != true) return;
                if (!(__result is NameValuePair[] pairs)) return;

                var validChars = new HashSet<char>("#ABCDEFGHIJKLMNOPQRSTUVWXYZ");
                var filtered = pairs
                    .Where(p => p?.Name?.Length == 1 && validChars.Contains(char.ToUpperInvariant(p.Name[0])))
                    .ToArray();
                if (filtered.Length != pairs.Length && filtered.Any(p => p.Name[0] != '#'))
                    __result = filtered;
            }
            catch
            {
                // Prefix filtering is cosmetic; never affect the underlying browse request.
            }
        }

        private static bool ContainsCjkIdeograph(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            foreach (var ch in value)
            {
                if ((ch >= '\u3400' && ch <= '\u4DBF') ||
                    (ch >= '\u4E00' && ch <= '\u9FFF') ||
                    (ch >= '\uF900' && ch <= '\uFAFF'))
                    return true;
            }
            return false;
        }
    }
}
