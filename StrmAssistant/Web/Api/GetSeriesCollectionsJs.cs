using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/{Web}/components/strmassistant/seriescollections.js", "GET", IsHidden = true)]
    [Unauthenticated]
    public sealed class GetSeriesCollectionsJs
    {
        public string Web { get; set; }
    }
}
