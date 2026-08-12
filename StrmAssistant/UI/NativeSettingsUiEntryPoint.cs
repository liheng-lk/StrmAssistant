namespace StrmAssistant.UI
{
    public sealed class NativeSettingsUiStatus
    {
        public bool RegistrationAttempted { get; set; }
        public bool Registered { get; set; }
        public int TabCount { get; set; }
        public int AdditionalTabCount { get; set; }
        public int LegacyPagesHidden { get; set; }
        public bool LegacyRollbackPerformed { get; set; }
        public bool MainPageIsMainConfigPage { get; set; }
        public string Error { get; set; }
    }

    public static class NativeSettingsUiState
    {
        public static NativeSettingsUiStatus Status { get; internal set; } = new NativeSettingsUiStatus();
    }
}
