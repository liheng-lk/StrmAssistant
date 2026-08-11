using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Experience;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    public sealed class RemoteDeepDeleteProbeResponse
    {
        public bool Success { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string SourcePath { get; set; }
        public RemoteDeepDeletePlan Plan { get; set; }
        public RemoteDeepDeleteProbeResult Probe { get; set; }
        public string Error { get; set; }
    }

    [Route("/StrmAssistant/DeepDelete/{Id}/RemoteProbe", "GET",
        Summary = "Resolve and verify a remote deep-delete target without deleting anything")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetRemoteDeepDeleteProbe : IReturn<RemoteDeepDeleteProbeResponse>
    {
        public string Id { get; set; }
    }

    public sealed class RemoteDeepDeleteProbeApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly RemoteDeepDeleteService _service = new RemoteDeepDeleteService();

        public RemoteDeepDeleteProbeApiService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public async Task<object> Get(GetRemoteDeepDeleteProbe request)
        {
            var item = ResolveItem(request?.Id);
            if (item == null)
                return new RemoteDeepDeleteProbeResponse
                {
                    Success = false,
                    ItemId = request?.Id,
                    Error = "Media item was not found."
                };

            var plan = _service.BuildPlan(item);
            var response = new RemoteDeepDeleteProbeResponse
            {
                ItemId = item.InternalId.ToString(),
                ItemName = item.Name,
                SourcePath = item.Path,
                Plan = plan
            };

            if (!plan.Applicable)
            {
                response.Error = plan.Error ?? "The item does not resolve to a configured remote target.";
                return response;
            }
            if (!plan.Allowed)
            {
                response.Error = plan.Error ?? "The remote target is outside the configured deletion safety boundary.";
                return response;
            }

            var probe = await _service.ProbeAsync(plan, CancellationToken.None).ConfigureAwait(false);
            response.Probe = probe;
            response.Success = probe?.Success == true;
            response.Error = response.Success ? null : probe?.Error;
            return response;
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
