using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/{Web}/components/strmassistant/settings-tabs.js", "GET", IsHidden = true)]
    [Unauthenticated]
    public sealed class GetSettingsTabsJs
    {
        public string Web { get; set; }
    }
}
