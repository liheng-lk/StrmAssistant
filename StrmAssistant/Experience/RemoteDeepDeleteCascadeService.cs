using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.IO;
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
        public string Error { get; set; }
    }

    public sealed class RemoteDeepDeleteCascadePlan
    {
        public bool Applicable { get; set; }
        public bool Allowed { get; set; }
        public int RootItemCount { get; set; }
        public int EnumeratedItemCount { get; set; }
        public int RemoteCandidateCount { get; set; }
        public int UniqueRemotePathCount { get; set; }
        public int LocalOnlyCount { get; set; }
        public int BlockedRemoteCount { get; set; }
        public int MaxRemoteCandidates { get; set; }
        public bool CandidateLimitExceeded { get; set; }
        public List<RemoteDeepDeleteCascadeEntry> Entries { get; set; } = new List<RemoteDeepDeleteCascadeEntry>();
        public List<string> Warnings { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    /// <summary>
    /// Expands one or more Emby delete roots into media leaves before a destructive native delete is
    /// allowed to continue. This closes the folder/Series/Season and DELETE /Items?Ids=... gaps where
    /// Emby can remove a local STRM tree while a direct-item-only remote bridge never sees the cloud
    /// targets below it.
    /// </summary>
    public sealed class RemoteDeepDeleteCascadeService
    {
        public const int DefaultMaxRemoteCandidates = 512;
        private readonly ILibraryManager _libraryManager;
        private readonly RemoteDeepDeleteService _remoteService = new RemoteDeepDeleteService();

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
                foreach (var descendant in FetchMediaDescendants(root))
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
                    RemotePlan = remotePlan
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
                }
                result.Entries.Add(entry);
            }

            result.UniqueRemotePathCount = result.Entries
                .Where(entry => entry.RequiresRemoteDelete && entry.RemotePlan?.RemotePath != null)
                .Select(entry => entry.RemotePlan.RemotePath)
                .Distinct(StringComparer.Ordinal)
                .Count();
            result.Applicable = result.RemoteCandidateCount > 0 || result.BlockedRemoteCount > 0;
            result.CandidateLimitExceeded = result.RemoteCandidateCount > result.MaxRemoteCandidates;

            if (result.CandidateLimitExceeded)
            {
                result.Error = "Cascade contains " + result.RemoteCandidateCount +
                               " remote media items, exceeding the safety limit of " +
                               result.MaxRemoteCandidates + ". Split the deletion into smaller groups.";
            }
            else if (result.BlockedRemoteCount > 0)
            {
                result.Error = result.BlockedRemoteCount +
                               " remote-looking media items failed mapping/allow-list preflight. No native local deletion should continue.";
            }

            result.Allowed = result.Applicable && !result.CandidateLimitExceeded &&
                             result.BlockedRemoteCount == 0 &&
                             result.Entries.Where(entry => entry.RequiresRemoteDelete).All(entry => entry.Allowed);

            var duplicateRemotePaths = result.Entries
                .Where(entry => entry.RequiresRemoteDelete && entry.RemotePlan?.RemotePath != null)
                .GroupBy(entry => entry.RemotePlan.RemotePath, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .ToList();
            if (duplicateRemotePaths.Count > 0)
                result.Warnings.Add(duplicateRemotePaths.Count +
                                    " remote paths are referenced by multiple Emby items; each unique cloud path must be deleted only once, while every matching local item still waits for Emby's ItemRemoved confirmation.");

            if (result.RemoteCandidateCount == 0 && result.BlockedRemoteCount == 0)
                result.Warnings.Add("No remote STRM targets were found under the requested delete roots.");
            return result;
        }

        private IEnumerable<BaseItem> FetchMediaDescendants(BaseItem root)
        {
            if (root == null || _libraryManager == null || root.InternalId <= 0)
                return Array.Empty<BaseItem>();
            try
            {
                return _libraryManager.GetItemList(new InternalItemsQuery
                {
                    AncestorIds = new[] { root.InternalId },
                    HasPath = true,
                    MediaTypes = new[] { MediaType.Video, MediaType.Audio },
                    Recursive = true
                }) ?? Array.Empty<BaseItem>();
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Remote Deep Delete cascade descendant enumeration failed for {0}: {1}",
                    root.Path, ex.Message);
                return Array.Empty<BaseItem>();
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
