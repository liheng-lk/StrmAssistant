using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.UI;
using System.Collections.Generic;

namespace StrmAssistant.Api
{
    public sealed class NativeSettingsUiDiagnosticsResult
    {
        public string Mode { get; set; }
        public bool RegistrationAttempted { get; set; }
        public bool Registered { get; set; }
        public int TabCount { get; set; }
        public int LegacyPagesHidden { get; set; }
        public bool LegacyRollbackPerformed { get; set; }
        public string Error { get; set; }
        public List<string> ExpectedTabs { get; set; }
    }

    [Route("/StrmAssistant/SettingsUi/Status", "GET", Summary = "Get native settings UI registration status")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetNativeSettingsUiDiagnostics : IReturn<NativeSettingsUiDiagnosticsResult> { }

    public sealed class NativeSettingsUiDiagnosticsApiService : BaseApiService
    {
        public object Get(GetNativeSettingsUiDiagnostics request)
        {
            var status = NativeSettingsUiState.Status ?? new NativeSettingsUiStatus();
            return new NativeSettingsUiDiagnosticsResult
            {
                Mode = "Emby native IHasTabbedUIPages",
                RegistrationAttempted = status.RegistrationAttempted,
                Registered = status.Registered,
                TabCount = status.TabCount,
                LegacyPagesHidden = status.LegacyPagesHidden,
                LegacyRollbackPerformed = status.LegacyRollbackPerformed,
                Error = status.Error,
                ExpectedTabs = new List<string> { "常规", "媒体信息", "元数据", "片头片尾", "体验增强", "关于" }
            };
        }
    }
}
