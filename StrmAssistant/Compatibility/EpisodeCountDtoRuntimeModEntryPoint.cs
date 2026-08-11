using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Dto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class EpisodeCountDtoCapabilityStatus
    {
        public int TargetsFound { get; set; }
        public int TargetsPatched { get; set; }
        public bool UserDataPropertyFound { get; set; }
        public bool UnplayedItemCountPropertyFound { get; set; }
        public List<string> Targets { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    public static class EpisodeCountDtoModState
    {
        public static EpisodeCountDtoCapabilityStatus Status { get; internal set; } =
            new EpisodeCountDtoCapabilityStatus();
    }

    public sealed class EpisodeCountDtoRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.episode-count-dto";
        private Harmony _harmony;

        public EpisodeCountDtoRuntimeModEntryPoint(ILibraryManager libraryManager)
        {
            EpisodeCountDtoPatches.LibraryManager = libraryManager;
        }

        public void Run()
        {
            var userDataProperty = typeof(BaseItemDto).GetProperty("UserData");
            var userDataType = userDataProperty?.PropertyType;
            var status = new EpisodeCountDtoCapabilityStatus
            {
                UserDataPropertyFound = userDataProperty != null,
                UnplayedItemCountPropertyFound = userDataType?.GetProperty("UnplayedItemCount") != null
            };
            EpisodeCountDtoModState.Status = status;

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
                var singlePostfix = typeof(EpisodeCountDtoPatches).GetMethod(
                    nameof(EpisodeCountDtoPatches.SingleDtoPostfix), BindingFlags.Public | BindingFlags.Static);
                var batchPostfix = typeof(EpisodeCountDtoPatches).GetMethod(
                    nameof(EpisodeCountDtoPatches.BatchDtoPostfix), BindingFlags.Public | BindingFlags.Static);

                foreach (var method in dtoService.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    HarmonyMethod postfix = null;
                    if (string.Equals(method.Name, "GetBaseItemDto", StringComparison.Ordinal) &&
                        method.ReturnType == typeof(BaseItemDto) &&
                        method.GetParameters().Any(parameter => typeof(BaseItem).IsAssignableFrom(parameter.ParameterType)))
                        postfix = new HarmonyMethod(singlePostfix);
                    else if (string.Equals(method.Name, "GetBaseItemDtos", StringComparison.Ordinal) &&
                             method.ReturnType == typeof(BaseItemDto[]) &&
                             method.GetParameters().Any(parameter => parameter.ParameterType == typeof(BaseItem[])))
                        postfix = new HarmonyMethod(batchPostfix);

                    if (postfix == null) continue;
                    status.TargetsFound++;
                    _harmony.Patch(method, postfix: postfix);
                    status.TargetsPatched++;
                    status.Targets.Add(method.ToString());
                }
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Episode count DTO mod unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
        }
    }

    public static class EpisodeCountDtoPatches
    {
        internal static ILibraryManager LibraryManager { get; set; }

        public static void SingleDtoPostfix(object[] __args, ref BaseItemDto __result)
        {
            try
            {
                if (!Enabled() || __args == null || __result == null) return;
                var item = __args.OfType<BaseItem>().FirstOrDefault();
                if (item != null) Apply(item, __result);
            }
            catch (Exception ex)
            {
                Debug(ex);
            }
        }

        public static void BatchDtoPostfix(object[] __args, ref BaseItemDto[] __result)
        {
            try
            {
                if (!Enabled() || __args == null || __result == null) return;
                var items = __args.OfType<BaseItem[]>().FirstOrDefault();
                if (items == null) return;
                var count = Math.Min(items.Length, __result.Length);
                for (var index = 0; index < count; index++)
                {
                    if (__result[index] != null) Apply(items[index], __result[index]);
                }
            }
            catch (Exception ex)
            {
                Debug(ex);
            }
        }

        private static void Apply(BaseItem item, BaseItemDto dto)
        {
            if (LibraryManager == null || item == null || dto == null || dto.UserData == null) return;
            int count;
            if (item is Series series)
                count = CountSeriesEpisodes(series, null);
            else if (item is Season season && season.Series != null)
                count = CountSeriesEpisodes(season.Series, season.IndexNumber);
            else
                return;

            if (count < 0) return;
            SetCount(dto.UserData, count);
        }

        private static int CountSeriesEpisodes(Series series, int? seasonNumber)
        {
            try
            {
                var episodes = LibraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    ParentWithPresentationUniqueKeyFromItemId = series.InternalId,
                    Recursive = true
                }).OfType<Episode>()
                    .Where(episode => episode.Series?.InternalId == series.InternalId);

                if (seasonNumber.HasValue)
                    episodes = episodes.Where(episode => episode.ParentIndexNumber == seasonNumber.Value);
                return episodes.Select(episode => episode.InternalId).Distinct().Count();
            }
            catch
            {
                return -1;
            }
        }

        private static void SetCount(object userData, int count)
        {
            if (userData == null) return;
            var property = userData.GetType().GetProperty("UnplayedItemCount",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.CanWrite != true) return;

            var underlying = Nullable.GetUnderlyingType(property.PropertyType);
            var targetType = underlying ?? property.PropertyType;
            var converted = Convert.ChangeType(count, targetType);
            property.SetValue(userData, underlying == null ? converted : Activator.CreateInstance(property.PropertyType, converted));
        }

        private static bool Enabled()
        {
            return Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions?.DisplayTotalEpisodeCount == true;
        }

        private static void Debug(Exception ex)
        {
            if (Plugin.Instance?.DebugMode == true)
                Plugin.Instance.Logger.Debug("Episode count DTO enhancement skipped: " + ex.Message);
        }
    }
}
