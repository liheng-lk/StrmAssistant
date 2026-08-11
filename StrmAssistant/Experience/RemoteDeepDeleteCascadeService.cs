using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using StrmAssistant.MediaEnhance;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Experience
{
    public sealed class RemoteDeepDeleteCascadeEntry
    {
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string ItemPath { get; set; }
        public bool IsRootItem { get; set; }
        public bool IsDescendant { get; set; }
        public bool LooksRemote { get; set; }
        public bool RequiresRemoteDelete { get; set; }
        public bool Allowed { get; set; }
        public RemoteDeepDeletePlan RemotePlan { get; set; }
        public bool RequiresLocalDeepDelete { get; set; }
        public bool LocalDeepDeleteAllowed { get; set; }
        public bool LocalTargetResolutionFailed { get; set; }
        public DeepDeletePlan LocalPlan { get; set; }
        public string Error { get; set; }
    }

    public sealed class RemoteDeepDeleteCascadePlan
    {
        public bool Applicable { get; set; }
        public bool Allowed { get; set; }
        public bool EnumerationFailed { get; set; }
        public int RootItemCount { get; set; }
        public int EnumeratedItemCount { get; set; }
        public int RemoteCandidateCount { get; set; }
        public int UniqueRemotePathCount { get; set; }
        public int LocalOnlyCount { get; set; }
        public int LocalDeepDeleteItemCount { get; set; }
        public int LocalDeepDeleteEntryCount { get; set; }
        public int BlockedLocalEntryCount { get; set; }
        public int BlockedRemoteCount { get; set; }
        public int MaxRemoteCandidates { get; set; }
        public bool CandidateLimitExceeded { get; set; }
        public List<RemoteDeepDeleteCascadeEntry> Entries { get; set; } = new List<RemoteDeepDeleteCascadeEntry>();
        public List<string> EnumerationErrors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    /// <summary>
    /// Expands one or more Emby delete roots into media leaves before a destructive native delete is
    /// allowed to continue. Container descendant enumeration is fail-closed. For non-remote STRM leaves,
    /// the same local DeepDeleteTargetFile/AssociatedFiles rules are planned too, so a mixed parent tree
    /// cannot silently deep-delete its cloud targets while orphaning configured local targets.
    /// </summary>
    public sealed class RemoteDeepDeleteCascadeService
    {
        public const int DefaultMaxRemoteCandidates = 512;
        private readonly ILibraryManager _libraryManager;
        private readonly RemoteDeepDeleteService _remoteService = new RemoteDeepDeleteService();
        private readonly DeepDeleteService _localService = new DeepDeleteService();

        public RemoteDeepDeleteCascadeService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public RemoteDeepDeleteCascadePlan BuildPlan(IEnumerable<BaseItem> rootItems,
            int maxRemoteCandidates = DefaultMaxRemoteCandidates)
        {
            var result = new RemoteDeepDeleteCascadePlan
            {
                MaxRemoteCandidates = Math.Max(1, maxRemoteCandidates)
            };
            var runtime = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            var experience = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            if (!runtime.Enabled || runtime.Provider == RemoteDeepDeleteProviderType.None)
            {
                result.Error = "Remote Deep Delete is disabled or no remote provider is selected.";
                return result;
            }
            if (experience?.EnableDeepDelete != true)
            {
                result.Error = "Deep Delete is disabled in plugin options.";
                return result;
            }

            var roots = (rootItems ?? Enumerable.Empty<BaseItem>())
                .Where(item => item != null && item.InternalId > 0)
                .GroupBy(item => item.InternalId)
                .Select(group => group.First())
                .ToList();
            result.RootItemCount = roots.Count;
            if (roots.Count == 0)
            {
                result.Error = "No valid Emby root items were supplied for cascade planning.";
                return result;
            }

            var expanded = new Dictionary<long, Tuple<BaseItem, bool, bool>>();
            foreach (var root in roots)
            {
                expanded[root.InternalId] = Tuple.Create(root, true, false);
                if (!(root is Folder)) continue;

                if (!TryFetchMediaDescendants(root, out var descendants, out var enumerationError))
                {
                    result.EnumerationFailed = true;
                    var error = enumerationError ??
                                "Unknown descendant enumeration failure for root " + root.InternalId + ".";
                    result.EnumerationErrors.Add(error);
                    result.Entries.Add(new RemoteDeepDeleteCascadeEntry
                    {
                        ItemId = "enumeration:" + root.InternalId,
                        ItemName = root.Name,
                        ItemPath = root.Path,
                        IsRootItem = false,
                        IsDescendant = true,
                        LooksRemote = true,
                        RequiresRemoteDelete = false,
                        Allowed = false,
                        LocalDeepDeleteAllowed = false,
                        Error = error
                    });
                    continue;
                }

                foreach (var descendant in descendants)
                {
                    if (descendant == null || descendant.InternalId <= 0) continue;
                    if (!expanded.ContainsKey(descendant.InternalId))
                        expanded[descendant.InternalId] = Tuple.Create(descendant, false, true);
                }
            }

            result.EnumeratedItemCount = expanded.Count;
            foreach (var tuple in expanded.Values)
            {
                var item = tuple.Item1;
                var remotePlan = _remoteService.BuildPlan(item);
                var looksRemote = remotePlan?.TargetLooksRemote == true || LooksLikeRemoteTarget(remotePlan?.SourceTarget);
                var entry = new RemoteDeepDeleteCascadeEntry
                {
                    ItemId = item.InternalId.ToString(),
                    ItemName = item.Name,
                    ItemPath = item.Path,
                    IsRootItem = tuple.Item2,
                    IsDescendant = tuple.Item3,
                    LooksRemote = looksRemote,
                    RequiresRemoteDelete = remotePlan?.Applicable == true,
                    Allowed = remotePlan?.Applicable == true && remotePlan.Allowed,
                    RemotePlan = remotePlan,
                    LocalDeepDeleteAllowed = true
                };

                if (remotePlan?.Applicable == true)
                {
                    result.RemoteCandidateCount++;
                    if (!remotePlan.Allowed)
                    {
                        result.BlockedRemoteCount++;
                        entry.Error = remotePlan.Error ?? "Remote delete plan is not allowed.";
                    }
                }
                else if (looksRemote)
                {
                    result.BlockedRemoteCount++;
                    entry.Error = remotePlan?.Error ?? "Remote media target could not be mapped safely.";
                }
                else
                {
                    result.LocalOnlyCount++;
                    AttachLocalPlan(item, experience, entry, result);
                }
                result.Entries.Add(entry);
            }

            result.UniqueRemotePathCount = result.Entries
                .Where(entry => entry.RequiresRemoteDelete && entry.RemotePlan?.RemotePath != null)
                .Select(entry => entry.RemotePlan.RemotePath)
                .Distinct(StringComparer.Ordinal)
                .Count();
            result.Applicable = result.EnumerationFailed || result.RemoteCandidateCount > 0 ||
                                result.BlockedRemoteCount > 0 || result.LocalDeepDeleteItemCount > 0;
            result.CandidateLimitExceeded = result.RemoteCandidateCount > result.MaxRemoteCandidates;

            if (result.EnumerationFailed)
            {
                result.Error = "One or more container descendant queries failed. Destructive cascade execution is blocked because an enumeration failure cannot be treated as an empty folder.";
            }
            else if (result.CandidateLimitExceeded)
            {
                result.Error = "Cascade contains " + result.RemoteCandidateCount +
                               " remote media items, exceeding the safety limit of " +
                               result.MaxRemoteCandidates + ". Split the deletion into smaller groups.";
            }
            else if (result.BlockedRemoteCount > 0)
            {
                result.Error = result.BlockedRemoteCount +
                               " remote-looking media items failed mapping/allow-list preflight. No local deletion should continue.";
            }
            else if (result.BlockedLocalEntryCount > 0)
            {
                result.Error = result.BlockedLocalEntryCount +
                               " local deep-delete target/associated path(s) are unresolved or outside configured local allowed roots. No cascade deletion should continue.";
            }

            result.Allowed = result.Applicable && !result.EnumerationFailed && !result.CandidateLimitExceeded &&
                             result.BlockedRemoteCount == 0 && result.BlockedLocalEntryCount == 0 &&
                             result.Entries.Where(entry => entry.RequiresRemoteDelete).All(entry => entry.Allowed) &&
                             result.Entries.Where(entry => entry.RequiresLocalDeepDelete)
                                 .All(entry => entry.LocalDeepDeleteAllowed);

            var duplicateRemotePaths = result.Entries
                .Where(entry => entry.RequiresRemoteDelete && entry.RemotePlan?.RemotePath != null)
                .GroupBy(entry => entry.RemotePlan.RemotePath, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .ToList();
            if (duplicateRemotePaths.Count > 0)
                result.Warnings.Add(duplicateRemotePaths.Count +
                                    " remote paths are referenced by multiple Emby items; each unique cloud path is deleted only once, while every matching local item still waits for Emby's ItemRemoved confirmation.");

            if (!result.EnumerationFailed && result.RemoteCandidateCount == 0 && result.BlockedRemoteCount == 0)
                result.Warnings.Add("No remote STRM targets were found under the requested delete roots.");
            if (result.LocalDeepDeleteItemCount > 0)
                result.Warnings.Add(result.LocalDeepDeleteItemCount +
                                    " non-remote STRM/symlink item(s) also have configured local deep-delete work and are included in this cascade transaction.");
            return result;
        }

        private void AttachLocalPlan(BaseItem item, StrmAssistant.Options.ExperienceEnhanceOptions options,
            RemoteDeepDeleteCascadeEntry entry, RemoteDeepDeleteCascadePlan aggregate)
        {
            if (item == null || options == null || string.IsNullOrWhiteSpace(item.Path)) return;
            var candidate = item.IsShortcut ||
                            string.Equals(System.IO.Path.GetExtension(item.Path), ".strm",
                                StringComparison.OrdinalIgnoreCase);
            if (!candidate) return;

            var localPlan = _localService.BuildPlan(item.Path, options);
            entry.LocalPlan = localPlan;
            var configuredLocalWork = options.DeepDeleteTargetFile || options.DeepDeleteAssociatedFiles;
            if (!configuredLocalWork) return;

            if (options.DeepDeleteTargetFile && !localPlan.HasResolvedMediaTarget)
            {
                entry.RequiresLocalDeepDelete = true;
                entry.LocalDeepDeleteAllowed = false;
                entry.LocalTargetResolutionFailed = true;
                entry.Error = "A configured local STRM target could not be resolved for deep deletion.";
                aggregate.BlockedLocalEntryCount++;
                aggregate.LocalDeepDeleteItemCount++;
                return;
            }

            if (localPlan.Entries.Count == 0) return;
            entry.RequiresLocalDeepDelete = true;
            entry.LocalDeepDeleteAllowed = !localPlan.HasBlockedEntries;
            aggregate.LocalDeepDeleteItemCount++;
            aggregate.LocalDeepDeleteEntryCount += localPlan.Entries.Count;
            aggregate.BlockedLocalEntryCount += localPlan.Entries.Count(planEntry => !planEntry.Allowed);
            if (!entry.LocalDeepDeleteAllowed && string.IsNullOrWhiteSpace(entry.Error))
                entry.Error = "One or more local deep-delete paths are outside configured allowed roots.";
        }

        private bool TryFetchMediaDescendants(BaseItem root, out BaseItem[] descendants, out string error)
        {
            descendants = Array.Empty<BaseItem>();
            error = null;
            if (root == null || _libraryManager == null || root.InternalId <= 0)
            {
                error = "Invalid root or library manager while enumerating remote-delete descendants.";
                return false;
            }
            try
            {
                descendants = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    AncestorIds = new[] { root.InternalId },
                    HasPath = true,
                    MediaTypes = new[] { MediaType.Video, MediaType.Audio },
                    Recursive = true
                }) ?? Array.Empty<BaseItem>();
                return true;
            }
            catch (Exception ex)
            {
                error = "Root " + root.InternalId + " (" + root.Path + "): " + ex.GetBaseException().Message;
                Plugin.Instance?.Logger?.Warn("Remote Deep Delete cascade descendant enumeration failed: " + error);
                return false;
            }
        }

        private static bool LooksLikeRemoteTarget(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ||
                   string.Equals(uri.Scheme, "webdav", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(uri.Scheme, "webdavs", StringComparison.OrdinalIgnoreCase);
        }
    }
}
