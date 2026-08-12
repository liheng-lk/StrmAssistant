using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Options;
using System;
using System.Reflection;
using System.Threading.Tasks;

namespace StrmAssistant.UI
{
    internal sealed class NativeSettingsPageView<T> : NativeSettingsViewBase, IPluginPageView where T : class, IEditableObject
    {
        private readonly Plugin _plugin;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly string _sectionKey;
        private readonly Func<PluginOptions, T> _selector;

        public NativeSettingsPageView(string pluginId, Plugin plugin, IJsonSerializer jsonSerializer,
            string sectionKey, Func<PluginOptions, T> selector)
            : base(pluginId)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
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
            var previous = GetSection(master);

            try
            {
                SetSection(master, ContentData);

                if (_sectionKey == NativeSettingsSections.Experience &&
                    master.ExperienceEnhanceOptions != null)
                {
                    // Saving this specific native tab is an explicit choice for remote-delete UI fields.
                    master.ExperienceEnhanceOptions.RemoteDeepDeleteUiAuthoritative = true;
                }

                var validationErrors = master.GetCrossValidationErrors();
                if (validationErrors.Count > 0)
                    throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));

                _plugin.SavePluginOptionsSuppress();
            }
            catch
            {
                // The editor works on a deep clone, so restoring this reference fully reverts a rejected save.
                SetSection(master, previous);
                throw;
            }

            Reload();
            return Task.FromResult<IPluginUIView>(this);
        }

        private IEditableObject GetSection(PluginOptions master)
        {
            switch (_sectionKey)
            {
                case NativeSettingsSections.General: return master.GeneralOptions;
                case NativeSettingsSections.Media: return master.MediaInfoExtractOptions;
                case NativeSettingsSections.Metadata: return master.MetadataEnhanceOptions;
                case NativeSettingsSections.Intro: return master.IntroSkipOptions;
                case NativeSettingsSections.Experience: return master.ExperienceEnhanceOptions;
                case NativeSettingsSections.About: return master.AboutOptions;
                default: throw new InvalidOperationException("Unknown native settings section: " + _sectionKey);
            }
        }

        private void SetSection(PluginOptions master, IEditableObject value)
        {
            switch (_sectionKey)
            {
                case NativeSettingsSections.General:
                    master.GeneralOptions = (GeneralOptions)value;
                    break;
                case NativeSettingsSections.Media:
                    master.MediaInfoExtractOptions = (MediaInfoExtractOptions)value;
                    break;
                case NativeSettingsSections.Metadata:
                    master.MetadataEnhanceOptions = (MetadataEnhanceOptions)value;
                    break;
                case NativeSettingsSections.Intro:
                    master.IntroSkipOptions = (IntroSkipOptions)value;
                    break;
                case NativeSettingsSections.Experience:
                    master.ExperienceEnhanceOptions = (ExperienceEnhanceOptions)value;
                    break;
                case NativeSettingsSections.About:
                    master.AboutOptions = (AboutOptions)value;
                    break;
                default:
                    throw new InvalidOperationException("Unknown native settings section: " + _sectionKey);
            }
        }

        private void Reload()
        {
            var master = PrepareMasterOptions();
            var section = _selector(master);
            var json = _jsonSerializer.SerializeToString(section);
            ContentData = _jsonSerializer.DeserializeFromString<T>(json);
            if (ContentData == null)
                throw new InvalidOperationException("Failed to clone native settings section: " + _sectionKey);
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
