using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/{Web}/components/strmassistant/settings-tabs.js", "GET", IsHidden = true)]
    [Route("/{Web}/components/strmassistant/settings-tabs-v{Version}.js", "GET", IsHidden = true)]
    [Unauthenticated]
    public sealed class GetSettingsTabsJs
    {
        public string Web { get; set; }

        /// <summary>
        /// Optional cache-busting plugin version used by the versioned route.
        /// The resource content is identical to the legacy route; the unique URL
        /// prevents Emby Web/browser module caches from keeping an older tabs module
        /// after the plugin DLL is upgraded without an Emby Web version change.
        /// </summary>
        public string Version { get; set; }
    }
}
