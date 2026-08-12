using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.UI
{
    internal abstract class NativeSettingsControllerBase : IPluginUIPageController
    {
        protected NativeSettingsControllerBase(string pluginId)
        {
            PluginId = pluginId;
        }

        public abstract PluginPageInfo PageInfo { get; }

        public string PluginId { get; }

        public virtual Task Initialize(CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public abstract Task<IPluginUIView> CreateDefaultPageView();
    }
}
