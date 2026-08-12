using System;
using System.IO;
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

        // Kept only so browsers with a cached pre-2.0.9 shortcuts module receive the retired/no-op module
        // instead of a 404. New builds no longer append the DOM settings-tabs loader at all.
        public object Get(GetSettingsTabsJs request)
        {
            return _resultFactory.GetResult(Request,
                (ReadOnlyMemory<byte>)SettingsTabsJs.Value, "application/x-javascript");
        }

        public object Get(GetShortcutMenu request)
        {
            var javascript = ShortcutMenuHelper.ModifiedShortcutsString + SeriesCollectionsLoader;
            return _resultFactory.GetResult(javascript.AsSpan(), "application/x-javascript");
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
