using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.Plugins.UI.Views;
using StrmAssistant.Options;
using System;
using System.Linq;
using System.Reflection;
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
            var master = _plugin.GetPluginOptions();
            switch (_sectionKey)
            {
                case NativeSettingsSections.General:
                    master.GeneralOptions = (GeneralOptions)ContentData;
                    break;
                case NativeSettingsSections.Media:
                    master.MediaInfoExtractOptions = (MediaInfoExtractOptions)ContentData;
                    break;
                case NativeSettingsSections.Metadata:
                    master.MetadataEnhanceOptions = (MetadataEnhanceOptions)ContentData;
                    break;
                case NativeSettingsSections.Intro:
                    master.IntroSkipOptions = (IntroSkipOptions)ContentData;
                    break;
                case NativeSettingsSections.Experience:
                    master.ExperienceEnhanceOptions = (ExperienceEnhanceOptions)ContentData;
                    break;
                case NativeSettingsSections.About:
                    master.AboutOptions = (AboutOptions)ContentData;
                    break;
                default:
                    throw new InvalidOperationException("Unknown native settings section: " + _sectionKey);
            }

            var validationErrors = master.GetCrossValidationErrors();
            if (validationErrors.Count > 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));

            _plugin.SavePluginOptionsSuppress();
            Reload();
            return Task.FromResult<IPluginUIView>(this);
        }

        private void Reload()
        {
            var master = PrepareMasterOptions();
            ContentData = _selector(master);
        }

        private PluginOptions PrepareMasterOptions()
        {
            var options = _plugin.GetPluginOptions();
            var method = typeof(Plugin).GetMethod("OnBeforeShowUI", BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) return options;

            try
            {
                var prepared = method.Invoke(_plugin, new object[] { options }) as PluginOptions;
                return prepared ?? options;
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }
    }
}
