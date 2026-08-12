using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using System;
using System.Threading.Tasks;

namespace StrmAssistant.UI
{
    internal sealed class NativeSettingsTabPageController : NativeSettingsControllerBase
    {
        private readonly Func<IPluginUIView> _factory;

        public NativeSettingsTabPageController(string pluginId, string name, string displayName, Func<IPluginUIView> factory)
            : base(pluginId)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            PageInfo = new PluginPageInfo
            {
                Name = name,
                DisplayName = displayName,
            };
        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            return Task.FromResult(_factory());
        }
    }
}
