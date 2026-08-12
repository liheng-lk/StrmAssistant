using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Plugins.UI.Views.Enums;
using System;
using System.Threading.Tasks;

namespace StrmAssistant.UI
{
    internal abstract class NativeSettingsViewBase : IPluginUIView, IPluginViewWithOptions
    {
        protected NativeSettingsViewBase(string pluginId)
        {
            PluginId = pluginId;
        }

        public event EventHandler<GenericEventArgs<IPluginUIView>> UIViewInfoChanged;

        public virtual string Caption => ContentData?.EditorTitle;

        public virtual string SubCaption => ContentData?.EditorDescription;

        public string PluginId { get; }

        public IEditableObject ContentData { get; set; }

        public UserDto User { get; set; }

        public string RedirectViewUrl { get; set; }

        public Uri HelpUrl { get; set; }

        public QueryCloseAction QueryCloseAction { get; set; }

        public WizardHidingBehavior WizardHidingBehavior { get; set; }

        public CompactViewAppearance CompactViewAppearance { get; set; }

        public DialogSize DialogSize { get; set; }

        public virtual bool IsCommandAllowed(string commandKey)
        {
            return true;
        }

        public virtual Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            return Task.FromResult<IPluginUIView>(null);
        }

        public virtual Task Cancel()
        {
            return Task.CompletedTask;
        }

        public virtual void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data)
        {
        }

        protected void RaiseUIViewInfoChanged()
        {
            UIViewInfoChanged?.Invoke(this, new GenericEventArgs<IPluginUIView>(this));
        }

        // Keep this deliberately empty. Dialog decoration properties evolved across Emby 4.8/4.9/4.10;
        // the settings tabs only require the common IPluginUIView/IPluginPageView contract.
        public virtual PluginViewOptions ViewOptions => new PluginViewOptions();
    }
}
