using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.Collections.Generic;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    public class GeneralOptions : EditableOptionsBase
    {
        [DisplayNameL("GeneralOptions_EditorTitle_General_Options", typeof(Resources))]
        public override string EditorTitle => Resources.GeneralOptions_EditorTitle_General_Options;

        [DisplayNameL("PluginOptions_CatchupMode_Catch_up_Mode__Experimental_", typeof(Resources))]
        [DescriptionL("PluginOptions_CatchupMode_Catch_up_users_favorites__exclusive_to_Strm___Default_is_False_", typeof(Resources))]
        [Required]
        public bool CatchupMode { get; set; } = false;

        public enum CatchupTask
        {
            [DescriptionL("CatchupTask_MediaInfo_MediaInfo", typeof(Resources))]
            MediaInfo,
            [DescriptionL("CatchupTask_Fingerprint_Fingerprint", typeof(Resources))]
            Fingerprint,
            [DescriptionL("CatchupTask_IntroSkip_IntroSkip", typeof(Resources))]
            IntroSkip,
            [DescriptionL("CatchupTask_EpisodeRefresh_EpisodeRefresh", typeof(Resources))]
            EpisodeRefresh
        }
        
        [Browsable(false)]
        public IEnumerable<EditorSelectOption> CatchupTaskList { get; set; }

        [DisplayNameL("GeneralOptions_CatchupScope_Catchup_Scope", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(CatchupTaskList))]
        [VisibleCondition(nameof(CatchupMode), SimpleCondition.IsTrue)]
        public string CatchupTaskScope { get; set; } = CatchupTask.MediaInfo.ToString();

        [DisplayNameL("PluginOptions_MaxConcurrentCount_Max_Concurrent_Count", typeof(Resources))]
        [DescriptionL("PluginOptions_MaxConcurrentCount_Max_Concurrent_Count_must_be_between_1_to_10__Default_is_1_", typeof(Resources))]
        [Required, MinValue(1), MaxValue(20)]
        public int MaxConcurrentCount { get; set; } = 1;

        [DisplayNameL("GeneralOptions_CooldownSeconds_Cooldown_Time__Seconds___Default_is_0", typeof(Resources))]
        [DescriptionL("GeneralOptions_CooldownDurationSeconds_Applicable_to_single_thread_mode__Default_is_0_", typeof(Resources))]
        [VisibleCondition(nameof(MaxConcurrentCount), ValueCondition.IsEqual, 1)]
        [Required, MinValue(0), MaxValue(60)]
        public int CooldownDurationSeconds { get; set; } = 0;
        
        [DisplayNameL("GeneralOptions_Tier2MaxConcurrentCount_Tier_2_Max_Concurrent_Count", typeof(Resources))]
        [DescriptionL("GeneralOptions_Tier2MaxConcurrentCount_Refresh_metadata__subtitle__local_tasks__Default_is_1_", typeof(Resources))]
        [Required, MinValue(1), MaxValue(20)]
        public int Tier2MaxConcurrentCount { get; set; } = 1;

        [DisplayName("中文搜索增强（兼容模式）")]
        [Description("默认关闭。仅扩展 Emby 自身生成的 FTS SearchTerm，不重建数据库或加载第三方 SQLite tokenizer。目标方法不可用时自动退回 Emby 原生搜索。")]
        [Required]
        public bool EnableChineseSearchEnhance { get; set; } = false;

        [DisplayName("简繁体混合搜索")]
        [Description("保留原始查询，并同时加入简体/繁体等价查询。例如繁体标题可用简体关键词搜索，反之亦然。不会修改媒体标题或数据库内容。")]
        [Required]
        [VisibleCondition(nameof(EnableChineseSearchEnhance), SimpleCondition.IsTrue)]
        public bool EnableSimplifiedTraditionalSearch { get; set; } = true;

        public enum ProxyRoutingMode
        {
            [Description("全部公网请求")]
            Global,
            [Description("仅指定域名/刮削器")]
            Whitelist
        }

        [DisplayName("代理服务器增强（实验）")]
        [Description("为 Emby 新建的 HTTP handler 注入代理。默认关闭；不会修改系统代理。建议先使用白名单模式，仅让 TMDB/TVDB 等指定域名走代理。")]
        [Required]
        public bool EnableProxyServerEnhance { get; set; } = false;

        [DisplayName("代理地址")]
        [Description("HTTP/HTTPS 代理 URL，例如 http://127.0.0.1:7890。支持 URL 中的用户名/密码。")]
        [VisibleCondition(nameof(EnableProxyServerEnhance), SimpleCondition.IsTrue)]
        public string ProxyServerUrl { get; set; } = string.Empty;

        [DisplayName("代理模式")]
        [Description("Global：除本地/私网地址外均走代理；Whitelist：仅下方域名命中时走代理。")]
        [VisibleCondition(nameof(EnableProxyServerEnhance), SimpleCondition.IsTrue)]
        public ProxyRoutingMode ProxyMode { get; set; } = ProxyRoutingMode.Whitelist;

        [DisplayName("代理域名白名单")]
        [Description("逗号、分号或换行分隔，可填 api.themoviedb.org、thetvdb.com 或 *.themoviedb.org。Whitelist 模式下只有命中项走代理。")]
        [VisibleCondition(nameof(EnableProxyServerEnhance), SimpleCondition.IsTrue)]
        public string ProxyWhitelistDomains { get; set; } = "*.themoviedb.org,*.tmdb.org,*.thetvdb.com";

        [DisplayName("额外直连地址")]
        [Description("逗号、分号或换行分隔。除 RFC1918/localhost 外额外强制直连的主机/IP，可用于 NAS、反代、局域网服务。")]
        [VisibleCondition(nameof(EnableProxyServerEnhance), SimpleCondition.IsTrue)]
        public string ProxyBypassHosts { get; set; } = string.Empty;

        [DisplayName("本地发现/回源地址")]
        [Description("可选。填写 Emby/NAS 在本地网络中使用的主机名或 IP；该地址始终直连，不经过代理。此项只影响本插件代理路由，不修改 Emby 的公网地址配置。")]
        [VisibleCondition(nameof(EnableProxyServerEnhance), SimpleCondition.IsTrue)]
        public string ProxyLocalDiscoveryAddress { get; set; } = string.Empty;
    }
}
