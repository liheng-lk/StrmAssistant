using MediaBrowser.Controller.Plugins;
using System;
using System.Linq;

namespace StrmAssistant.UI
{
    public sealed class NativeSettingsUiStatus
    {
        public bool RegistrationAttempted { get; set; }
        public bool Registered { get; set; }
        public int TabCount { get; set; }
        public int LegacyPagesHidden { get; set; }
        public string Error { get; set; }
    }

    public static class NativeSettingsUiState
    {
        public static NativeSettingsUiStatus Status { get; internal set; } = new NativeSettingsUiStatus();
    }

    public sealed class NativeSettingsUiEntryPoint : IServerEntryPoint
    {
        private readonly IPluginUIPagesRegistrar _registrar;

        public NativeSettingsUiEntryPoint(IPluginUIPagesRegistrar registrar)
        {
            _registrar = registrar;
        }

        public void Run()
        {
            var status = new NativeSettingsUiStatus { RegistrationAttempted = true };
            NativeSettingsUiState.Status = status;

            try
            {
                var plugin = Plugin.Instance;
                if (plugin == null)
                    throw new InvalidOperationException("Plugin.Instance is not initialized.");

                var controller = new NativeSettingsMainController(plugin.GetPluginInfo(), plugin);
                status.TabCount = controller.TabPageControllers.Count;
                status.Registered = _registrar.RegisterPageController(plugin, controller);
                if (!status.Registered)
                    throw new InvalidOperationException("Emby IPluginUIPagesRegistrar rejected the native settings controller.");

                var registrations = _registrar.GetPluginUIPageRegistrations();
                foreach (var registration in registrations.Where(r => r.Plugin != null && r.Plugin.Id == plugin.Id))
                {
                    var info = registration.PageInfo;
                    if (info == null || string.Equals(info.Name, "StrmAssistantNativeSettings", StringComparison.Ordinal))
                        continue;

                    // BasePluginSimpleUI remains the authoritative settings store, but its generated one-page UI
                    // must no longer compete with the native tabbed controller for the plugin Settings route.
                    info.EnableInMainMenu = false;
                    info.IsMainConfigPage = false;
                    status.LegacyPagesHidden++;
                }

                plugin.Logger.Info("Native settings UI registration: Registered={0}, Tabs={1}, LegacyPagesHidden={2}",
                    status.Registered, status.TabCount, status.LegacyPagesHidden);
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Error("Native settings UI registration failed: " + status.Error);
            }
        }

        public void Dispose()
        {
        }
    }
}
