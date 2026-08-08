using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/StrmAssistant/Library/Collections/Delete", "POST",
        Summary = "Remove the Emby collections virtual folder and keep it hidden")]
    [Authenticated(Roles = "Admin")]
    public sealed class RemoveCollectionsVirtualFolder : IReturnVoid, IReturn
    {
        public string Id { get; set; }
    }
}
