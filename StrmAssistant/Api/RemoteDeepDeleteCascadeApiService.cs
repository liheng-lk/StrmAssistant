using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using StrmAssistant.Experience;
using StrmAssistant.MediaEnhance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    public sealed class RemoteDeepDeleteCascadePreviewResponse
    {
        public bool Success { get; set; }
        public string PlanHash { get; set; }
        public RemoteDeepDeleteCascadePlan Plan { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class RemoteDeepDeleteCascadeApplyResponse
    {
        public bool Success { get; set; }
        public bool Executed { get; set; }
        public bool DryRun { get; set; }
        public string PlanHash { get; set; }
        public int RemoteCandidateCount { get; set; }
        public int UniqueRemotePathCount { get; set; }
        public int RemotePathsVerifiedDeleted { get; set; }
        public int LocalDeepDeleteItemCount { get; set; }
        public int LocalDeepDeleteEntriesPlanned { get; set; }
        public int LocalDeepDeleteItemsCompleted { get; set; }
        public int LocalRootsRequested { get; set; }
        public int LocalRootsDeleted { get; set; }
        public List<string> DeletedRemotePaths { get; set; } = new List<string>();
        public List<string> DeletedLocalPaths { get; set; } = new List<string>();
        public List<string> DeletedLocalDirectories { get; set; } = new List<string>();
        public List<string> SkippedLocalPaths { get; set; } = new List<string>();
        public List<string> DeletedRootItemIds { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/DeepDelete/{Id}/CascadePlan", "GET",
        Summary = "Preview all remote/local STRM leaves protected before deleting an item/folder")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetRemoteDeepDeleteCascadePlan : IReturn<RemoteDeepDeleteCascadePreviewResponse>
    {
        public string Id { get; set; }
        public int MaxRemoteCandidates { get; set; } = RemoteDeepDeleteCascadeService.DefaultMaxRemoteCandidates;
    }

    [Route("/StrmAssistant/DeepDelete/CascadePlan", "GET",
        Summary = "Preview all remote/local STRM leaves protected before a batch item deletion")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetRemoteDeepDeleteBatchCascadePlan : IReturn<RemoteDeepDeleteCascadePreviewResponse>
    {
        public string Ids { get; set; }
        public int MaxRemoteCandidates { get; set; } = RemoteDeepDeleteCascadeService.DefaultMaxRemoteCandidates;
    }

    [Route("/StrmAssistant/DeepDelete/{Id}/Cascade", "DELETE",
        Summary = "Execute a previously previewed mixed deep-delete cascade using an exact plan hash")]
    [Authenticated(Roles = "Admin")]
    public sealed class ExecuteRemoteDeepDeleteCascade : IReturn<RemoteDeepDeleteCascadeApplyResponse>
    {
        public string Id { get; set; }
        public bool Confirm { get; set; }
        public string PlanHash { get; set; }
    }

    [Route("/StrmAssistant/DeepDelete/Cascade", "POST",
        Summary = "Execute a previously previewed batch deep-delete cascade using an exact plan hash")]
    [Authenticated(Roles = "Admin")]
    public sealed class ExecuteRemoteDeepDeleteBatchCascade : IReturn<RemoteDeepDeleteCascadeApplyResponse>
    {
        public string Ids { get; set; }
        public bool Confirm { get; set; }
        public string PlanHash { get; set; }
    }

    public sealed class RemoteDeepDeleteCascadeApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly RemoteDeepDeleteCascadeService _cascade;
        private readonly RemoteDeepDeleteService _remote = new RemoteDeepDeleteService();
        private readonly DeepDeleteService _local = new DeepDeleteService();

        public RemoteDeepDeleteCascadeApiService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
            _cascade = new RemoteDeepDeleteCascadeService(libraryManager);
        }

        public object Get(GetRemoteDeepDeleteCascadePlan request)
        {
            var item = ResolveItem(request?.Id);
            if (item == null) return PreviewError("Item was not found or id is invalid.");
            return BuildPreview(new[] { item }, NormalizePreviewLimit(request?.MaxRemoteCandidates ?? 0));
        }

        public object Get(GetRemoteDeepDeleteBatchCascadePlan request)
        {
            var ids = SplitIds(request?.Ids).ToArray();
            if (ids.Length == 0) return PreviewError("Ids is empty.");
            var items = ids.Select(ResolveItem).Where(item => item != null).ToArray();
            if (items.Length != ids.Length)
                return PreviewError("One or more requested item ids could not be resolved. Batch preview is fail-closed.");
            return BuildPreview(items, NormalizePreviewLimit(request?.MaxRemoteCandidates ?? 0));
        }

        public async Task<object> Delete(ExecuteRemoteDeepDeleteCascade request)
        {
            var item = ResolveItem(request?.Id);
            if (item == null) return ApplyError(request?.PlanHash, "Item was not found or id is invalid.");
            return await ExecuteCascadeAsync(new[] { item }, request?.Confirm == true, request?.PlanHash)
                .ConfigureAwait(false);
        }

        public async Task<object> Post(ExecuteRemoteDeepDeleteBatchCascade request)
        {
            var ids = SplitIds(request?.Ids).ToArray();
            if (ids.Length == 0) return ApplyError(request?.PlanHash, "Ids is empty.");
            var items = ids.Select(ResolveItem).Where(item => item != null).ToArray();
            if (items.Length != ids.Length)
                return ApplyError(request?.PlanHash,
                    "One or more requested item ids could not be resolved. Batch execution is fail-closed.");
            return await ExecuteCascadeAsync(items, request?.Confirm == true, request?.PlanHash)
                .ConfigureAwait(false);
        }

        private RemoteDeepDeleteCascadePreviewResponse BuildPreview(IEnumerable<BaseItem> rootItems, int maxCandidates)
        {
            var roots = NormalizeRoots(rootItems).ToArray();
            var plan = _cascade.BuildPlan(roots, maxCandidates);
            var response = new RemoteDeepDeleteCascadePreviewResponse
            {
                Plan = plan,
                PlanHash = ComputePlanHash(roots, plan),
                Success = plan != null && plan.Allowed && plan.RemoteCandidateCount > 0
            };
            if (!string.IsNullOrWhiteSpace(plan?.Error)) response.Errors.Add(plan.Error);
            if (plan?.Warnings != null) response.Warnings.AddRange(plan.Warnings);
            var options = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            var remote = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            if (options?.EnableDeepDelete != true) response.Warnings.Add("Deep Delete is currently disabled.");
            if (!remote.Enabled) response.Warnings.Add("Remote Deep Delete is currently disabled.");
            if (!remote.TreatNotFoundAsSuccess)
                response.Warnings.Add("Confirmed cascade execution currently requires TreatNotFoundAsSuccess=true. The verified-delete journal protects direct retries, while multi-leaf cascade keeps the stronger global idempotence gate.");
            return response;
        }

        private async Task<RemoteDeepDeleteCascadeApplyResponse> ExecuteCascadeAsync(IEnumerable<BaseItem> rootItems,
            bool confirm, string expectedPlanHash)
        {
            var roots = NormalizeRoots(rootItems).ToArray();
            var options = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            var remoteOptions = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            var result = new RemoteDeepDeleteCascadeApplyResponse
            {
                DryRun = options?.DeepDeleteDryRun == true,
                LocalRootsRequested = roots.Length
            };

            if (!confirm)
                return AddError(result, "Cascade execution requires Confirm=true after reviewing CascadePlan.");
            if (string.IsNullOrWhiteSpace(expectedPlanHash))
                return AddError(result, "PlanHash is required. Fetch CascadePlan immediately before execution.");
            if (options?.EnableDeepDelete != true)
                return AddError(result, "Deep Delete is disabled in plugin options.");
            if (!remoteOptions.Enabled || remoteOptions.Provider == RemoteDeepDeleteProviderType.None)
                return AddError(result, "Remote Deep Delete is disabled or no provider is selected.");
            if (options.DeepDeleteDryRun)
            {
                result.Warnings.Add("Dry Run is enabled. No remote object, local target, or Emby item was deleted.");
                return result;
            }
            if (!remoteOptions.TreatNotFoundAsSuccess)
                return AddError(result,
                    "Cascade execution requires TreatNotFoundAsSuccess=true for safe retry across multiple remote leaves and later local phases.");

            var plan = _cascade.BuildPlan(roots, RemoteDeepDeleteCascadeService.DefaultMaxRemoteCandidates);
            result.RemoteCandidateCount = plan.RemoteCandidateCount;
            result.UniqueRemotePathCount = plan.UniqueRemotePathCount;
            result.LocalDeepDeleteItemCount = plan.LocalDeepDeleteItemCount;
            result.LocalDeepDeleteEntriesPlanned = plan.LocalDeepDeleteEntryCount;
            var actualHash = ComputePlanHash(roots, plan);
            result.PlanHash = actualHash;
            if (!FixedEquals(expectedPlanHash.Trim(), actualHash))
                return AddError(result,
                    "Cascade plan changed after preview. No deletion was performed. Fetch a fresh CascadePlan and use its new PlanHash.");
            if (!plan.Allowed || plan.RemoteCandidateCount <= 0)
                return AddError(result, plan.Error ?? "Cascade plan is not allowed or contains no remote media targets.");

            var groups = plan.Entries
                .Where(entry => entry.RequiresRemoteDelete && entry.Allowed && entry.RemotePlan?.RemotePath != null)
                .GroupBy(entry => entry.RemotePlan.RemotePath, StringComparer.Ordinal)
                .Select(group => new { First = group.First(), All = group.ToArray() })
                .ToArray();

            foreach (var group in groups)
            {
                var execution = await _remote.ExecuteAsync(group.First.RemotePlan, CancellationToken.None)
                    .ConfigureAwait(false);
                if (execution?.Success != true)
                {
                    result.Errors.Add(execution?.Error ??
                                      "Remote cascade deletion failed before all cloud targets were verified missing.");
                    if (!string.IsNullOrWhiteSpace(execution?.VerificationError))
                        result.Errors.Add("Verification: " + execution.VerificationError);
                    result.Warnings.Add(
                        "No new local target or Emby-root deletion was started after this remote failure. Previously verified cloud deletions remain retryable.");
                    return result;
                }
                result.RemotePathsVerifiedDeleted++;
                result.DeletedRemotePaths.Add(group.First.RemotePlan.RemotePath);
            }

            // Remote phase is complete. Execute every configured local STRM/symlink target plan before
            // touching the Emby root tree. A local failure leaves the Emby items intact; the verified
            // remote journal makes the already-completed cloud phase diagnosable/retryable.
            foreach (var entry in plan.Entries.Where(entry => entry.RequiresLocalDeepDelete))
            {
                if (entry.LocalPlan == null || !entry.LocalDeepDeleteAllowed)
                {
                    result.Errors.Add(entry.Error ??
                                      "A local deep-delete plan became unavailable or disallowed during cascade execution.");
                    return result;
                }

                var localExecution = _local.Execute(entry.LocalPlan, options);
                result.DeletedLocalPaths.AddRange(localExecution.DeletedPaths);
                result.DeletedLocalDirectories.AddRange(localExecution.DeletedDirectories);
                result.SkippedLocalPaths.AddRange(localExecution.SkippedPaths);
                if (localExecution.Errors.Count > 0)
                {
                    result.Errors.AddRange(localExecution.Errors.Select(error =>
                        "Local deep delete " + entry.ItemId + ": " + error));
                    result.Warnings.Add(
                        "All remote paths processed before this point remain verified/retryable, but Emby root deletion was not started because a configured local target phase failed.");
                    return result;
                }
                result.LocalDeepDeleteItemsCompleted++;
            }

            // Only after every remote and configured local target phase succeeds do we arm deferred
            // MediaInfo cleanup and ask Emby to delete the requested local/library roots.
            var pendingIds = new HashSet<long>();
            foreach (var entry in plan.Entries)
            {
                if (!long.TryParse(entry.ItemId, out var itemId)) continue;
                var item = _libraryManager.GetItemById(itemId);
                if (item == null || !MediaInfoReliabilityShadowStore.AppliesTo(item)) continue;
                NativeRemoteDeleteDeferredCleanupQueue.MarkPending(item, entry.RemotePlan?.RemotePath);
                pendingIds.Add(itemId);
            }

            foreach (var root in OrderRootsForDeletion(roots))
            {
                var current = _libraryManager.GetItemById(root.InternalId);
                if (current == null)
                {
                    result.LocalRootsDeleted++;
                    result.DeletedRootItemIds.Add(root.InternalId.ToString());
                    continue;
                }
                try
                {
                    _libraryManager.DeleteItem(current, new DeleteOptions
                    {
                        DeleteFileLocation = true,
                        DeleteFromExternalProvider = true
                    });
                }
                catch (Exception ex)
                {
                    result.Errors.Add("Emby local/library deletion failed for " + current.InternalId +
                                      " (" + current.Name + "): " + ex.Message);
                    continue;
                }

                if (_libraryManager.GetItemById(root.InternalId) == null)
                {
                    result.LocalRootsDeleted++;
                    result.DeletedRootItemIds.Add(root.InternalId.ToString());
                }
                else
                {
                    result.Errors.Add("Emby DeleteItem returned but root item still exists: " + root.InternalId);
                }
            }

            foreach (var itemId in pendingIds)
                if (_libraryManager.GetItemById(itemId) != null)
                    NativeRemoteDeleteDeferredCleanupQueue.CancelPending(itemId);

            result.Executed = result.RemotePathsVerifiedDeleted == result.UniqueRemotePathCount &&
                              result.LocalDeepDeleteItemsCompleted == result.LocalDeepDeleteItemCount;
            result.Success = result.Executed && result.Errors.Count == 0 &&
                             result.LocalRootsDeleted == result.LocalRootsRequested;
            if (!result.Success && result.RemotePathsVerifiedDeleted == result.UniqueRemotePathCount)
                result.Warnings.Add(
                    "All cloud targets are verified deleted but one or more later local phases remain. Re-preview the remaining item(s) and retry; the transaction layers preserve MediaInfo until ItemRemoved and retain verified-delete retry state when needed.");
            return result;
        }

        private static IEnumerable<BaseItem> NormalizeRoots(IEnumerable<BaseItem> roots)
        {
            return (roots ?? Enumerable.Empty<BaseItem>())
                .Where(item => item != null && item.InternalId > 0)
                .GroupBy(item => item.InternalId)
                .Select(group => group.First());
        }

        private static IEnumerable<BaseItem> OrderRootsForDeletion(IEnumerable<BaseItem> roots)
        {
            return NormalizeRoots(roots).OrderBy(item => SafePathLength(item.Path));
        }

        private static int SafePathLength(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? int.MaxValue : path.Length;
        }

        private static string ComputePlanHash(IEnumerable<BaseItem> roots, RemoteDeepDeleteCascadePlan plan)
        {
            var remote = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            var local = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            var builder = new StringBuilder();
            builder.Append("v2|")
                .Append(remote.Enabled).Append('|')
                .Append(remote.Provider).Append('|')
                .Append(remote.BaseUrl ?? string.Empty).Append('|')
                .Append(remote.PathMappings ?? string.Empty).Append('|')
                .Append(remote.AllowedRemoteRoots ?? string.Empty).Append('|')
                .Append(remote.TreatNotFoundAsSuccess).Append('|')
                .Append(remote.DeleteAssociatedSidecars).Append('|')
                .Append(local?.EnableDeepDelete == true).Append('|')
                .Append(local?.DeepDeleteDryRun == true).Append('|')
                .Append(local?.DeepDeleteTargetFile == true).Append('|')
                .Append(local?.DeepDeleteAssociatedFiles == true).Append('|')
                .Append(local?.DeepDeleteEmptyDirectories == true).Append('|')
                .Append(local?.DeepDeleteAllowedRoots ?? string.Empty).Append('|');

            foreach (var root in NormalizeRoots(roots).OrderBy(item => item.InternalId))
                builder.Append("R:").Append(root.InternalId).Append(':').Append(NormalizePath(root.Path)).Append('|');
            foreach (var entry in (plan?.Entries ?? new List<RemoteDeepDeleteCascadeEntry>())
                         .OrderBy(entry => entry.ItemId, StringComparer.Ordinal)
                         .ThenBy(entry => entry.RemotePlan?.RemotePath, StringComparer.Ordinal))
            {
                builder.Append("E:").Append(entry.ItemId).Append(':')
                    .Append(NormalizePath(entry.ItemPath)).Append(':')
                    .Append(entry.LooksRemote).Append(':')
                    .Append(entry.RequiresRemoteDelete).Append(':')
                    .Append(entry.Allowed).Append(':')
                    .Append(entry.RemotePlan?.Provider ?? string.Empty).Append(':')
                    .Append(entry.RemotePlan?.RemotePath ?? string.Empty).Append(':')
                    .Append(entry.RequiresLocalDeepDelete).Append(':')
                    .Append(entry.LocalDeepDeleteAllowed).Append(':')
                    .Append(entry.LocalTargetResolutionFailed).Append('|');

                foreach (var localEntry in (entry.LocalPlan?.Entries ?? new List<DeepDeletePlanEntry>())
                             .OrderBy(value => value.Path, StringComparer.Ordinal)
                             .ThenBy(value => value.Kind))
                {
                    builder.Append("L:").Append(NormalizePath(localEntry.Path)).Append(':')
                        .Append(localEntry.Kind).Append(':')
                        .Append(localEntry.Allowed).Append('|');
                }
            }

            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
            var hex = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes) hex.Append(value.ToString("x2"));
            return hex.ToString();
        }

        private static bool FixedEquals(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right) || left.Length != right.Length) return false;
            var diff = 0;
            for (var i = 0; i < left.Length; i++) diff |= left[i] ^ right[i];
            return diff == 0;
        }

        private static string NormalizePath(string value)
        {
            return (value ?? string.Empty).Trim().Replace('\\', '/');
        }

        private static int NormalizePreviewLimit(int value)
        {
            return value <= 0 ? RemoteDeepDeleteCascadeService.DefaultMaxRemoteCandidates :
                Math.Min(RemoteDeepDeleteCascadeService.DefaultMaxRemoteCandidates, value);
        }

        private static IEnumerable<string> SplitIds(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
            return raw.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private BaseItem ResolveItem(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            if (long.TryParse(id, out var internalId))
            {
                try
                {
                    var byLong = _libraryManager.GetItemById(internalId);
                    if (byLong != null) return byLong;
                }
                catch { }
            }

            foreach (var method in _libraryManager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                         .Where(method => string.Equals(method.Name, "GetItemById", StringComparison.Ordinal) &&
                                          method.GetParameters().Length == 1))
            {
                try
                {
                    var parameterType = method.GetParameters()[0].ParameterType;
                    object argument = null;
                    if (parameterType == typeof(string)) argument = id;
                    else if (parameterType == typeof(Guid) && Guid.TryParse(id, out var guid)) argument = guid;
                    else continue;
                    if (method.Invoke(_libraryManager, new[] { argument }) is BaseItem item) return item;
                }
                catch { }
            }
            return null;
        }

        private static RemoteDeepDeleteCascadePreviewResponse PreviewError(string error)
        {
            return new RemoteDeepDeleteCascadePreviewResponse
            {
                Success = false,
                Plan = new RemoteDeepDeleteCascadePlan { Error = error },
                Errors = new List<string> { error }
            };
        }

        private static RemoteDeepDeleteCascadeApplyResponse ApplyError(string planHash, string error)
        {
            return AddError(new RemoteDeepDeleteCascadeApplyResponse { PlanHash = planHash }, error);
        }

        private static RemoteDeepDeleteCascadeApplyResponse AddError(RemoteDeepDeleteCascadeApplyResponse response,
            string error)
        {
            response.Success = false;
            if (!string.IsNullOrWhiteSpace(error)) response.Errors.Add(error);
            return response;
        }
    }
}
