using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Services;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace StrmAssistant.Api
{
    public sealed class MediaMaintenancePlan
    {
        public bool Success { get; set; }
        public string Action { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string ItemPath { get; set; }
        public int MediaStreamCount { get; set; }
        public int ChapterCount { get; set; }
        public int ChapterImageReferenceCount { get; set; }
        public string PersistedMediaInfoJson { get; set; }
        public List<string> Paths { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    public sealed class MediaMaintenanceResult
    {
        public bool Success { get; set; }
        public bool Executed { get; set; }
        public string Action { get; set; }
        public MediaMaintenancePlan Plan { get; set; }
        public List<string> DeletedPaths { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/Maintenance/{Id}/ThumbnailCache/Plan", "GET",
        Summary = "Preview chapter JPEG and Emby thumbnail-set/BIF cache paths")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetThumbnailCacheMaintenancePlan : IReturn<MediaMaintenancePlan>
    {
        public string Id { get; set; }
    }

    [Route("/StrmAssistant/Maintenance/{Id}/ThumbnailCache/Clear", "POST",
        Summary = "Clear chapter JPEG references/files and Emby-resolved thumbnail-set/BIF files")]
    [Authenticated(Roles = "Admin")]
    public sealed class ClearThumbnailCacheMaintenance : IReturn<MediaMaintenanceResult>
    {
        public string Id { get; set; }
        public bool Confirm { get; set; }
    }

    [Route("/StrmAssistant/Maintenance/{Id}/MediaInfo/Plan", "GET",
        Summary = "Preview MediaInfo state before clearing it")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetMediaInfoMaintenancePlan : IReturn<MediaMaintenancePlan>
    {
        public string Id { get; set; }
    }

    [Route("/StrmAssistant/Maintenance/{Id}/MediaInfo/Clear", "POST",
        Summary = "Clear MediaInfo so the item can be freshly extracted")]
    [Authenticated(Roles = "Admin")]
    public sealed class ClearMediaInfoMaintenance : IReturn<MediaMaintenanceResult>
    {
        public string Id { get; set; }
        public bool Confirm { get; set; }
        public bool DeletePersistedJson { get; set; } = true;
    }

    /// <summary>
    /// Explicit admin maintenance endpoints. No library scan or background event invokes these methods.
    /// Thumbnail-set paths are obtained from Emby's own Video.GetThumbnailSetInfos API instead of guessed.
    /// </summary>
    public sealed class MediaMaintenanceApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IFileSystem _fileSystem;

        public MediaMaintenanceApiService(ILibraryManager libraryManager, IItemRepository itemRepository,
            IFileSystem fileSystem)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _fileSystem = fileSystem;
        }

        public object Get(GetThumbnailCacheMaintenancePlan request)
        {
            var item = ResolveItem(request?.Id) as Video;
            return item == null
                ? ErrorPlan("ThumbnailCache", request?.Id, "Video item was not found.")
                : BuildThumbnailPlan(item);
        }

        public object Post(ClearThumbnailCacheMaintenance request)
        {
            var item = ResolveItem(request?.Id) as Video;
            if (item == null)
                return ErrorResult("ThumbnailCache", request?.Id, "Video item was not found.");

            var plan = BuildThumbnailPlan(item);
            var result = new MediaMaintenanceResult { Action = "ThumbnailCache", Plan = plan };
            if (!plan.Success)
            {
                result.Errors.AddRange(plan.Errors);
                return result;
            }

            if (request?.Confirm != true)
            {
                result.Warnings.Add("Clear was not confirmed. Review Plan.Paths and set Confirm=true.");
                return result;
            }

            var chapters = _itemRepository.GetChapters(item);
            foreach (var path in plan.Paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (_fileSystem.FileExists(path))
                    {
                        _fileSystem.DeleteFile(path);
                        result.DeletedPaths.Add(path);
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add(path + ": " + ex.Message);
                }
            }

            try
            {
                var changed = false;
                foreach (var chapter in chapters)
                {
                    if (string.IsNullOrWhiteSpace(chapter.ImagePath)) continue;
                    chapter.ImagePath = null;
                    chapter.ImageDateModified = default;
                    chapter.ImageTag = null;
                    changed = true;
                }

                if (changed) _itemRepository.SaveChapters(item.InternalId, chapters);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Unable to clear chapter image references: " + ex.Message);
            }

            result.Executed = true;
            result.Success = result.Errors.Count == 0;
            return result;
        }

        public object Get(GetMediaInfoMaintenancePlan request)
        {
            var item = ResolveItem(request?.Id);
            return item == null
                ? ErrorPlan("MediaInfo", request?.Id, "Media item was not found.")
                : BuildMediaInfoPlan(item);
        }

        public object Post(ClearMediaInfoMaintenance request)
        {
            var item = ResolveItem(request?.Id);
            if (item == null)
                return ErrorResult("MediaInfo", request?.Id, "Media item was not found.");

            var plan = BuildMediaInfoPlan(item);
            var result = new MediaMaintenanceResult { Action = "MediaInfo", Plan = plan };
            if (request?.Confirm != true)
            {
                result.Warnings.Add("Clear was not confirmed. Review the MediaInfo plan and set Confirm=true.");
                return result;
            }

            try
            {
                _itemRepository.SaveMediaStreams(item.InternalId, new List<MediaStream>(), CancellationToken.None);

                item.Size = 0;
                item.RunTimeTicks = null;
                item.Container = null;
                item.TotalBitrate = 0;
                item.Width = 0;
                item.Height = 0;

                _libraryManager.UpdateItems(new List<BaseItem> { item }, null,
                    ItemUpdateType.MetadataImport, false, false, null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Unable to clear MediaInfo fields/streams: " + ex.Message);
                return result;
            }

            if (request.DeletePersistedJson)
            {
                try
                {
                    var directoryService = Plugin.MediaInfoApi.GetMediaInfoRefreshOptions().DirectoryService;
                    Plugin.MediaInfoApi.DeleteMediaInfoJson(item, directoryService, "Explicit MediaInfo Clear");
                }
                catch (Exception ex)
                {
                    result.Errors.Add("MediaInfo was cleared, but persisted JSON cleanup failed: " + ex.Message);
                }
            }

            result.Executed = true;
            result.Success = result.Errors.Count == 0;
            return result;
        }

        private MediaMaintenancePlan BuildThumbnailPlan(Video item)
        {
            var plan = BasePlan("ThumbnailCache", item);
            var directoryService = new DirectoryService(Plugin.Instance.Logger, _fileSystem);
            var chapters = _itemRepository.GetChapters(item);
            plan.ChapterCount = chapters.Count;
            plan.ChapterImageReferenceCount = chapters.Count(c => !string.IsNullOrWhiteSpace(c.ImagePath));

            foreach (var chapter in chapters)
            {
                if (!string.IsNullOrWhiteSpace(chapter.ImagePath)) plan.Paths.Add(chapter.ImagePath);
            }

            var chapterDirectory = Path.Combine(item.GetInternalMetadataPath(), "chapters");
            try
            {
                foreach (var path in directoryService.GetFilePaths(chapterDirectory))
                    plan.Paths.Add(path);
            }
            catch (IOException)
            {
                // Missing chapter directory is a normal no-cache state.
            }
            catch (Exception ex)
            {
                plan.Warnings.Add("Unable to enumerate chapter directory: " + ex.Message);
            }

            try
            {
                foreach (var path in ResolveThumbnailSetPaths(item, directoryService))
                    plan.Paths.Add(path);
            }
            catch (Exception ex)
            {
                plan.Warnings.Add("Unable to resolve Emby thumbnail-set/BIF paths: " + ex.Message);
            }

            plan.Paths = plan.Paths.Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList();
            plan.Success = true;
            return plan;
        }

        private IEnumerable<string> ResolveThumbnailSetPaths(Video item, IDirectoryService directoryService)
        {
            var methods = typeof(Video).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => string.Equals(m.Name, "GetThumbnailSetInfos", StringComparison.Ordinal))
                .ToArray();

            MethodInfo selected = methods.FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length == 4 && p[0].ParameterType == typeof(string) &&
                       p[1].ParameterType == typeof(Guid) && p[2].ParameterType == typeof(bool) &&
                       typeof(IDirectoryService).IsAssignableFrom(p[3].ParameterType);
            });

            object response = null;
            if (selected != null)
            {
                response = selected.Invoke(null, new object[] { item.Path, item.Id, true, directoryService });
            }
            else
            {
                selected = methods.FirstOrDefault(m =>
                {
                    var p = m.GetParameters();
                    return p.Length == 5 && p[0].ParameterType == typeof(string) &&
                           p[1].ParameterType == typeof(Guid) &&
                           typeof(IDirectoryService).IsAssignableFrom(p[2].ParameterType) &&
                           p[3].ParameterType == typeof(int) && p[4].ParameterType == typeof(bool);
                });

                if (selected != null)
                    response = selected.Invoke(null, new object[] { item.Path, item.Id, directoryService, 0, false });
            }

            if (!(response is IEnumerable values)) yield break;
            foreach (var value in values)
            {
                var path = value?.GetType().GetProperty("Path", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(value) as string;
                if (!string.IsNullOrWhiteSpace(path)) yield return path;
            }
        }

        private MediaMaintenancePlan BuildMediaInfoPlan(BaseItem item)
        {
            var plan = BasePlan("MediaInfo", item);
            try
            {
                plan.MediaStreamCount = item.GetMediaStreams()?.Count ?? 0;
            }
            catch
            {
                plan.MediaStreamCount = 0;
            }

            try
            {
                plan.PersistedMediaInfoJson = Common.MediaInfoApi.GetMediaInfoJsonPath(item);
                if (!string.IsNullOrWhiteSpace(plan.PersistedMediaInfoJson) &&
                    _fileSystem.FileExists(plan.PersistedMediaInfoJson))
                    plan.Paths.Add(plan.PersistedMediaInfoJson);
            }
            catch (Exception ex)
            {
                plan.Warnings.Add("Unable to resolve persisted MediaInfo JSON: " + ex.Message);
            }

            plan.Success = true;
            return plan;
        }

        private static MediaMaintenancePlan BasePlan(string action, BaseItem item)
        {
            return new MediaMaintenancePlan
            {
                Action = action,
                ItemId = item.InternalId.ToString(),
                ItemName = item.Name,
                ItemPath = item.Path
            };
        }

        private BaseItem ResolveItem(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !long.TryParse(id, out var internalId)) return null;
            return _libraryManager.GetItemById(internalId);
        }

        private static MediaMaintenancePlan ErrorPlan(string action, string id, string error)
        {
            return new MediaMaintenancePlan
            {
                Success = false,
                Action = action,
                ItemId = id,
                Errors = new List<string> { error }
            };
        }

        private static MediaMaintenanceResult ErrorResult(string action, string id, string error)
        {
            return new MediaMaintenanceResult
            {
                Success = false,
                Executed = false,
                Action = action,
                Plan = ErrorPlan(action, id, error),
                Errors = new List<string> { error }
            };
        }
    }
}
