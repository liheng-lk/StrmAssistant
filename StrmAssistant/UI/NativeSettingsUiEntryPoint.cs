using MediaBrowser.Controller.Plugins;
using System;

namespace StrmAssistant.UI
{
    public sealed class NativeSettingsUiStatus
    {
        public bool RegistrationAttempted { get; set; }
        public bool Registered { get; set; }
        public int TabCount { get; set; }
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
                    status.Error = "Emby IPluginUIPagesRegistrar rejected the native settings controller.";

                plugin.Logger.Info("Native settings UI registration: Registered={0}, Tabs={1}",
                    status.Registered, status.TabCount);
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
