using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.Plugins.UI.Views;
using System;
using System.Threading.Tasks;

namespace StrmAssistant.UI
{
    internal sealed class NativeSettingsPageView<T> : NativeSettingsViewBase, IPluginPageView where T : class, IEditableObject
    {
        private readonly Plugin _plugin;
        private readonly string _sectionKey;
        private readonly Func<PluginOptions, T> _selector;

        public NativeSettingsPageView(string pluginId, Plugin plugin, string sectionKey, Func<PluginOptions, T> selector)
            : base(pluginId)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _sectionKey = sectionKey ?? throw new ArgumentNullException(nameof(sectionKey));
            _selector = selector ?? throw new ArgumentNullException(nameof(selector));
            Reload();
        }

        public bool ShowSave { get; set; } = true;

        public bool ShowBack { get; set; } = false;

        public bool AllowSave { get; set; } = true;

        public bool AllowBack { get; set; } = true;

        public Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            _plugin.SaveNativeUiSection(_sectionKey, ContentData);
            Reload();
            return Task.FromResult<IPluginUIView>(this);
        }

        private void Reload()
        {
            var master = _plugin.GetPreparedPluginOptionsForNativeUi();
            ContentData = _selector(master);
        }
    }
}
