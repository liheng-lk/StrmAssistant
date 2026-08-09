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
