using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using MediaBrowser.Model.MediaInfo;
using StrmAssistant.Properties;
using System.Collections.Generic;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    public class MediaInfoExtractOptions : EditableOptionsBase
    {
        [DisplayNameL("PluginOptions_EditorTitle_Strm_Extract", typeof(Resources))]
        public override string EditorTitle => Resources.PluginOptions_EditorTitle_Strm_Extract;

        [DisplayNameL("PluginOptions_IncludeExtra_Include_Extra", typeof(Resources))]
        [DescriptionL("PluginOptions_IncludeExtra_Include_media_extras_to_extract__Default_is_False_", typeof(Resources))]
        [Required]
        public bool IncludeExtra { get; set; } = false;

        [DisplayName("提取黑名单")]
        [Description("对媒体信息提取、片头声纹和视频预览缩略图使用统一过滤规则。默认关闭。")]
        [Required]
        public bool EnableExtractionBlacklist { get; set; } = false;

        [DisplayName("黑名单标签")]
        [Description("逗号、分号或换行分隔。媒体命中任意标签时跳过 MediaInfo、声纹和 BIF/缩略图任务。")]
        [VisibleCondition(nameof(EnableExtractionBlacklist), SimpleCondition.IsTrue)]
        public string ExtractionBlacklistTags { get; set; } = string.Empty;

        [DisplayName("黑名单关键词")]
        [Description("逗号、分号或换行分隔。名称、原始标题或媒体路径命中任意关键词时跳过 MediaInfo、声纹和 BIF/缩略图任务。")]
        [VisibleCondition(nameof(EnableExtractionBlacklist), SimpleCondition.IsTrue)]
        public string ExtractionBlacklistKeywords { get; set; } = string.Empty;

        [DisplayName("外挂音轨扫描")]
        [Description("扫描并更新电影/剧集旁的外挂音轨。需要 Emby Server 4.9.1.80 或更高版本；较旧版本会自动忽略此选项。")]
        [Required]
        public bool EnableExternalAudioTrackScan { get; set; } = true;

        [DisplayNameL("PluginOptions_EnableImageCapture_Enable_Image_Capture", typeof(Resources))]
        [DescriptionL("PluginOptions_EnableImageCapture_Perform_image_capture_for_videos_without_primary_image__Default_is_False_", typeof(Resources))]
        [Browsable(false)]
        [Required]
        public bool EnableImageCapture => false;

        [DisplayNameL("MediaInfoExtractOptions_ImageCaptureOffset_Image_Capture_Offset", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_ImageCaptureOffset_Image_capture_position_as_a_percentage_of_runtime__Default_is_10_", typeof(Resources))]
        [Required, MinValue(10), MaxValue(90)]
        [VisibleCondition(nameof(EnableImageCapture), SimpleCondition.IsTrue)]
        public int ImageCapturePosition { get; set; } = 10;

        [Browsable(false)]
        [Required]
        public string ImageCaptureExcludeMediaContainers { get; set; } =
            string.Join(",", new[] { MediaContainers.MpegTs, MediaContainers.Ts, MediaContainers.M2Ts });

        public enum PersistMediaInfoOption
        {
            None,
            Default,
            Restore
        }

        [Browsable(false)]
        public List<EditorRadioOption> PersistMediaInfoOptionList { get; set; } = new List<EditorRadioOption>();

        [DisplayName("")]
        [SelectItemsSource(nameof(PersistMediaInfoOptionList))]
        [SelectShowRadioGroup]
        public string PersistMediaInfoMode { get; set; } = PersistMediaInfoOption.None.ToString();

        [DisplayName("音乐媒体信息持久化")]
        [Description("将音乐 Audio 条目的媒体流信息和主图一并写入/恢复 MediaInfo JSON。底层序列化已支持音频和嵌入图片。")]
        [VisibleCondition(nameof(PersistMediaInfoMode), ValueCondition.IsNotEqual, PersistMediaInfoOption.None)]
        public bool PersistMusicMediaInfo { get; set; } = false;

        [DisplayNameL("MediaInfoExtractOptions_MediaInfoJsonRootFolder_MediaInfo_Json_Root_Folder", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_MediaInfoJsonRootFolder_Store_or_load_media_info_JSON_files_under_this_root_folder__Default_is_EMPTY_", typeof(Resources))]
        [EditFolderPicker]
        [VisibleCondition(nameof(PersistMediaInfoMode), ValueCondition.IsNotEqual, PersistMediaInfoOption.None)]
        public string MediaInfoJsonRootFolder { get; set; } = string.Empty;

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> LibraryList { get; set; }

        [DisplayNameL("PluginOptions_LibraryScope_Library_Scope", typeof(Resources))]
        [DescriptionL("PluginOptions_LibraryScope_Library_scope_to_extract__Blank_includes_all_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        public string LibraryScope { get; set; } = string.Empty;
    }
}
