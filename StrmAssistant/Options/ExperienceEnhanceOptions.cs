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

        [DisplayName("深度删除")]
        [Description("启用安全深度删除能力。该功能不会绑定 Emby 的 ItemRemoved 事件自动删除文件，只允许由明确的用户删除动作调用。")]
        [Required]
        public bool EnableDeepDelete { get; set; } = false;

        [DisplayName("深度删除预演模式 (Dry Run)")]
        [Description("开启时只生成并记录删除计划，不实际删除任何文件。建议首次配置时保持开启。")]
        [VisibleCondition(nameof(EnableDeepDelete), SimpleCondition.IsTrue)]
        public bool DeepDeleteDryRun { get; set; } = true;

        [DisplayName("允许删除的根目录")]
        [Description("每行填写一个允许删除的本地根目录。目标文件不在这些目录内时会被拒绝。留空时禁止删除 STRM 指向的目标文件。")]
        [VisibleCondition(nameof(EnableDeepDelete), SimpleCondition.IsTrue)]
        public string DeepDeleteAllowedRoots { get; set; } = string.Empty;

        [DisplayName("删除 STRM 指向的本地目标文件")]
        [Description("仅对本地绝对路径或 file:// 路径生效；HTTP/HTTPS 等远程地址永远不会删除。")]
        [VisibleCondition(nameof(EnableDeepDelete), SimpleCondition.IsTrue)]
        public bool DeepDeleteTargetFile { get; set; } = false;

        [DisplayName("删除关联文件")]
        [Description("删除与目标媒体文件同名前缀的 NFO、JSON、图片、字幕等关联文件。")]
        [VisibleCondition(nameof(EnableDeepDelete), SimpleCondition.IsTrue)]
        public bool DeepDeleteAssociatedFiles { get; set; } = true;

        [DisplayName("清理空目录")]
        [Description("删除完成后清理允许根目录内的空目录；不会删除允许根目录本身。默认关闭。")]
        [VisibleCondition(nameof(EnableDeepDelete), SimpleCondition.IsTrue)]
        public bool DeepDeleteEmptyDirectories { get; set; } = false;

        [DisplayName("隐藏合集媒体库")]
        [Description("将 Emby 的 BoxSets/合集顶级媒体库加入所有用户的 MyMediaExcludes，仅从用户界面隐藏，不删除合集和刮削配置。关闭后只撤销由本插件添加的隐藏项。")]
        [Required]
        public bool HideCollectionsLibrary { get; set; } = false;

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
