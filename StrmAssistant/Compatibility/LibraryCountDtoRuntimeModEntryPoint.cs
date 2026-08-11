using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Dto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class LibraryCountDtoCapabilityStatus
    {
        public int TargetsFound { get; set; }
        public int TargetsPatched { get; set; }
        public bool RecursiveItemCountPropertyFound { get; set; }
        public List<string> Targets { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    public static class LibraryCountDtoModState
    {
        public static LibraryCountDtoCapabilityStatus Status { get; internal set; } =
            new LibraryCountDtoCapabilityStatus();
    }

    public sealed class LibraryCountDtoRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.library-count-dto";
        private Harmony _harmony;

        public LibraryCountDtoRuntimeModEntryPoint(ILibraryManager libraryManager)
        {
            LibraryCountDtoPatches.LibraryManager = libraryManager;
        }

        public void Run()
        {
            var status = new LibraryCountDtoCapabilityStatus
            {
                RecursiveItemCountPropertyFound = typeof(BaseItemDto).GetProperty("RecursiveItemCount") != null
            };
            LibraryCountDtoModState.Status = status;

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
                var singlePostfix = typeof(LibraryCountDtoPatches).GetMethod(
                    nameof(LibraryCountDtoPatches.SingleDtoPostfix), BindingFlags.Public | BindingFlags.Static);
                var batchPostfix = typeof(LibraryCountDtoPatches).GetMethod(
                    nameof(LibraryCountDtoPatches.BatchDtoPostfix), BindingFlags.Public | BindingFlags.Static);

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
                Plugin.Instance?.Logger?.Warn("Library count DTO mod unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
        }
    }

    public static class LibraryCountDtoPatches
    {
        internal static ILibraryManager LibraryManager { get; set; }

        public static void SingleDtoPostfix(object[] __args, ref BaseItemDto __result)
        {
            try
            {
                if (!Enabled() || __args == null || __result == null) return;
                var item = __args.OfType<BaseItem>().FirstOrDefault();
                if (item is CollectionFolder folder) Apply(folder, __result);
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
                    if (items[index] is CollectionFolder folder && __result[index] != null)
                        Apply(folder, __result[index]);
                }
            }
            catch (Exception ex)
            {
                Debug(ex);
            }
        }

        private static void Apply(CollectionFolder folder, BaseItemDto dto)
        {
            if (LibraryManager == null || folder == null || dto == null) return;
            var itemType = GetPrimaryItemType(folder.CollectionType);
            if (string.IsNullOrWhiteSpace(itemType)) return;

            int count;
            try
            {
                count = LibraryManager.GetItemList(new InternalItemsQuery
                {
                    ParentIds = new[] { folder.InternalId },
                    Recursive = true,
                    IncludeItemTypes = new[] { itemType }
                }).Count();
            }
            catch
            {
                return;
            }

            var property = dto.GetType().GetProperty("RecursiveItemCount",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.CanWrite != true) return;
            var underlying = Nullable.GetUnderlyingType(property.PropertyType);
            var targetType = underlying ?? property.PropertyType;
            var converted = Convert.ChangeType(count, targetType);
            property.SetValue(dto, underlying == null ? converted : Activator.CreateInstance(property.PropertyType, converted));
        }

        private static string GetPrimaryItemType(string collectionType)
        {
            if (string.IsNullOrWhiteSpace(collectionType)) return null;
            switch (collectionType.Trim().ToLowerInvariant())
            {
                case "movies": return "Movie";
                case "tvshows": return "Series";
                case "music": return "MusicAlbum";
                case "books": return "Book";
                case "photos": return "Photo";
                case "homevideos": return "Video";
                case "musicvideos": return "MusicVideo";
                default: return null;
            }
        }

        private static bool Enabled()
        {
            return Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions?.DisplayLibraryItemCount == true;
        }

        private static void Debug(Exception ex)
        {
            if (Plugin.Instance?.DebugMode == true)
                Plugin.Instance.Logger.Debug("Library count DTO enhancement skipped: " + ex.Message);
        }
    }
}
