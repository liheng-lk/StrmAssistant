using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Services;
using StrmAssistant.Common;
using StrmAssistant.MediaEnhance;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    public sealed class MediaInfoSyncStatus
    {
        public bool Ready { get; set; }
        public bool Enabled { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string ItemPath { get; set; }
        public bool ItemHasMediaInfo { get; set; }
        public bool LibraryInScope { get; set; }
        public string PersistMode { get; set; }
        public string SharedRoot { get; set; }
        public bool MappingRequired { get; set; }
        public bool MappingMatched { get; set; }
        public string MatchedLocalRoot { get; set; }
        public string LogicalRoot { get; set; }
        public string SyncKey { get; set; }
        public bool PortableKey { get; set; }
        public string JsonPath { get; set; }
        public string LegacyJsonPath { get; set; }
        public bool JsonExists { get; set; }
        public long JsonSize { get; set; }
        public string JsonModifiedUtc { get; set; }
        public string JsonSha256 { get; set; }
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
        Summary = "Inspect the portable shared MediaInfo JSON sync state for one item")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetMediaInfoSyncStatus : IReturn<MediaInfoSyncStatus>
    {
        public string Id { get; set; }
    }

    [Route("/StrmAssistant/MediaInfoSync/{Id}/Export", "POST",
        Summary = "Export one item's current MediaInfo to its portable shared sync key")]
    [Authenticated(Roles = "Admin")]
    public sealed class ExportMediaInfoSync : IReturn<MediaInfoSyncActionResult>
    {
        public string Id { get; set; }
        public bool Overwrite { get; set; }
        public bool Confirm { get; set; }
    }

    [Route("/StrmAssistant/MediaInfoSync/{Id}/Import", "POST",
        Summary = "Restore missing MediaInfo from its portable shared sync key")]
    [Authenticated(Roles = "Admin")]
    public sealed class ImportMediaInfoSync : IReturn<MediaInfoSyncActionResult>
    {
        public string Id { get; set; }
        public bool Confirm { get; set; }
    }

    /// <summary>
    /// Safe cross-server synchronization based on the existing portable MediaInfo JSON format.
    /// Host-specific media roots can map to a common logical root so Linux/Windows servers resolve
    /// the same SyncKey. This service never opens or writes another Emby server's database.
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
                result.Errors.Add("MediaInfo sync is not ready. Review Status warnings and the shared-sync configuration first.");
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

            if (status.JsonExists && request?.Overwrite != true)
            {
                result.Success = true;
                result.Warnings.Add("The portable shared JSON already exists. Nothing was overwritten.");
                return result;
            }

            if (request?.Overwrite == true && request.Confirm != true)
            {
                result.Warnings.Add("Overwrite was requested but not confirmed. Set Confirm=true after reviewing Status and JsonSha256.");
                return result;
            }

            try
            {
                var directoryService = Plugin.MediaInfoApi.GetMediaInfoRefreshOptions().DirectoryService;
                var legacyPath = MediaInfoApi.GetMediaInfoJsonPath(item);

                // Explicit export refreshes the local persistence representation first so the
                // canonical shared copy always reflects the current Emby item state.
                var serialized = await Plugin.MediaInfoApi
                    .SerializeMediaInfo(item.InternalId, directoryService, true, "MediaInfoSync Export staging")
                    .ConfigureAwait(false);

                if (!serialized && !_fileSystem.FileExists(legacyPath))
                {
                    result.Errors.Add("MediaInfo serialization did not produce a staging JSON file.");
                    return result;
                }

                if (!SamePath(legacyPath, status.JsonPath))
                {
                    EnsureParentDirectory(status.JsonPath);
                    _fileSystem.CopyFile(legacyPath, status.JsonPath, true);
                }

                result.Status = BuildStatus(item);
                result.Executed = true;
                result.Success = result.Status.JsonExists;
                if (!result.Success)
                    result.Errors.Add("MediaInfo export completed without a visible shared JSON file.");
                return result;
            }
            catch (Exception ex)
            {
                result.Errors.Add("MediaInfo export failed: " + ex.Message);
                return result;
            }
        }

        public async Task<object> Post(ImportMediaInfoSync request)
        {
            var item = ResolveItem(request?.Id);
            if (item == null) return ErrorResult("Import", request?.Id, "Media item was not found.");

            var status = BuildStatus(item);
            var result = new MediaInfoSyncActionResult { Action = "Import", Status = status };
            if (!status.Ready)
            {
                result.Errors.Add("MediaInfo sync is not ready. Review Status warnings and the shared-sync configuration first.");
                return result;
            }

            if (!status.JsonExists)
            {
                result.Errors.Add("No shared MediaInfo JSON exists for this SyncKey.");
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
                result.Warnings.Add("Import was not confirmed. Set Confirm=true after reviewing SyncKey and JsonSha256.");
                return result;
            }

            var legacyPath = MediaInfoApi.GetMediaInfoJsonPath(item);
            var staged = !SamePath(legacyPath, status.JsonPath);
            var legacyExisted = staged && _fileSystem.FileExists(legacyPath);
            string backupPath = null;

            try
            {
                if (staged)
                {
                    EnsureParentDirectory(legacyPath);
                    if (legacyExisted)
                    {
                        var tempRoot = Plugin.Instance?.ApplicationPaths?.TempDirectory;
                        if (string.IsNullOrWhiteSpace(tempRoot)) tempRoot = Path.GetTempPath();
                        Directory.CreateDirectory(tempRoot);
                        backupPath = Path.Combine(tempRoot,
                            "strmassistant-mediainfo-sync-backup-" + Guid.NewGuid().ToString("N") + ".json");
                        _fileSystem.CopyFile(legacyPath, backupPath, true);
                    }

                    _fileSystem.CopyFile(status.JsonPath, legacyPath, true);
                }

                var directoryService = Plugin.MediaInfoApi.GetMediaInfoRefreshOptions().DirectoryService;
                var imported = await Plugin.MediaInfoApi
                    .DeserializeMediaInfo(item, directoryService, "MediaInfoSync Import", false)
                    .ConfigureAwait(false);

                result.Status = BuildStatus(item);
                result.Executed = imported && result.Status.ItemHasMediaInfo;
                result.Success = result.Executed;
                if (!result.Success)
                {
                    result.Errors.Add("MediaInfo import was skipped or failed. Existing restore logic rejects stale JSON when the media file has changed.");
                }

                return result;
            }
            catch (Exception ex)
            {
                result.Errors.Add("MediaInfo import failed: " + ex.Message);
                return result;
            }
            finally
            {
                if (staged)
                {
                    try
                    {
                        if (legacyExisted && !string.IsNullOrWhiteSpace(backupPath) && _fileSystem.FileExists(backupPath))
                            _fileSystem.CopyFile(backupPath, legacyPath, true);
                        else if (!legacyExisted && _fileSystem.FileExists(legacyPath))
                            _fileSystem.DeleteFile(legacyPath);
                    }
                    catch (Exception cleanupEx)
                    {
                        Plugin.Instance?.Logger?.Warn("MediaInfoSync - Unable to restore staging path: " + cleanupEx.Message);
                    }

                    try
                    {
                        if (!string.IsNullOrWhiteSpace(backupPath) && _fileSystem.FileExists(backupPath))
                            _fileSystem.DeleteFile(backupPath);
                    }
                    catch
                    {
                        // Backup cleanup is best effort only.
                    }
                }
            }
        }

        private MediaInfoSyncStatus BuildStatus(BaseItem item)
        {
            var options = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions;
            var root = options?.MediaInfoJsonRootFolder?.Trim();
            var persistMode = options?.PersistMediaInfoMode ?? MediaInfoExtractOptions.PersistMediaInfoOption.None.ToString();
            var enabled = options?.EnableMediaInfoSharedSync == true;
            var mappingRequired = options?.MediaInfoSyncRequireMappedPath != false;
            var rootConfigured = !string.IsNullOrWhiteSpace(root);
            var persistenceEnabled = persistMode != MediaInfoExtractOptions.PersistMediaInfoOption.None.ToString();
            var resolution = MediaInfoSyncPathResolver.Resolve(item, root, options?.MediaInfoSyncPathMappings);
            var jsonPath = resolution.Success ? resolution.JsonPath : null;
            var file = string.IsNullOrWhiteSpace(jsonPath) ? null : _fileSystem.GetFileInfo(jsonPath);

            var status = new MediaInfoSyncStatus
            {
                Enabled = enabled,
                Ready = enabled && rootConfigured && persistenceEnabled && resolution.Success &&
                        (!mappingRequired || resolution.MappingMatched),
                ItemId = item.InternalId.ToString(),
                ItemName = item.Name,
                ItemPath = item.Path,
                ItemHasMediaInfo = Plugin.LibraryApi.HasMediaInfo(item),
                LibraryInScope = Plugin.LibraryApi.IsLibraryInScope(item),
                PersistMode = persistMode,
                SharedRoot = root,
                MappingRequired = mappingRequired,
                MappingMatched = resolution.MappingMatched,
                MatchedLocalRoot = resolution.LocalRoot,
                LogicalRoot = resolution.LogicalRoot,
                SyncKey = resolution.SyncKey,
                PortableKey = resolution.MappingMatched,
                JsonPath = jsonPath,
                LegacyJsonPath = MediaInfoApi.GetMediaInfoJsonPath(item),
                JsonExists = file?.Exists == true,
                JsonSize = file?.Length ?? 0,
                JsonModifiedUtc = file?.Exists == true ? file.LastWriteTimeUtc.ToString("O") : null
            };

            if (status.JsonExists)
            {
                try
                {
                    status.JsonSha256 = ComputeSha256(status.JsonPath);
                }
                catch (Exception ex)
                {
                    status.Warnings.Add("Unable to calculate shared JSON SHA-256: " + ex.Message);
                }
            }

            if (!enabled)
                status.Warnings.Add("Shared MediaInfo sync is disabled in plugin options.");
            if (!rootConfigured)
                status.Warnings.Add("MediaInfoJsonRootFolder is empty. Shared sync never falls back to sidecar JSON next to the media file.");
            if (!persistenceEnabled)
                status.Warnings.Add("PersistMediaInfoMode is None. Enable MediaInfo persistence before using shared sync.");
            if (!resolution.Success)
                status.Warnings.Add(resolution.Error ?? "Unable to resolve a shared SyncKey.");
            if (resolution.Success && !resolution.MappingMatched)
                status.Warnings.Add("No path mapping matched. The fallback key depends on this host's filesystem layout and may not be portable to another server.");
            if (mappingRequired && !resolution.MappingMatched)
                status.Warnings.Add("A portable path mapping is required, so Export/Import is blocked until this item's path matches a mapping rule.");
            if (!status.LibraryInScope)
                status.Warnings.Add("This item is outside the configured MediaInfo library scope.");
            if (item.IsShortcut)
                status.Warnings.Add("STRM item identity uses its library-side path for the SyncKey. Both servers must map the logical STRM library tree consistently.");

            return status;
        }

        private BaseItem ResolveItem(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            if (!long.TryParse(id, out var internalId)) return null;
            return _libraryManager.GetItemById(internalId);
        }

        private void EnsureParentDirectory(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        }

        private static bool SamePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            try
            {
                var a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string ComputeSha256(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
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
