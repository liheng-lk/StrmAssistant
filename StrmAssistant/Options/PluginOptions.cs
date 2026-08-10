using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System;
using System.ComponentModel;
using System.Linq;
using static StrmAssistant.Options.GeneralOptions;

namespace StrmAssistant.Options
{
    public class PluginOptions : EditableOptionsBase
    {
        public override string EditorTitle => Resources.PluginOptions_EditorTitle_Strm_Assistant;

        public override string EditorDescription => string.Empty;
        
        public GenericItemList Disclaimer { get; set; } = new GenericItemList();

        [VisibleCondition(nameof(ShowConflictPluginLoadedStatus), SimpleCondition.IsTrue)]
        public StatusItem ConflictPluginLoadedStatus { get; set; } = new StatusItem();

        [VisibleCondition(nameof(IsModSuccess), SimpleCondition.IsFalse)]
        public StatusItem ModStatus { get; set; } = new StatusItem();

        [DisplayNameL("GeneralOptions_EditorTitle_General_Options", typeof(Resources))]
        public GeneralOptions GeneralOptions { get; set; } = new GeneralOptions();

        [DisplayNameL("PluginOptions_EditorTitle_Strm_Extract", typeof(Resources))]
        public MediaInfoExtractOptions MediaInfoExtractOptions { get; set; } = new MediaInfoExtractOptions();
        
        [DisplayNameL("PluginOptions_MetadataEnhanceOptions_Metadata_Enhance", typeof(Resources))]
        public MetadataEnhanceOptions MetadataEnhanceOptions { get; set; } = new MetadataEnhanceOptions();

        [DisplayNameL("PluginOptions_IntroSkipOptions_Intro_Credits_Detection", typeof(Resources))]
        public IntroSkipOptions IntroSkipOptions { get; set; } = new IntroSkipOptions();

        [DisplayNameL("ExperienceEnhanceOptions_EditorTitle_Experience_Enhance", typeof(Resources))]
        public ExperienceEnhanceOptions ExperienceEnhanceOptions { get; set; } = new ExperienceEnhanceOptions();

        [DisplayNameL("AboutOptions_EditorTitle_About", typeof(Resources))]
        public AboutOptions AboutOptions { get; set; } = new AboutOptions();

        [Browsable(false)]
        public bool? IsModSuccess => true;

        [Browsable(false)]
        public bool ShowConflictPluginLoadedStatus =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetName().Name)
                .Any(n => n == "StrmExtract" || n == "InfuseSync");

        protected override void Validate(ValidationContext context)
        {
            if (GeneralOptions.CatchupMode &&
                GeneralOptions.CatchupTaskScope.Contains(CatchupTask.Fingerprint.ToString()) &&
                !IntroSkipOptions.UnlockIntroSkip)
            {
                context.AddValidationError(Resources.InvalidFingerprintCatchup);
            }

            var experience = ExperienceEnhanceOptions;
            if (experience?.EnableRemoteDeepDelete == true)
            {
                if (!experience.EnableDeepDelete)
                    context.AddValidationError("启用远程/网盘深度删除前必须先启用“深度删除”总开关。");

                if (experience.RemoteDeepDeleteProvider ==
                    ExperienceEnhanceOptions.RemoteDeepDeleteProviderOption.None)
                    context.AddValidationError("远程/网盘深度删除必须选择 OpenList 或 WebDav 提供方。");

                if (!Uri.TryCreate(experience.RemoteDeepDeleteBaseUrl, UriKind.Absolute, out var remoteUri) ||
                    (remoteUri.Scheme != Uri.UriSchemeHttp && remoteUri.Scheme != Uri.UriSchemeHttps))
                    context.AddValidationError("远程删除 Base URL 必须是有效的 HTTP/HTTPS 绝对地址。");

                if (string.IsNullOrWhiteSpace(experience.RemoteDeepDeleteAllowedRoots))
                    context.AddValidationError("远程/网盘深度删除必须至少配置一个“允许删除的远端根目录”。");

                if (experience.RemoteDeepDeleteProvider ==
                        ExperienceEnhanceOptions.RemoteDeepDeleteProviderOption.OpenList &&
                    string.IsNullOrWhiteSpace(experience.RemoteDeepDeleteAccessToken))
                    context.AddValidationError("OpenList 远程删除需要 Access Token。");

                if (experience.RemoteDeepDeleteAssociatedFiles &&
                    experience.RemoteDeepDeleteProvider != ExperienceEnhanceOptions.RemoteDeepDeleteProviderOption.OpenList)
                    context.AddValidationError("“删除 OpenList 远端关联文件”只能与 OpenList 远程删除提供方一起使用。");

                if (experience.RemoteDeepDeleteAssociatedFiles &&
                    !experience.RemoteDeepDeleteTreatNotFoundAsSuccess)
                    context.AddValidationError(
                        "启用“删除 OpenList 远端关联文件”时必须开启“远端对象已不存在时视为成功”，否则主文件已删除后的半完成事务无法安全重试。");
            }
            else if (experience?.RemoteDeepDeleteAssociatedFiles == true)
            {
                context.AddValidationError("启用“删除 OpenList 远端关联文件”前必须先启用“远程/网盘深度删除”。");
            }
        }
    }
}
