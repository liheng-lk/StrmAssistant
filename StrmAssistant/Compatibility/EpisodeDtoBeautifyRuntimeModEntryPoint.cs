using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace StrmAssistant.Compatibility
{
    public sealed class EpisodeDtoBeautifyCapabilityStatus
    {
        public int SingleTargetsFound { get; set; }
        public int SingleTargetsPatched { get; set; }
        public int BatchTargetsFound { get; set; }
        public int BatchTargetsPatched { get; set; }
        public List<string> Targets { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    public static class EpisodeDtoBeautifyModState
    {
        public static EpisodeDtoBeautifyCapabilityStatus Status { get; internal set; } =
            new EpisodeDtoBeautifyCapabilityStatus();
    }

    public sealed class EpisodeDtoBeautifyRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.episode-dto-beautify";
        private Harmony _harmony;

        public void Run()
        {
            var status = new EpisodeDtoBeautifyCapabilityStatus();
            EpisodeDtoBeautifyModState.Status = status;
            try
            {
                var assembly = Assembly.Load("Emby.Server.Implementations");
                var dtoService = assembly.GetType("Emby.Server.Implementations.Dto.DtoService");
                if (dtoService == null)
                {
                    status.Error = "DtoService type was not found.";
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                var singlePostfix = typeof(EpisodeDtoBeautifyPatches).GetMethod(
                    nameof(EpisodeDtoBeautifyPatches.SingleDtoPostfix), BindingFlags.Public | BindingFlags.Static);
                var batchPostfix = typeof(EpisodeDtoBeautifyPatches).GetMethod(
                    nameof(EpisodeDtoBeautifyPatches.BatchDtoPostfix), BindingFlags.Public | BindingFlags.Static);

                foreach (var method in dtoService.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (string.Equals(method.Name, "GetBaseItemDto", StringComparison.Ordinal) &&
                        method.ReturnType == typeof(BaseItemDto) &&
                        method.GetParameters().Any(parameter => typeof(BaseItem).IsAssignableFrom(parameter.ParameterType)))
                    {
                        status.SingleTargetsFound++;
                        _harmony.Patch(method, postfix: new HarmonyMethod(singlePostfix));
                        status.SingleTargetsPatched++;
                        status.Targets.Add(method.ToString());
                    }
                    else if (string.Equals(method.Name, "GetBaseItemDtos", StringComparison.Ordinal) &&
                             method.ReturnType == typeof(BaseItemDto[]) &&
                             method.GetParameters().Any(parameter => parameter.ParameterType == typeof(BaseItem[])))
                    {
                        status.BatchTargetsFound++;
                        _harmony.Patch(method, postfix: new HarmonyMethod(batchPostfix));
                        status.BatchTargetsPatched++;
                        status.Targets.Add(method.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Episode DTO beautify mod unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
        }
    }

    public static class EpisodeDtoBeautifyPatches
    {
        private static readonly Regex GenericEpisodeTitle = new Regex(
            @"^(?:episode|ep|e)\s*0*\d+\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static void SingleDtoPostfix(object[] __args, ref BaseItemDto __result)
        {
            try
            {
                var options = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
                if (options?.BeautifyMissingEpisodeMetadata != true || __args == null || __result == null) return;
                var item = __args.OfType<BaseItem>().FirstOrDefault();
                if (item is Episode episode) ApplyEpisodeTitle(episode, __result);
            }
            catch (Exception ex)
            {
                Debug("single DTO", ex);
            }
        }

        public static void BatchDtoPostfix(object[] __args, ref BaseItemDto[] __result)
        {
            try
            {
                if (__args == null || __result == null || __result.Length == 0) return;
                var options = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
                if (options == null) return;

                var items = __args.OfType<BaseItem[]>().FirstOrDefault();
                if (items == null || items.Length == 0) return;
                var count = Math.Min(items.Length, __result.Length);

                if (options.BeautifyMissingEpisodeMetadata)
                {
                    for (var index = 0; index < count; index++)
                    {
                        if (items[index] is Episode episode && __result[index] != null)
                            ApplyEpisodeTitle(episode, __result[index]);
                    }
                }

                if (options.BeautifyMultipartTitles)
                {
                    var partIndex = 1;
                    for (var index = 0; index < count; index++)
                    {
                        var item = items[index];
                        var dto = __result[index];
                        if (item?.ExtraType != ExtraType.AdditionalPart || dto == null) continue;
                        partIndex++;
                        dto.Name = IsChineseUi(item)
                            ? "第 " + partIndex + " 部分"
                            : "Part " + partIndex;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug("batch DTO", ex);
            }
        }

        private static void ApplyEpisodeTitle(Episode episode, BaseItemDto dto)
        {
            if (episode == null || dto == null || !episode.IndexNumber.HasValue) return;
            var name = episode.Name;
            var fileName = string.Empty;
            try { fileName = Path.GetFileNameWithoutExtension(episode.Path ?? string.Empty); }
            catch { }

            var missing = string.IsNullOrWhiteSpace(name) ||
                          (!string.IsNullOrWhiteSpace(fileName) &&
                           string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) ||
                          GenericEpisodeTitle.IsMatch(name ?? string.Empty);
            if (!missing) return;

            if (IsChineseUi(episode))
            {
                dto.Name = episode.ParentIndexNumber.HasValue
                    ? "第 " + episode.ParentIndexNumber.Value + " 季 第 " + episode.IndexNumber.Value + " 集"
                    : "第 " + episode.IndexNumber.Value + " 集";
            }
            else
            {
                dto.Name = episode.ParentIndexNumber.HasValue
                    ? "S" + episode.ParentIndexNumber.Value.ToString("00") + "E" + episode.IndexNumber.Value.ToString("00")
                    : "Episode " + episode.IndexNumber.Value;
            }
        }

        private static bool IsChineseUi(BaseItem item)
        {
            try
            {
                var language = item.GetPreferredMetadataLanguage();
                return !string.IsNullOrWhiteSpace(language) &&
                       language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void Debug(string scope, Exception ex)
        {
            if (Plugin.Instance?.DebugMode == true)
                Plugin.Instance.Logger.Debug("Episode DTO beautify " + scope + " skipped: " + ex.Message);
        }
    }
}
