using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.Collections.Generic;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    public class IntroSkipOptions : EditableOptionsBase
    {
        [DisplayNameL("PluginOptions_IntroSkipOptions_Intro_Credits_Detection", typeof(Resources))]
        public override string EditorTitle => Resources.PluginOptions_IntroSkipOptions_Intro_Credits_Detection;
        
        [DisplayName("原生片头探测增强（可选）")]
        [Description("可选增强项，用于扩展 Emby 原生片头探测能力；它不是片头片尾检测的总开关。默认关闭。")]
        [Required]
        public bool UnlockIntroSkip { get; set; } = false;

        [DisplayNameL("IntroSkipOptions_IntroDetectionFingerprintMinutes_Intro_Detection_Fingerprint_Minutes", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_IntroDetectionFingerprintMinutes_It_must_be_between_2_and_20__Default_is_10_", typeof(Resources))]
        [MinValue(2), MaxValue(20)]
        [Required]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        public int IntroDetectionFingerprintMinutes { get; set; } = 10;

        [DisplayName("按媒体库覆盖声纹长度")]
        [Description("可选，每行一条：媒体库名称或内部 ID = 分钟。例如 动画 = 6、电视剧 = 10。范围 2–20 分钟；未匹配的媒体库继续使用上面的全局值。")]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        public string FingerprintDurationOverrides { get; set; } = string.Empty;

        [DisplayName("片头声纹使用分布式 ffmpeg（实验）")]
        [Description("默认关闭。开启后只为本插件创建的 AudioFingerprintManager 注入分布式 ffmpeg/rffmpeg 路径；不会修改 Emby 全局转码器。季级声纹匹配和片头标记仍使用 Emby 原生逻辑。")]
        [Required]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        public bool EnableDistributedFingerprintRouting { get; set; } = false;

        [DisplayName("分布式声纹失败时回退 Emby 原生")]
        [Description("建议保持开启。远端 worker、Chromaprint 或共享路径执行发生异常时，重新使用 Emby 原生 ffmpeg 完成当前声纹流程。")]
        [Required]
        [VisibleCondition(nameof(EnableDistributedFingerprintRouting), SimpleCondition.IsTrue)]
        public bool DistributedFingerprintFallbackToEmby { get; set; } = true;

        [DisplayName("允许 STRM 使用分布式声纹")]
        [Description("默认关闭。只有确认 STRM 最终媒体路径在 Emby 与 worker 上完全一致时再开启；否则 STRM 自动保留原生声纹路径。")]
        [Required]
        [VisibleCondition(nameof(EnableDistributedFingerprintRouting), SimpleCondition.IsTrue)]
        public bool EnableDistributedFingerprintForStrm { get; set; } = false;

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> MarkerEnabledLibraryList { get; set; }

        [DisplayNameL("IntroSkipOptions_MarkerEnabledLibraryScope_Library_Scope", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_MarkerEnabledLibraryScope_Intro_detection_enabled_library_scope__Blank_includes_all_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(MarkerEnabledLibraryList))]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        [Browsable(false)]
        public string MarkerEnabledLibraryScope
        {
            get => string.Empty;
            set { }
        }

        [DisplayName("启用片头片尾检测（总开关）")]
        [Description("片头片尾功能总开关。关闭时播放行为探测和在线数据库匹配都不会执行；要使用 IntroDB.app 或 TheIntroDB.org，必须开启此项。")]
        [Required]
        public bool EnableIntroSkip { get; set; } = false;

        [DisplayName("启用在线片头/片尾数据库匹配")]
        [Description("在线匹配总开关。只有本项和上面的“启用片头片尾检测（总开关）”都开启时才会实际查询 IntroDB.app / TheIntroDB.org。此开关始终显示，避免子选项可见但在线功能实际未启用。")]
        [Required]
        public bool EnableOnlineIntroDb { get; set; } = true;

        [DisplayName("使用 IntroDB.app")]
        [Description("当前公开接口：https://api.introdb.app/segments；读取无需 API Key。若新接口没有完整片头数据，会回退 https://api.introdb.app/intro。按剧集 IMDb ID + 季号 + 集号匹配。")]
        [Required]
        [VisibleCondition(nameof(EnableOnlineIntroDb), SimpleCondition.IsTrue)]
        public bool IntroDbAppEnabled { get; set; } = true;

        [DisplayName("使用 TheIntroDB.org")]
        [Description("当前接口：https://api.theintrodb.org/v3/media。按 TMDB/IMDb ID + 季号 + 集号 + 实际媒体时长匹配，可返回片头、回顾、片尾和预告分段；v3 无结果时保留 v2 兼容回退。")]
        [Required]
        [VisibleCondition(nameof(EnableOnlineIntroDb), SimpleCondition.IsTrue)]
        public bool TheIntroDbEnabled { get; set; } = true;

        [DisplayName("启用自定义片头数据库")]
        [Description("可选第三方来源。关闭时不会访问自定义地址；IntroDB.app 与 TheIntroDB.org 不受影响。")]
        [Required]
        [VisibleCondition(nameof(EnableOnlineIntroDb), SimpleCondition.IsTrue)]
        public bool CustomIntroDbEnabled { get; set; } = false;

        [DisplayName("自定义片头数据库 URL 模板")]
        [Description("支持 {series_tmdb}、{series_imdb}、{episode_tmdb}、{episode_imdb}、{season}、{episode}、{series_name}、{episode_name}、{duration_ms} 占位符。")]
        [VisibleCondition(nameof(CustomIntroDbEnabled), SimpleCondition.IsTrue)]
        public string CustomIntroDbEndpointTemplate { get; set; } = string.Empty;

        [DisplayName("片头数据库优先级")]
        [Description("从左到右查询并合并缺失字段。可用值：IntroDbApp,TheIntroDb,Custom。")]
        [VisibleCondition(nameof(EnableOnlineIntroDb), SimpleCondition.IsTrue)]
        public string IntroDbProviderOrder { get; set; } = "IntroDbApp,TheIntroDb,Custom";

        [DisplayName("在线匹配缓存（分钟）")]
        [Description("相同剧集匹配结果的缓存时间，0 表示不缓存。不同媒体时长使用不同缓存键。")]
        [MinValue(0), MaxValue(1440)]
        [Required]
        [VisibleCondition(nameof(EnableOnlineIntroDb), SimpleCondition.IsTrue)]
        public int IntroDbCacheMinutes { get; set; } = 60;

        [DisplayName("在线匹配超时（秒）")]
        [MinValue(3), MaxValue(120)]
        [Required]
        [VisibleCondition(nameof(EnableOnlineIntroDb), SimpleCondition.IsTrue)]
        public int IntroDbTimeoutSeconds { get; set; } = 15;

        [DisplayName("在线匹配最低置信度")]
        [Description("0–1。低于该值的在线片头结果不会自动写入标记。")]
        [MinValue(0), MaxValue(1)]
        [Required]
        [VisibleCondition(nameof(EnableOnlineIntroDb), SimpleCondition.IsTrue)]
        public double IntroDbMinimumConfidence { get; set; } = 0.75;

        [DisplayName("在线匹配允许写入片尾标记")]
        [Required]
        [VisibleCondition(nameof(EnableOnlineIntroDb), SimpleCondition.IsTrue)]
        public bool IntroDbAllowCreditsMarker { get; set; } = true;

        [DisplayName("新增剧集后自动应用在线匹配")]
        [Description("开启后，新加入的剧集会在延迟后查询在线片头库；高置信度结果直接写入片头/片尾标记。已有完整标记默认不会被覆盖。")]
        [Required]
        [VisibleCondition(nameof(EnableOnlineIntroDb), SimpleCondition.IsTrue)]
        public bool IntroDbAutoApplyOnItemAdded { get; set; } = true;

        [DisplayName("在线匹配延迟（秒）")]
        [Description("等待新剧集完成基础元数据识别并获得 TMDB/IMDb ID 后再查询在线片头库。")]
        [MinValue(3), MaxValue(300)]
        [Required]
        [VisibleCondition(nameof(IntroDbAutoApplyOnItemAdded), SimpleCondition.IsTrue)]
        public int IntroDbAutoApplyDelaySeconds { get; set; } = 30;

        [DisplayName("在线匹配覆盖已有标记")]
        [Description("默认关闭。关闭时在线数据库只补缺失的片头/片尾标记，不删除或覆盖 Emby 已有检测结果。")]
        [Required]
        [VisibleCondition(nameof(EnableOnlineIntroDb), SimpleCondition.IsTrue)]
        public bool IntroDbOverwriteExistingMarkers { get; set; } = false;

        [DisplayNameL("IntroSkipOptions_MaxIntroDurationSeconds", typeof(Resources))]
        [MinValue(10), MaxValue(600)]
        [Required]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public int MaxIntroDurationSeconds { get; set; } = 150;

        [DisplayNameL("IntroSkipOptions_MaxCreditsDurationSeconds", typeof(Resources))]
        [MinValue(10), MaxValue(600)]
        [Required]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public int MaxCreditsDurationSeconds { get; set; } = 360;

        [DisplayNameL("IntroSkipOptions_MinOpeningPlotDurationSeconds", typeof(Resources))]
        [MinValue(30), MaxValue(120)]
        [Required]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public int MinOpeningPlotDurationSeconds { get; set; } = 60;

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> LibraryList { get; set; }

        [DisplayNameL("IntroSkipOptions_LibraryScope_Library_Scope", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_LibraryScope_TV_shows_library_scope_to_detect__Blank_includes_all_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public string LibraryScope { get; set; } = string.Empty;

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> UserList { get; set; }

        [DisplayNameL("IntroSkipOptions_UserScope_User_Scope", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_UserScope_Users_allowed_to_detect__Blank_includes_all", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(UserList))]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public string UserScope { get; set; } = string.Empty;

        [DisplayNameL("IntroSkipOptions_ClientScope_Client_Scope", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_ClientScope_Allowed_clients__Default_is_Emby_Infuse_SenPlayer", typeof(Resources))]
        [Required]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public string ClientScope { get; set; } = "Emby,Infuse,SenPlayer";

        public enum IntroSkipPreference
        {
            [DescriptionL("IntroSkipControl_ResetAndOverwrite_ResetAndOverwrite", typeof(Resources))]
            ResetAndOverwrite,
            [DescriptionL("IntroSkipPreference_NoDetectionButReset_NoDetectionButReset", typeof(Resources))]
            NoDetectionButReset
        }

        [Browsable(false)]
        public List<EditorSelectOption> IntroSkipPreferenceList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("IntroSkipOptions_IntroSkipPreferences_IntroSkip_Preferences", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(IntroSkipPreferenceList))]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public string IntroSkipPreferences { get; set; } = string.Empty;
    }
}
