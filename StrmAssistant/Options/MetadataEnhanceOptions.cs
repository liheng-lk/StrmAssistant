using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.Collections.Generic;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    public enum RefreshPersonOption
    {
        Default,
        FullRefresh,
        NoAdult
    }

    public class MetadataEnhanceOptions : EditableOptionsBase
    {
        [DisplayNameL("PluginOptions_MetadataEnhanceOptions_Metadata_Enhance", typeof(Resources))]
        public override string EditorTitle => Resources.PluginOptions_MetadataEnhanceOptions_Metadata_Enhance;

        [DisplayName("替代 TMDB 配置（实验）")]
        [Description("默认关闭。仅改写 MovieDb provider 发出的 TMDB API/图片 URL，不修改系统代理或其他元数据提供器。配置无效时自动保持原生地址。")]
        [Required]
        public bool EnableAlternateMovieDbConfig { get; set; } = false;

        [DisplayName("替代 TMDB API 地址")]
        [Description("可选，例如 https://api.tmdb.org 或自建兼容反代。必须是 HTTP/HTTPS 绝对地址；留空继续使用 MovieDb 原生 API 地址。")]
        [VisibleCondition(nameof(EnableAlternateMovieDbConfig), SimpleCondition.IsTrue)]
        public string AlternateMovieDbApiUrl { get; set; } = string.Empty;

        [DisplayName("替代 TMDB 图片地址")]
        [Description("可选，例如自建 image.tmdb.org 兼容反代。只替换以 https://image.tmdb.org 开头的远程图片 URL。")]
        [VisibleCondition(nameof(EnableAlternateMovieDbConfig), SimpleCondition.IsTrue)]
        public string AlternateMovieDbImageUrl { get; set; } = string.Empty;

        [DisplayName("替代 TMDB API Key")]
        [Description("可选，仅接受 32 位十六进制 v3 API key。留空或格式无效时继续使用 MovieDb 内置 key。")]
        [VisibleCondition(nameof(EnableAlternateMovieDbConfig), SimpleCondition.IsTrue)]
        public string AlternateMovieDbApiKey { get; set; } = string.Empty;

        [DisplayName("TMDB 元数据回退语言（实验）")]
        [Description("默认关闭。扩展 MovieDb 自身的元数据语言链，不替换 MovieDb provider；优先语言缺少标题/简介时，可继续尝试指定回退语言。目标接口不可用时自动保持原生行为。")]
        [Required]
        public bool EnableMovieDbFallbackLanguages { get; set; } = false;

        [DisplayName("TMDB 回退语言顺序")]
        [Description("逗号、分号或换行分隔，例如 zh-SG,zh-HK,zh-TW,ja-JP。配置项会插入英文回退之前，重复项自动忽略。")]
        [VisibleCondition(nameof(EnableMovieDbFallbackLanguages), SimpleCondition.IsTrue)]
        public string MovieDbFallbackLanguages { get; set; } = "zh-SG,zh-HK,zh-TW,ja-JP";

        [DisplayName("仅中文首选语言启用 TMDB 回退")]
        [Description("建议保持开启。只有媒体库首选元数据语言以 zh 开头时才扩展回退链，避免改变英文/日文媒体库原有抓取顺序。")]
        [Required]
        [VisibleCondition(nameof(EnableMovieDbFallbackLanguages), SimpleCondition.IsTrue)]
        public bool MovieDbFallbackOnlyForChinese { get; set; } = true;

        [DisplayName("TMDB 图片允许通用中文")]
        [Description("当 MovieDb 图片语言参数已有 zh-CN/zh-HK/zh-TW 等中文区域语言时，额外加入通用 zh，提升没有精确区域标签的中文海报命中率。")]
        [Required]
        [VisibleCondition(nameof(EnableMovieDbFallbackLanguages), SimpleCondition.IsTrue)]
        public bool IncludeGenericChineseImageLanguage { get; set; } = true;

        [DisplayName("原始语言海报优先（实验）")]
        [Description("获取远程图片时保留全部结果，只把推断出的原始语言 Primary 海报排到最前；媒体库首选图片语言其次。不会删除或自动替换已有本地图片。")]
        [Required]
        public bool PreferOriginalPoster { get; set; } = false;

        [DisplayName("背景图也优先原始语言")]
        [Description("开启后 Backdrop 也采用相同的原始语言优先排序；默认关闭，仅影响 Primary 海报。")]
        [Required]
        [VisibleCondition(nameof(PreferOriginalPoster), SimpleCondition.IsTrue)]
        public bool PreferOriginalBackdrop { get; set; } = false;

        [DisplayName("拼音首字母排序")]
        [Description("中文标题按拼音首字母参与字母索引排序；只改变运行时 SortName 计算，不批量写数据库，也不会覆盖已锁定的 SortName。")]
        [Required]
        public bool PinyinSortName { get; set; } = false;
        
        [Browsable(false)]
        [Required]
        public string RefreshPersonMode { get; set; } = RefreshPersonOption.Default.ToString();

        public enum EpisodeRefreshOption
        {
            [DescriptionL("EpisodeRefreshOption_NoOverview_No_Overview", typeof(Resources))]
            NoOverview,
            [DescriptionL("EpisodeRefreshOption_NoImage_No_Image", typeof(Resources))]
            NoImage,
            [DescriptionL("EpisodeRefreshOption_NonChineseOverview_Non_Chinese_Overview", typeof(Resources))]
            NonChineseOverview,
            [DescriptionL("EpisodeRefreshOption_DefaultEpisodeName_Default_Episode_Name", typeof(Resources))]
            DefaultEpisodeName,
            [DescriptionL("EpisodeRefreshOption_ReplaceCapturedImage_Replace_Captured_Image", typeof(Resources))]
            ReplaceCapturedImage
        }

        [Browsable(false)]
        public List<EditorSelectOption> EpisodeRefreshOptionList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("MetadataEnhanceOptions_EpisodeRefreshScope_Episode_Metadata_Refresh_Scope", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EpisodeRefreshScope_Episode_refresh_scope_for_scheduled_task_and_catch_up__Default_is_no_overview_and_no_image_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(EpisodeRefreshOptionList))]
        public string EpisodeRefreshScope { get; set; } = string.Join(",", EpisodeRefreshOption.NoOverview.ToString(),
            EpisodeRefreshOption.NoImage.ToString());
        
        [DisplayNameL("MetadataEnhanceOptions_EpisodeRefreshLookBackDays_Episode_Refresh_Lookback_Days", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EpisodeRefreshLookbackDays_Episode_metadata_refresh_lookback_days__Default_is_365_", typeof(Resources))]
        [Required, MinValue(1)]
        public int EpisodeRefreshLookBackDays { get; set; } = 365;

        [Browsable(false)]
        public int EpisodeRefreshLookbackDays
        {
            get => EpisodeRefreshLookBackDays;
            set => EpisodeRefreshLookBackDays = value;
        }
    }
}
