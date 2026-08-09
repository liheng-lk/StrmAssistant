using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using System;
using System.Collections.Generic;

namespace StrmAssistant.Api
{
    public sealed class ProxyRouteDiagnosticResult
    {
        public bool Success { get; set; }
        public bool Enabled { get; set; }
        public string Mode { get; set; }
        public string Destination { get; set; }
        public string DestinationHost { get; set; }
        public bool WouldUseProxy { get; set; }
        public bool WouldBypassProxy { get; set; }
        public string ProxyEndpoint { get; set; }
        public bool HttpHandlerPatchActive { get; set; }
        public string HttpHandlerTarget { get; set; }
        public string Error { get; set; }
        public List<string> Notes { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/Diagnostics/ProxyRoute", "GET",
        Summary = "Evaluate whether one URL would use the configured Strm Assistant proxy")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetProxyRouteDiagnostic : IReturn<ProxyRouteDiagnosticResult>
    {
        public string Url { get; set; }
    }

    /// <summary>
    /// Pure routing evaluation. No outbound network request is made.
    /// </summary>
    public sealed class ProxyDiagnosticsApiService : BaseApiService
    {
        public object Get(GetProxyRouteDiagnostic request)
        {
            var options = Plugin.Instance?.GetPluginOptions()?.GeneralOptions;
            var status = RuntimeModState.Status;
            var result = new ProxyRouteDiagnosticResult
            {
                Enabled = options?.EnableProxyServerEnhance == true,
                Mode = options?.ProxyMode.ToString(),
                Destination = request?.Url,
                HttpHandlerPatchActive = status?.HttpHandlerPatched == true,
                HttpHandlerTarget = status?.HttpHandlerTarget
            };

            if (string.IsNullOrWhiteSpace(request?.Url) ||
                !Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var destination))
            {
                result.Error = "Url must be a valid absolute URI.";
                return result;
            }

            result.DestinationHost = destination.DnsSafeHost;
            if (!result.Enabled)
            {
                result.Success = true;
                result.WouldBypassProxy = true;
                result.Notes.Add("Proxy enhancement is disabled, so Emby's original handler behavior is preserved.");
                return result;
            }

            if (string.IsNullOrWhiteSpace(options.ProxyServerUrl) ||
                !Uri.TryCreate(options.ProxyServerUrl.Trim(), UriKind.Absolute, out var proxyUri))
            {
                result.Error = "Proxy enhancement is enabled but ProxyServerUrl is invalid.";
                return result;
            }

            if (proxyUri.Scheme != Uri.UriSchemeHttp && proxyUri.Scheme != Uri.UriSchemeHttps)
            {
                result.Error = "Only HTTP/HTTPS proxy URLs are accepted by this routing layer.";
                return result;
            }

            try
            {
                var proxy = new SelectiveWebProxy(proxyUri, options.ProxyMode,
                    options.ProxyWhitelistDomains, options.ProxyBypassHosts,
                    options.ProxyLocalDiscoveryAddress);

                result.WouldBypassProxy = proxy.IsBypassed(destination);
                result.WouldUseProxy = !result.WouldBypassProxy;
                result.ProxyEndpoint = SanitizeProxyUri(proxyUri);
                result.Success = true;

                if (!result.HttpHandlerPatchActive)
                    result.Notes.Add("Routing rules are valid, but the runtime CreateHttpClientHandler patch is not active on this Emby build.");
                if (result.WouldBypassProxy)
                    result.Notes.Add("The destination matched local/private/bypass rules or did not match the whitelist.");
                else
                    result.Notes.Add("The destination would be routed through the configured proxy by new Emby HTTP handlers.");
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        private static string SanitizeProxyUri(Uri uri)
        {
            var builder = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty
            };
            return builder.Uri.GetLeftPart(UriPartial.Authority);
        }
    }
}
