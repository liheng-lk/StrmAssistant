using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using StrmAssistant.Experience;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    public sealed class OpenListRemoteSidecarPreviewResponse
    {
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string SourcePath { get; set; }
        public RemoteDeepDeletePlan MainPlan { get; set; }
        public OpenListRemoteSidecarPlan Sidecars { get; set; }
        public OpenListRemoteSidecarDeleteStatus Runtime { get; set; }
    }

    [Route("/StrmAssistant/DeepDelete/{Id}/RemoteSidecars", "GET",
        Summary = "Preview conservative same-stem OpenList sidecars without deleting anything")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetOpenListRemoteSidecarPreview : IReturn<OpenListRemoteSidecarPreviewResponse>
    {
        public string Id { get; set; }
    }

    public sealed class OpenListRemoteSidecarApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly RemoteDeepDeleteService _remote = new RemoteDeepDeleteService();
        private readonly OpenListRemoteSidecarService _sidecars = new OpenListRemoteSidecarService();

        public OpenListRemoteSidecarApiService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public async Task<object> Get(GetOpenListRemoteSidecarPreview request)
        {
            var item = ResolveItem(request?.Id);
            if (item == null) throw new ArgumentException("Media item was not found: " + request?.Id);
            var mainPlan = _remote.BuildPlan(item);
            var sidecars = await _sidecars.PlanAsync(mainPlan, CancellationToken.None).ConfigureAwait(false);
            return new OpenListRemoteSidecarPreviewResponse
            {
                ItemId = item.InternalId.ToString(),
                ItemName = item.Name,
                SourcePath = item.Path,
                MainPlan = mainPlan,
                Sidecars = sidecars,
                Runtime = OpenListRemoteSidecarDeleteState.Status
            };
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
