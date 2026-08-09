using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Services;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    public sealed class MediaInfoSyncStatus
    {
        public bool Ready { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string ItemPath { get; set; }
        public bool ItemHasMediaInfo { get; set; }
        public bool LibraryInScope { get; set; }
        public string PersistMode { get; set; }
        public string SharedRoot { get; set; }
        public string JsonPath { get; set; }
        public bool JsonExists { get; set; }
        public long JsonSize { get; set; }
        public string JsonModifiedUtc { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class MediaInfoSyncActionResult
    {
        public bool Success { get; set; }
        public bool Executed { get; set; }
        public string Action { get; set; }
        public MediaInfoSyncStatus Status { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/MediaInfoSync/{Id}/Status", "GET",
        Summary = "Inspect the shared MediaInfo JSON sync state for one item")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetMediaInfoSyncStatus : IReturn<MediaInfoSyncStatus>
    {
        public string Id { get; set; }
    }

    [Route("/StrmAssistant/MediaInfoSync/{Id}/Export", "POST",
        Summary = "Export one item's current MediaInfo to the configured shared JSON root")]
    [Authenticated(Roles = "Admin")]
    public sealed class ExportMediaInfoSync : IReturn<MediaInfoSyncActionResult>
    {
        public string Id { get; set; }
        public bool Overwrite { get; set; }
        public bool Confirm { get; set; }
    }

    [Route("/StrmAssistant/MediaInfoSync/{Id}/Import", "POST",
        Summary = "Restore missing MediaInfo from the configured shared JSON root")]
    [Authenticated(Roles = "Admin")]
    public sealed class ImportMediaInfoSync : IReturn<MediaInfoSyncActionResult>
    {
        public string Id { get; set; }
        public bool Confirm { get; set; }
    }

    /// <summary>
    /// Safe first-stage cross-server synchronization based on the existing portable
    /// MediaInfo JSON format. Servers share/synchronize the configured JSON root;
    /// this API never opens or writes another Emby server's database.
    /// </summary>
    public sealed class MediaInfoSyncApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;

        public MediaInfoSyncApiService(ILibraryManager libraryManager, IFileSystem fileSystem)
        {
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
        }

        public object Get(GetMediaInfoSyncStatus request)
        {
            var item = ResolveItem(request?.Id);
            return item == null ? MissingItemStatus(request?.Id) : BuildStatus(item);
        }

        public async Task<object> Post(ExportMediaInfoSync request)
        {
            var item = ResolveItem(request?.Id);
            if (item == null) return ErrorResult("Export", request?.Id, "Media item was not found.");

            var status = BuildStatus(item);
            var result = new MediaInfoSyncActionResult { Action = "Export", Status = status };
            if (!status.Ready)
            {
                result.Errors.Add("MediaInfo sync is not ready. Configure a non-empty MediaInfo JSON root and enable a persistence mode first.");
                return result;
            }

            if (!status.LibraryInScope)
            {
                result.Errors.Add("The item is outside the configured MediaInfo library scope.");
                return result;
            }

            if (!status.ItemHasMediaInfo)
            {
                result.Errors.Add("The source item has no MediaInfo to export.");
                return result;
            }

            if (request?.Overwrite == true && request.Confirm != true)
            {
                result.Warnings.Add("Overwrite was requested but not confirmed. Set Confirm=true after reviewing Status.");
                return result;
            }

            var directoryService = new DirectoryService(Plugin.Instance.Logger, _fileSystem);
            var exported = await Plugin.MediaInfoApi
                .SerializeMediaInfo(item.InternalId, directoryService, request?.Overwrite == true,
                    "MediaInfoSync Export")
                .ConfigureAwait(false);

            result.Status = BuildStatus(item);
            result.Executed = exported;
            result.Success = exported || result.Status.JsonExists;
            if (!exported && result.Status.JsonExists && request?.Overwrite != true)
                result.Warnings.Add("The shared JSON already exists. Nothing was overwritten.");
            if (!result.Success)
                result.Errors.Add("MediaInfo export did not create a shared JSON file.");
            return result;
        }

        public async Task<object> Post(ImportMediaInfoSync request)
        {
            var item = ResolveItem(request?.Id);
            if (item == null) return ErrorResult("Import", request?.Id, "Media item was not found.");

            var status = BuildStatus(item);
            var result = new MediaInfoSyncActionResult { Action = "Import", Status = status };
            if (!status.Ready)
            {
                result.Errors.Add("MediaInfo sync is not ready. Configure a non-empty MediaInfo JSON root and enable a persistence mode first.");
                return result;
            }

            if (!status.JsonExists)
            {
                result.Errors.Add("No shared MediaInfo JSON exists for this item.");
                return result;
            }

            if (status.ItemHasMediaInfo)
            {
                result.Warnings.Add("Safe import mode does not overwrite an item that already has MediaInfo. Clear/restore workflows must remain explicit.");
                result.Success = true;
                return result;
            }

            if (request?.Confirm != true)
            {
                result.Warnings.Add("Import was not confirmed. Set Confirm=true after reviewing Status.");
                return result;
            }

            var directoryService = new DirectoryService(Plugin.Instance.Logger, _fileSystem);
            var imported = await Plugin.MediaInfoApi
                .DeserializeMediaInfo(item, directoryService, "MediaInfoSync Import", false)
                .ConfigureAwait(false);

            result.Status = BuildStatus(item);
            result.Executed = imported && result.Status.ItemHasMediaInfo;
            result.Success = result.Executed;
            if (!result.Success)
            {
                result.Errors.Add("MediaInfo import was skipped or failed. The existing restore logic also rejects stale JSON when the media file has changed.");
            }

            return result;
        }

        private MediaInfoSyncStatus BuildStatus(BaseItem item)
        {
            var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
            var root = options?.MediaInfoJsonRootFolder?.Trim();
            var persistMode = options?.PersistMediaInfoMode ?? MediaInfoExtractOptions.PersistMediaInfoOption.None.ToString();
            var rootConfigured = !string.IsNullOrWhiteSpace(root);
            var persistenceEnabled = persistMode != MediaInfoExtractOptions.PersistMediaInfoOption.None.ToString();
            var jsonPath = MediaInfoApi.GetMediaInfoJsonPath(item);
            var file = string.IsNullOrWhiteSpace(jsonPath) ? null : _fileSystem.GetFileInfo(jsonPath);

            var status = new MediaInfoSyncStatus
            {
                Ready = rootConfigured && persistenceEnabled,
                ItemId = item.InternalId.ToString(),
                ItemName = item.Name,
                ItemPath = item.Path,
                ItemHasMediaInfo = Plugin.LibraryApi.HasMediaInfo(item),
                LibraryInScope = Plugin.LibraryApi.IsLibraryInScope(item),
                PersistMode = persistMode,
                SharedRoot = root,
                JsonPath = jsonPath,
                JsonExists = file?.Exists == true,
                JsonSize = file?.Length ?? 0,
                JsonModifiedUtc = file?.Exists == true ? file.LastWriteTimeUtc.ToString("O") : null
            };

            if (!rootConfigured)
                status.Warnings.Add("MediaInfoJsonRootFolder is empty. Central/shared-root sync intentionally refuses to use sidecar JSON next to media files.");
            if (!persistenceEnabled)
                status.Warnings.Add("PersistMediaInfoMode is None. Enable MediaInfo persistence before using sync.");
            if (!status.LibraryInScope)
                status.Warnings.Add("This item is outside the configured MediaInfo library scope.");
            if (item.IsShortcut)
                status.Warnings.Add("STRM item identity depends on both servers resolving the same logical media item; validate the generated JSON path before importing.");

            return status;
        }

        private BaseItem ResolveItem(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            if (!long.TryParse(id, out var internalId)) return null;
            return _libraryManager.GetItemById(internalId);
        }

        private static MediaInfoSyncStatus MissingItemStatus(string id)
        {
            return new MediaInfoSyncStatus
            {
                Ready = false,
                ItemId = id,
                Warnings = new List<string> { "Media item was not found." }
            };
        }

        private static MediaInfoSyncActionResult ErrorResult(string action, string id, string error)
        {
            return new MediaInfoSyncActionResult
            {
                Action = action,
                Success = false,
                Executed = false,
                Status = MissingItemStatus(id),
                Errors = new List<string> { error }
            };
        }
    }
}