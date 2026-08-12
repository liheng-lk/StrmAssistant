using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Options;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace StrmAssistant.UI
{
    internal static class NativeSettingsSections
    {
        public const string General = "general";
        public const string Media = "media";
        public const string Metadata = "metadata";
        public const string Intro = "intro";
        public const string Experience = "experience";
        public const string About = "about";
    }

    internal sealed class NativeSettingsMainController : NativeSettingsControllerBase, IHasTabbedUIPages
    {
        private readonly Plugin _plugin;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly List<IPluginUIPageController> _tabs = new List<IPluginUIPageController>();

        public NativeSettingsMainController(PluginInfo pluginInfo, Plugin plugin, IJsonSerializer jsonSerializer)
            : base(pluginInfo.Id)
        {
            _plugin = plugin;
            _jsonSerializer = jsonSerializer;
            PageInfo = new PluginPageInfo
            {
                Name = "StrmAssistantSettings",
                EnableInMainMenu = true,
                DisplayName = "Strm Assistant",
                MenuIcon = "video_settings",
                // Emby's native tab host treats the controller's default view as the first tab.
                // The upstream StrmAssistant tab implementation and Emby SDK demo both keep
                // a tabbed main page out of the special single-page main-config route.
                IsMainConfigPage = false,
            };

            // The default page returned by CreateDefaultPageView is the first visible tab (General).
            // Only the remaining five pages belong in TabPageControllers.
            _tabs.Add(new NativeSettingsTabPageController(pluginInfo.Id, "StrmAssistantMediaInfo", "媒体信息",
                () => CreateMediaView(pluginInfo.Id)));
            _tabs.Add(new NativeSettingsTabPageController(pluginInfo.Id, "StrmAssistantMetadata", "元数据",
                () => CreateMetadataView(pluginInfo.Id)));
            _tabs.Add(new NativeSettingsTabPageController(pluginInfo.Id, "StrmAssistantIntroCredits", "片头片尾",
                () => CreateIntroView(pluginInfo.Id)));
            _tabs.Add(new NativeSettingsTabPageController(pluginInfo.Id, "StrmAssistantExperience", "体验增强",
                () => CreateExperienceView(pluginInfo.Id)));
            _tabs.Add(new NativeSettingsTabPageController(pluginInfo.Id, "StrmAssistantAbout", "关于",
                () => CreateAboutView(pluginInfo.Id)));
        }

        public override PluginPageInfo PageInfo { get; }

        public IReadOnlyList<IPluginUIPageController> TabPageControllers => _tabs.AsReadOnly();

        public string DefaultTabDisplayName => "常规";

        public int VisibleTabCount => 1 + _tabs.Count;

        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            return Task.FromResult<IPluginUIView>(CreateGeneralView(PluginId));
        }

        private IPluginUIView CreateGeneralView(string pluginId)
        {
            return new NativeSettingsPageView<GeneralOptions>(pluginId, _plugin, _jsonSerializer,
                NativeSettingsSections.General, options => options.GeneralOptions);
        }

        private IPluginUIView CreateMediaView(string pluginId)
        {
            return new NativeSettingsPageView<MediaInfoExtractOptions>(pluginId, _plugin, _jsonSerializer,
                NativeSettingsSections.Media, options => options.MediaInfoExtractOptions);
        }

        private IPluginUIView CreateMetadataView(string pluginId)
        {
            return new NativeSettingsPageView<MetadataEnhanceOptions>(pluginId, _plugin, _jsonSerializer,
                NativeSettingsSections.Metadata, options => options.MetadataEnhanceOptions);
        }

        private IPluginUIView CreateIntroView(string pluginId)
        {
            return new NativeSettingsPageView<IntroSkipOptions>(pluginId, _plugin, _jsonSerializer,
                NativeSettingsSections.Intro, options => options.IntroSkipOptions);
        }

        private IPluginUIView CreateExperienceView(string pluginId)
        {
            return new NativeSettingsPageView<ExperienceEnhanceOptions>(pluginId, _plugin, _jsonSerializer,
                NativeSettingsSections.Experience, options => options.ExperienceEnhanceOptions);
        }

        private IPluginUIView CreateAboutView(string pluginId)
        {
            return new NativeSettingsPageView<AboutOptions>(pluginId, _plugin, _jsonSerializer,
                NativeSettingsSections.About, options => options.AboutOptions);
        }
    }
}
