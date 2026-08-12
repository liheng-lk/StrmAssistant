using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Web.Api;
using StrmAssistant.Web.Helper;

namespace StrmAssistant.Web.Service
{
    [Unauthenticated]
    public class ShortcutMenuService : IService, IRequiresRequest
    {
        private const string SeriesCollectionsLoader = @"
setTimeout(() => {
    try {
        require(['components/strmassistant/seriescollections']).then((responses) => {
            const module = responses && responses[0];
            if (module && typeof module.init === 'function') module.init();
        });
    } catch (_) {}
}, 3200);
";

        private const string SettingsTabsLoaderTemplate = @"
(() => {
    const moduleName = '__MODULE__';
    const maxAttempts = 40;
    let attempt = 0;

    const retry = () => {
        if (attempt >= maxAttempts) return;
        window.setTimeout(load, 500);
    };

    const load = () => {
        attempt += 1;
        try {
            const pending = require([moduleName]);
            if (!pending || typeof pending.then !== 'function') {
                retry();
                return;
            }

            pending.then((responses) => {
                const module = responses && responses[0];
                if (module && typeof module.init === 'function') {
                    module.init();
                    window.__strmAssistantSettingsTabsModule = moduleName;
                    window.__strmAssistantSettingsTabsLoaded = true;
                    return;
                }
                retry();
            }).catch(() => retry());
        } catch (_) {
            retry();
        }
    };

    window.setTimeout(load, 250);
})();
";

        private readonly IHttpResultFactory _resultFactory;
        private static readonly Lazy<byte[]> SeriesCollectionsJs =
            new Lazy<byte[]>(() => ReadEmbeddedResource("seriescollections.js"), true);
        private static readonly Lazy<byte[]> SettingsTabsJs =
            new Lazy<byte[]>(() => ReadEmbeddedResource("settings-tabs.js"), true);

        public ShortcutMenuService(IHttpResultFactory resultFactory)
        {
            _resultFactory = resultFactory;
        }

        public IRequest Request { get; set; }

        public object Get(GetStrmAssistantJs request)
        {
            return _resultFactory.GetResult(Request,
                (ReadOnlyMemory<byte>)ShortcutMenuHelper.StrmAssistantJs.GetBuffer(), "application/x-javascript");
        }

        public object Get(GetSeriesCollectionsJs request)
        {
            return _resultFactory.GetResult(Request,
                (ReadOnlyMemory<byte>)SeriesCollectionsJs.Value, "application/x-javascript");
        }

        public object Get(GetSettingsTabsJs request)
        {
            return _resultFactory.GetResult(Request,
                (ReadOnlyMemory<byte>)SettingsTabsJs.Value, "application/x-javascript");
        }

        public object Get(GetShortcutMenu request)
        {
            var version = Plugin.Instance?.CurrentVersion ?? "0.0.0.0";
            var javascript = ShortcutMenuHelper.ModifiedShortcutsString
                             + SeriesCollectionsLoader
                             + BuildSettingsTabsLoader(version);
            return _resultFactory.GetResult(javascript.AsSpan(), "application/x-javascript");
        }

        internal static string BuildSettingsTabsModuleName(string version)
        {
            var safeVersion = new string((version ?? string.Empty)
                .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
                .ToArray());
            if (string.IsNullOrWhiteSpace(safeVersion)) safeVersion = "unknown";
            return "components/strmassistant/settings-tabs-v" + safeVersion;
        }

        internal static string BuildSettingsTabsLoader(string version)
        {
            return SettingsTabsLoaderTemplate.Replace("__MODULE__", BuildSettingsTabsModuleName(version));
        }

        private static byte[] ReadEmbeddedResource(string resourceName)
        {
            var name = typeof(Plugin).Namespace + ".Web.Resources." + resourceName;
            using var stream = typeof(ShortcutMenuService).GetTypeInfo().Assembly.GetManifestResourceStream(name);
            if (stream == null) return Array.Empty<byte>();
            using var destination = new MemoryStream();
            stream.CopyTo(destination);
            return destination.ToArray();
        }
    }
}
