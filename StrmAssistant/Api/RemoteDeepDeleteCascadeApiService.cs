using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Experience;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Api
{
    [Route("/StrmAssistant/DeepDelete/{Id}/CascadePlan", "GET",
        Summary = "Preview all remote STRM leaves that would be protected before deleting an item/folder")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetRemoteDeepDeleteCascadePlan : IReturn<RemoteDeepDeleteCascadePlan>
    {
        public string Id { get; set; }
        public int MaxRemoteCandidates { get; set; } = RemoteDeepDeleteCascadeService.DefaultMaxRemoteCandidates;
    }

    [Route("/StrmAssistant/DeepDelete/CascadePlan", "GET",
        Summary = "Preview all remote STRM leaves that would be protected before a batch item deletion")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetRemoteDeepDeleteBatchCascadePlan : IReturn<RemoteDeepDeleteCascadePlan>
    {
        public string Ids { get; set; }
        public int MaxRemoteCandidates { get; set; } = RemoteDeepDeleteCascadeService.DefaultMaxRemoteCandidates;
    }

    public sealed class RemoteDeepDeleteCascadeApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly RemoteDeepDeleteCascadeService _cascade;

        public RemoteDeepDeleteCascadeApiService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
            _cascade = new RemoteDeepDeleteCascadeService(libraryManager);
        }

        public object Get(GetRemoteDeepDeleteCascadePlan request)
        {
            var item = ResolveItem(request?.Id);
            if (item == null)
                return new RemoteDeepDeleteCascadePlan { Error = "Item was not found or id is invalid." };
            return _cascade.BuildPlan(new[] { item }, NormalizeLimit(request?.MaxRemoteCandidates ?? 0));
        }

        public object Get(GetRemoteDeepDeleteBatchCascadePlan request)
        {
            var ids = SplitIds(request?.Ids).ToArray();
            if (ids.Length == 0)
                return new RemoteDeepDeleteCascadePlan { Error = "Ids is empty." };
            var items = ids.Select(ResolveItem).Where(item => item != null).ToArray();
            if (items.Length == 0)
                return new RemoteDeepDeleteCascadePlan { Error = "No requested Emby items could be resolved." };
            var plan = _cascade.BuildPlan(items, NormalizeLimit(request?.MaxRemoteCandidates ?? 0));
            if (items.Length != ids.Length)
                plan.Warnings.Add((ids.Length - items.Length) + " requested item ids could not be resolved and were not included in the preview.");
            return plan;
        }

        private static int NormalizeLimit(int value)
        {
            return value <= 0 ? RemoteDeepDeleteCascadeService.DefaultMaxRemoteCandidates : Math.Min(4096, value);
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
    }
}
