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

        [DisplayName("ISO / BDMV 媒体探测（实验）")]
        [Description("启用独立的 ISO/BDMV ffprobe 探测。先通过 Health/Test 接口确认实际服务器上的 ffprobe 输出，再考虑启用写回。")]
        [Required]
        public bool EnableOpticalMediaProbe { get; set; } = false;

        [DisplayName("ISO / BDMV ffprobe 路径")]
        [Description("留空时使用 PATH 中的 ffprobe。Blu-ray ISO/BDMV 探测需要该 ffprobe 构建包含 bluray 协议（libbluray）。")]
        [VisibleCondition(nameof(EnableOpticalMediaProbe), SimpleCondition.IsTrue)]
        public string OpticalProbeExecutablePath { get; set; } = string.Empty;

        [DisplayName("ISO / BDMV 探测超时（秒）")]
        [Description("单个 ISO/BDMV ffprobe 进程的最长运行时间。默认 120 秒。")]
        [Required, MinValue(10), MaxValue(600)]
        [VisibleCondition(nameof(EnableOpticalMediaProbe), SimpleCondition.IsTrue)]
        public int OpticalProbeTimeoutSeconds { get; set; } = 120;

        [DisplayName("允许 ISO / BDMV 写回（实验）")]
        [Description("默认关闭。开启后仍必须通过管理员 Apply 接口逐个项目 Confirm=true 才会写入媒体流、章节、时长/码率/分辨率；不会自动批量覆盖媒体库。")]
        [Required]
        [VisibleCondition(nameof(EnableOpticalMediaProbe), SimpleCondition.IsTrue)]
        public bool EnableOpticalMediaWriteBack { get; set; } = false;

        [DisplayName("分布式提取工具自检（实验）")]
        [Description("启用自定义 ffprobe/ffmpeg 与 rffmpeg 的诊断设置。可先运行 Health 接口验证 worker/协议/滤镜能力。")]
        [Required]
        public bool EnableDistributedExtractDiagnostics { get; set; } = false;

        [DisplayName("分布式 ffprobe 路径")]
        [Description("填写 rffmpeg 的 ffprobe 软链接/包装器路径，或其他兼容 ffprobe。留空使用 PATH 中的 ffprobe。真正启用分布式路由时建议明确填写。")]
        [VisibleCondition(nameof(EnableDistributedExtractDiagnostics), SimpleCondition.IsTrue)]
        public string DistributedFfprobeExecutablePath { get; set; } = string.Empty;

        [DisplayName("分布式 ffmpeg 路径")]
        [Description("可填写 rffmpeg 的 ffmpeg 软链接/包装器路径；留空使用 PATH 中的 ffmpeg。可用于截图、BIF/章节图和声纹等重任务。")]
        [VisibleCondition(nameof(EnableDistributedExtractDiagnostics), SimpleCondition.IsTrue)]
        public string DistributedFfmpegExecutablePath { get; set; } = string.Empty;

        [DisplayName("rffmpeg 可执行文件路径")]
        [Description("可选。填写真实 rffmpeg 可执行文件后，自检接口会额外执行 `rffmpeg status` 返回节点状态。")]
        [VisibleCondition(nameof(EnableDistributedExtractDiagnostics), SimpleCondition.IsTrue)]
        public string RffmpegExecutablePath { get; set; } = string.Empty;

        [DisplayName("分布式工具自检超时（秒）")]
        [Description("ffprobe、ffmpeg 或 rffmpeg status 单次自检超时。默认 30 秒。")]
        [Required, MinValue(5), MaxValue(120)]
        [VisibleCondition(nameof(EnableDistributedExtractDiagnostics), SimpleCondition.IsTrue)]
        public int DistributedToolTimeoutSeconds { get; set; } = 30;

        [DisplayName("启用分布式 MediaInfo 路由（实验）")]
        [Description("默认关闭。开启后，普通视频/音频的 MediaInfo 提取优先调用上方配置的 ffprobe/rffmpeg 包装器，而不是先进入 Emby 原生 ffprobe 流程。ISO/BDMV 仍走独立光盘媒体流程。")]
        [Required]
        [VisibleCondition(nameof(EnableDistributedExtractDiagnostics), SimpleCondition.IsTrue)]
        public bool EnableDistributedExtractRouting { get; set; } = false;

        [DisplayName("分布式失败时回退 Emby 原生提取")]
        [Description("建议保持开启。远端 ffprobe、SSH、worker 或共享路径异常时，自动回退当前 Emby 原生 MediaInfo 提取，避免任务永久卡住。")]
        [Required]
        [VisibleCondition(nameof(EnableDistributedExtractRouting), SimpleCondition.IsTrue)]
        public bool DistributedExtractFallbackToEmby { get; set; } = true;

        [DisplayName("允许 STRM 使用分布式 MediaInfo")]
        [Description("默认关闭。STRM 挂载后得到的临时/转换路径通常无法在远端 worker 复用；只有确认 Emby 与 worker 看见完全相同的目标路径时再开启。")]
        [Required]
        [VisibleCondition(nameof(EnableDistributedExtractRouting), SimpleCondition.IsTrue)]
        public bool EnableDistributedExtractForStrm { get; set; } = false;

        [DisplayName("分布式 MediaInfo 超时（秒）")]
        [Description("单个远端 ffprobe/rffmpeg MediaInfo 任务最长运行时间。默认 600 秒。")]
        [Required, MinValue(30), MaxValue(3600)]
        [VisibleCondition(nameof(EnableDistributedExtractRouting), SimpleCondition.IsTrue)]
        public int DistributedExtractTimeoutSeconds { get; set; } = 600;

        [DisplayName("BIF / 章节图使用分布式 ffmpeg（实验）")]
        [Description("默认关闭。开启后先用配置的 ffmpeg/rffmpeg 在 Emby 原生 chapters 目录预生成缺失章节 JPEG，再交回 Emby ThumbnailGenerator 完成章节/BIF 保存和清理。")]
        [Required]
        [VisibleCondition(nameof(EnableDistributedExtractDiagnostics), SimpleCondition.IsTrue)]
        public bool EnableDistributedChapterImageRouting { get; set; } = false;

        [DisplayName("章节图失败时回退 Emby 原生")]
        [Description("建议保持开启。远端 worker 无法访问媒体路径或无法把输出写回 Emby metadata/chapters 目录时，由 Emby 原生 ThumbnailGenerator 继续生成缺失图片。")]
        [Required]
        [VisibleCondition(nameof(EnableDistributedChapterImageRouting), SimpleCondition.IsTrue)]
        public bool DistributedChapterImageFallbackToEmby { get; set; } = true;

        [DisplayName("单张章节图超时（秒）")]
        [Description("每个章节时间点的 ffmpeg 截图最长运行时间。默认 120 秒。")]
        [Required, MinValue(10), MaxValue(600)]
        [VisibleCondition(nameof(EnableDistributedChapterImageRouting), SimpleCondition.IsTrue)]
        public int DistributedChapterImageTimeoutSeconds { get; set; } = 120;

        public enum ImageCaptureAspectRatioOption
        {
            [Description("保持原始比例")]
            Source,
            [Description("横图 16:9（居中裁剪）")]
            Landscape16x9,
            [Description("竖图 2:3（居中裁剪）")]
            Portrait2x3
        }

        [DisplayName("自定义 / ISO 截图（实验）")]
        [Description("启用管理员手动 Plan/Apply 单帧截图。支持普通视频以及已启用光盘媒体探测的 Blu-ray ISO/BDMV；不会自动批量覆盖封面。")]
        [Required]
        public bool EnableCustomImageCapture { get; set; } = false;

        [DisplayName("本地自定义 ffmpeg 路径")]
        [Description("留空时使用 PATH 中的 ffmpeg。仅影响本插件的自定义截图流程，不修改 Emby 全局编码器配置。")]
        [VisibleCondition(nameof(EnableCustomImageCapture), SimpleCondition.IsTrue)]
        public string ImageCaptureFfmpegExecutablePath { get; set; } = string.Empty;

        [DisplayName("截图使用分布式 ffmpeg")]
        [Description("开启后优先使用上面的分布式 ffmpeg/rffmpeg 包装器。仅当远端执行后输出图片能够同步回 Emby 临时目录时保存才会成功。")]
        [Required]
        [VisibleCondition(nameof(EnableCustomImageCapture), SimpleCondition.IsTrue)]
        public bool EnableDistributedImageCapture { get; set; } = false;

        [DisplayName("截图画面比例")]
        [Description("默认保持视频原始比例。可选择居中裁剪为 16:9 横图或 2:3 竖图；只影响本插件生成的新截图。")]
        [VisibleCondition(nameof(EnableCustomImageCapture), SimpleCondition.IsTrue)]
        public ImageCaptureAspectRatioOption ImageCaptureAspectRatio { get; set; } = ImageCaptureAspectRatioOption.Source;

        [DisplayName("截图超时（秒）")]
        [Description("单次 ffmpeg 截图最长运行时间。默认 120 秒。")]
        [Required, MinValue(10), MaxValue(600)]
        [VisibleCondition(nameof(EnableCustomImageCapture), SimpleCondition.IsTrue)]
        public int ImageCaptureTimeoutSeconds { get; set; } = 120;

        [Browsable(false)]
        [Required]
        public bool EnableImageCapture => false;

        [DisplayNameL("MediaInfoExtractOptions_ImageCaptureOffset_Image_Capture_Offset", typeof(Resources))]
        [Description("截图位置，以媒体总时长百分比计算。默认 10%，手动 API 可为单个项目覆盖该值。")]
        [Required, MinValue(1), MaxValue(99)]
        [VisibleCondition(nameof(EnableCustomImageCapture), SimpleCondition.IsTrue)]
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

        [DisplayName("共享 MediaInfo 同步")]
        [Description("启用管理员 Status/Export/Import 同步接口。同步只读写配置的 JSON 根目录，不连接或修改另一台 Emby 的数据库。")]
        [Required]
        [VisibleCondition(nameof(PersistMediaInfoMode), ValueCondition.IsNotEqual, PersistMediaInfoOption.None)]
        public bool EnableMediaInfoSharedSync { get; set; } = false;

        [DisplayName("共享同步路径映射")]
        [Description("每行一条：本机媒体根目录 => 逻辑根目录。例如 /mnt/media/movies => movies。另一台服务器可用 D:\\Media\\Movies => movies，从而生成相同 Sync Key。最长匹配根目录优先。")]
        [VisibleCondition(nameof(EnableMediaInfoSharedSync), SimpleCondition.IsTrue)]
        public string MediaInfoSyncPathMappings { get; set; } = string.Empty;

        [DisplayName("共享同步必须命中路径映射")]
        [Description("建议开启。开启后只有媒体路径命中上方映射时才允许 Export/Import，避免 Linux/Windows 不同绝对路径生成不同 Key。关闭后未命中时退回旧的文件系统根目录相对路径。")]
        [Required]
        [VisibleCondition(nameof(EnableMediaInfoSharedSync), SimpleCondition.IsTrue)]
        public bool MediaInfoSyncRequireMappedPath { get; set; } = true;

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> LibraryList { get; set; }

        [DisplayNameL("PluginOptions_LibraryScope_Library_Scope", typeof(Resources))]
        [DescriptionL("PluginOptions_LibraryScope_Library_scope_to_extract__Blank_includes_all_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        public string LibraryScope { get; set; } = string.Empty;
    }
}
