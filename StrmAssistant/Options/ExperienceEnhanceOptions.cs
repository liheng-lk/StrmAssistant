using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    public class ExperienceEnhanceOptions : EditableOptionsBase
    {
        [DisplayNameL("ExperienceEnhanceOptions_EditorTitle_Experience_Enhance", typeof(Resources))]
        public override string EditorTitle => Resources.ExperienceEnhanceOptions_EditorTitle_Experience_Enhance;

        [DisplayName("通知系统增强")]
        [Description("启用 Strm Assistant 自定义通知事件。默认关闭，启用后才会发送收藏、片头片尾、深度删除、元数据、图片和合集变更通知。")]
        [Required]
        public bool EnableNotificationEnhance { get; set; } = false;

        [DisplayName("收藏更新通知")]
        [Description("媒体新增或更新时，仅向收藏相关媒体的用户发送通知。")]
        [VisibleCondition(nameof(EnableNotificationEnhance), SimpleCondition.IsTrue)]
        public bool NotifyFavoritesUpdate { get; set; } = true;

        [DisplayName("片头片尾更新通知")]
        [Description("片头或片尾标记发生变化时发送通知。")]
        [VisibleCondition(nameof(EnableNotificationEnhance), SimpleCondition.IsTrue)]
        public bool NotifyIntroCreditsUpdate { get; set; } = true;

        [DisplayName("深度删除通知")]
        [Description("深度删除任务成功或预演完成后发送通知。")]
        [VisibleCondition(nameof(EnableNotificationEnhance), SimpleCondition.IsTrue)]
        public bool NotifyDeepDelete { get; set; } = true;

        [DisplayName("元数据更新通知")]
        [Description("仅在手工或 REST API 更新后、跟踪字段的值确实发生变化时发送 metadata.update。")]
        [VisibleCondition(nameof(EnableNotificationEnhance), SimpleCondition.IsTrue)]
        public bool NotifyMetadataUpdate { get; set; } = false;

        [DisplayName("元数据更新跟踪字段")]
        [Description("逗号分隔。支持 Name,Overview,OriginalTitle,Tagline,OfficialRating,CustomRating,CriticRating,CommunityRating,IndexNumber,ParentIndexNumber,PremiereDate,ProductionYear,EndDate,RunTimeTicks,Tags,Genres,Studios,ProductionLocations,ProviderIds。")]
        [VisibleCondition(nameof(NotifyMetadataUpdate), SimpleCondition.IsTrue)]
        public string MetadataUpdateTrackedFields { get; set; } = "Name,Overview,OriginalTitle,Tags,Genres";

        [DisplayName("媒体图片更新通知")]
        [Description("手工或 REST API 更新电影、合集、节目、季、集图像时发送 image.update。")]
        [VisibleCondition(nameof(EnableNotificationEnhance), SimpleCondition.IsTrue)]
        public bool NotifyImageUpdate { get; set; } = false;

        [DisplayName("合集项目变更通知")]
        [Description("合集新增或移除项目时发送 collection.items.added / collection.items.removed。")]
        [VisibleCondition(nameof(EnableNotificationEnhance), SimpleCondition.IsTrue)]
        public bool NotifyCollectionItemsUpdate { get; set; } = false;

        [DisplayName("复制用户后清空继承的通知设置（实验）")]
        [Description("只在运行时明确识别到 Emby 的复制用户 CreateUser 重载时生效。仅重置实际发现为 notification/notifier 的用户级配置项；普通新建用户、源用户及其他用户设置不会修改。")]
        [VisibleCondition(nameof(EnableNotificationEnhance), SimpleCondition.IsTrue)]
        public bool ClearCopiedUserNotificationSettings { get; set; } = true;

        [DisplayName("深度删除")]
        [Description("启用安全深度删除能力。该功能不会绑定 Emby 的 ItemRemoved 事件自动删除文件，只允许由明确的用户删除动作调用。")]
        [Required]
        public bool EnableDeepDelete { get; set; } = false;

        [DisplayName("深度删除预演模式 (Dry Run)")]
        [Description("开启时只生成并记录删除计划，不实际删除任何文件或网盘对象。首次配置远端删除时建议先保持开启。")]
        [VisibleCondition(nameof(EnableDeepDelete), SimpleCondition.IsTrue)]
        public bool DeepDeleteDryRun { get; set; } = true;

        [DisplayName("允许删除的本地根目录")]
        [Description("每行填写一个允许删除的本地根目录。目标文件不在这些目录内时会被拒绝。留空时禁止删除 STRM 指向的本地目标文件。")]
        [EditMultiline(4)]
        [VisibleCondition(nameof(EnableDeepDelete), SimpleCondition.IsTrue)]
        public string DeepDeleteAllowedRoots { get; set; } = string.Empty;

        [DisplayName("删除 STRM 指向的本地目标文件")]
        [Description("仅用于本地绝对路径或 file:// 路径。HTTP/HTTPS 网盘地址由下面的“远程/网盘深度删除”单独处理。")]
        [VisibleCondition(nameof(EnableDeepDelete), SimpleCondition.IsTrue)]
        public bool DeepDeleteTargetFile { get; set; } = false;

        [DisplayName("删除关联文件")]
        [Description("删除与本地目标媒体文件同名前缀的 NFO、JSON、图片、字幕等关联文件。")]
        [VisibleCondition(nameof(EnableDeepDelete), SimpleCondition.IsTrue)]
        public bool DeepDeleteAssociatedFiles { get; set; } = true;

        [DisplayName("清理空目录")]
        [Description("本地删除完成后清理允许根目录内的空目录；不会删除允许根目录本身。默认关闭。")]
        [VisibleCondition(nameof(EnableDeepDelete), SimpleCondition.IsTrue)]
        public bool DeepDeleteEmptyDirectories { get; set; } = false;

        public enum RemoteDeepDeleteProviderOption
        {
            None,
            OpenList,
            WebDav
        }

        [DisplayName("远程/网盘深度删除")]
        [Description("让深度删除同时删除 STRM 指向的 OpenList/AList 或 WebDAV 对象。远端删除只有在路径映射、允许根目录、权限和删除后验证全部通过时才会继续删除本地 STRM/Emby 项目；失败时保持本地项目不动。")]
        [Required]
        [VisibleCondition(nameof(EnableDeepDelete), SimpleCondition.IsTrue)]
        public bool EnableRemoteDeepDelete { get; set; } = false;

        [DisplayName("远程删除提供方")]
        [Description("OpenList 通过 /api/fs/remove 删除并用 /api/fs/get 验证；WebDav 使用 DELETE 并通过 HEAD/PROPFIND 验证。")]
        [VisibleCondition(nameof(EnableRemoteDeepDelete), SimpleCondition.IsTrue)]
        public RemoteDeepDeleteProviderOption RemoteDeepDeleteProvider { get; set; } = RemoteDeepDeleteProviderOption.None;

        [DisplayName("远程服务 Base URL")]
        [Description("例如 https://alist.example.com 或 WebDAV 根地址。不要包含具体媒体路径。")]
        [VisibleCondition(nameof(EnableRemoteDeepDelete), SimpleCondition.IsTrue)]
        public string RemoteDeepDeleteBaseUrl { get; set; } = string.Empty;

        [DisplayName("OpenList Access Token")]
        [Description("仅 OpenList 使用。填写 OpenList API 所需的 Authorization 值；不会在诊断接口中回显明文。")]
        [IsPassword]
        [VisibleCondition(nameof(EnableRemoteDeepDelete), SimpleCondition.IsTrue)]
        public string RemoteDeepDeleteAccessToken { get; set; } = string.Empty;

        [DisplayName("WebDAV 用户名")]
        [VisibleCondition(nameof(EnableRemoteDeepDelete), SimpleCondition.IsTrue)]
        public string RemoteDeepDeleteUsername { get; set; } = string.Empty;

        [DisplayName("WebDAV 密码")]
        [IsPassword]
        [VisibleCondition(nameof(EnableRemoteDeepDelete), SimpleCondition.IsTrue)]
        public string RemoteDeepDeletePassword { get; set; } = string.Empty;

        [DisplayName("远端路径映射")]
        [Description("每行：STRM 地址前缀 => 网盘内部路径根。例如 https://alist.example.com/d/115 => /115。最长前缀优先。OpenList 同源 /d/ 直链也支持安全自动映射，但显式映射优先。")]
        [EditMultiline(6)]
        [VisibleCondition(nameof(EnableRemoteDeepDelete), SimpleCondition.IsTrue)]
        public string RemoteDeepDeletePathMappings { get; set; } = string.Empty;

        [DisplayName("允许删除的远端根目录")]
        [Description("每行一个 OpenList/WebDAV 内部路径，例如 /115/影视。只有该目录及其子目录允许执行远端删除；留空时远端破坏性操作一律阻止。")]
        [EditMultiline(5)]
        [VisibleCondition(nameof(EnableRemoteDeepDelete), SimpleCondition.IsTrue)]
        public string RemoteDeepDeleteAllowedRoots { get; set; } = string.Empty;

        [DisplayName("远端删除超时（秒）")]
        [MinValue(5), MaxValue(120)]
        [Required]
        [VisibleCondition(nameof(EnableRemoteDeepDelete), SimpleCondition.IsTrue)]
        public int RemoteDeepDeleteTimeoutSeconds { get; set; } = 30;

        [DisplayName("远端对象已不存在时视为成功")]
        [Description("建议开启。删除前对象已经不存在或删除后验证返回 404/410 时，允许继续清理本地 STRM/Emby 项目。")]
        [Required]
        [VisibleCondition(nameof(EnableRemoteDeepDelete), SimpleCondition.IsTrue)]
        public bool RemoteDeepDeleteTreatNotFoundAsSuccess { get; set; } = true;

        [DisplayName("隐藏合集媒体库")]
        [Description("将 Emby 的 BoxSets/合集顶级媒体库加入所有用户的 MyMediaExcludes，仅从用户界面隐藏，不删除合集和刮削配置。关闭后只撤销由本插件添加的隐藏项。")]
        [Required]
        public bool HideCollectionsLibrary { get; set; } = false;

        [DisplayName("人物显示过滤（实验）")]
        [Description("在电影/剧集详情返回 DTO 时过滤 People 列表，不删除人物数据库记录。关闭时完全保持 Emby 原始行为。")]
        [Required]
        public bool EnablePeopleDisplayFilter { get; set; } = false;

        [DisplayName("隐藏无头像人物")]
        [Description("只在详情页返回结果中隐藏没有 Primary Image 的人物。")]
        [VisibleCondition(nameof(EnablePeopleDisplayFilter), SimpleCondition.IsTrue)]
        public bool HidePeopleWithoutImage { get; set; } = false;

        [DisplayName("仅显示演员")]
        [Description("只保留 Actor / GuestStar；导演、编剧等其他人物仍保存在数据库中，只是不显示。")]
        [VisibleCondition(nameof(EnablePeopleDisplayFilter), SimpleCondition.IsTrue)]
        public bool ShowActorsOnly { get; set; } = false;

        [DisplayName("隐藏非中文人物名")]
        [Description("仅保留名称中包含中日韩统一表意文字的人物。适合中文人物库整理；不会改写或删除人物元数据。")]
        [VisibleCondition(nameof(EnablePeopleDisplayFilter), SimpleCondition.IsTrue)]
        public bool HideNonChinesePeopleNames { get; set; } = false;

        [DisplayName("缺集显示增强（实验）")]
        [Description("将 TMDB / TMDB Episode Group / Local Episode Group 的全季集列表接入 Emby 缺集功能，并尽量保持原 MovieDb provider 的优先级位置。")]
        [Required]
        public bool EnhanceMissingEpisodes { get; set; } = false;

        [DisplayName("未匹配剧集标题美化（实验）")]
        [Description("只在 DTO/UI 返回层为缺少标题的 Episode 生成可读的季/集显示标题，不写入数据库、不覆盖已有 Name。")]
        [Required]
        public bool BeautifyMissingEpisodeMetadata { get; set; } = false;

        [DisplayName("多段视频标题美化（实验）")]
        [Description("AdditionalPart/分段视频在 DTO/UI 中显示为 Part 2 / 第 2 部分等，不修改文件名和数据库元数据。")]
        [Required]
        public bool BeautifyMultipartTitles { get; set; } = false;

        [DisplayName("显示总集数而不是未看集数（实验）")]
        [Description("仅在 Series/Season DTO 返回层把 UnplayedItemCount 显示值替换成实际 Episode 总数；不修改用户真实已看/未看数据。")]
        [Required]
        public bool DisplayTotalEpisodeCount { get; set; } = false;

        [DisplayName("主页媒体库显示项目数（实验）")]
        [Description("在顶级媒体库 DTO 的 RecursiveItemCount 中写入主内容类型数量：电影库计 Movie、电视剧库计 Series、音乐库计 MusicAlbum 等；仅影响客户端显示，不修改媒体库。")]
        [Required]
        public bool DisplayLibraryItemCount { get; set; } = false;

        [DisplayName("日志内容最新优先（实验）")]
        [Description("在 Emby 管理后台使用日志 Lines API 时，仅把返回的日志行顺序反转为最新行在前；不改日志文件、不改日志文件列表。运行时找不到兼容 API 时自动保持原生行为。")]
        [Required]
        public bool DisplayLogLinesNewestFirst { get; set; } = false;

        [DisplayName("播完当前集后补记之前有进度的集")]
        [Description("仅在当前 Episode 因 PlaybackFinished 被标记为已播放后，将同剧中更早且存在播放进度但尚未已播放的集标记为已播放。手工切换已播放状态不会触发。")]
        [Required]
        public bool MarkPriorProgressEpisodesPlayed { get; set; } = false;

        [DisplayNameL("GeneralOptions_MergeMultiVersion_Merge_Multiple_Versions", typeof(Resources))]
        [DescriptionL("GeneralOptions_MergeMultiVersion_Auto_merge_multiple_versions_if_in_the_same_folder_", typeof(Resources))]
        [Required]
        public bool MergeMultiVersion { get; set; } = false;

        public enum MergeMoviesScopeOption
        {
            [DescriptionL("MergeScopeOption_LibraryScope_LibraryScope", typeof(Resources))]
            LibraryScope,
            [DescriptionL("MergeScopeOption_GlobalScope_GlobalScope", typeof(Resources))]
            GlobalScope
        }

        [DisplayName("")]
        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        public MergeMoviesScopeOption MergeMoviesPreference { get; set; } = MergeMoviesScopeOption.LibraryScope;

        [DisplayName("每组最多合并电影版本数")]
        [Description("0 表示不限数量（默认），不会使用固定 8 个版本限制。设置大于 0 时仅合并排序后的前 N 个版本，其余版本保持独立。")]
        [Required, MinValue(0)]
        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        public int MaxMergedMovieVersions { get; set; } = 0;

        public enum MergeSeriesScopeOption
        {
            [DescriptionL("MergeScopeOption_LibraryScope_LibraryScope", typeof(Resources))]
            LibraryScope,
            [DescriptionL("MergeScopeOption_GlobalScope_GlobalScope", typeof(Resources))]
            GlobalScope
        }

        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        [Browsable(false)]
        public MergeSeriesScopeOption MergeSeriesPreference => MergeSeriesScopeOption.LibraryScope;
    }
}
