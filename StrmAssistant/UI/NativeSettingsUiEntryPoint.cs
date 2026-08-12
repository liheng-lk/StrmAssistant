using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.UI
{
    public sealed class NativeSettingsUiStatus
    {
        public bool RegistrationAttempted { get; set; }
        public bool Registered { get; set; }
        public int TabCount { get; set; }
        public int LegacyPagesHidden { get; set; }
        public bool LegacyRollbackPerformed { get; set; }
        public string Error { get; set; }
    }

    public static class NativeSettingsUiState
    {
        public static NativeSettingsUiStatus Status { get; internal set; } = new NativeSettingsUiStatus();
    }

    public sealed class NativeSettingsUiEntryPoint : IServerEntryPoint
    {
        private readonly IPluginUIPagesRegistrar _registrar;
        private readonly IJsonSerializer _jsonSerializer;

        public NativeSettingsUiEntryPoint(IPluginUIPagesRegistrar registrar, IJsonSerializer jsonSerializer)
        {
            _registrar = registrar;
            _jsonSerializer = jsonSerializer;
        }

        public void Run()
        {
            var status = new NativeSettingsUiStatus { RegistrationAttempted = true };
            NativeSettingsUiState.Status = status;
            var hiddenPages = new List<Tuple<PluginPageInfo, bool, bool>>();

            try
            {
                var plugin = Plugin.Instance;
                if (plugin == null)
                    throw new InvalidOperationException("Plugin.Instance is not initialized.");

                // Hide the generated BasePluginSimpleUI page before registering the native controller.
                // Keep all old flags so a failed native registration can roll back without leaving the
                // plugin with no accessible settings page.
                var existing = _registrar.GetPluginUIPageRegistrations();
                foreach (var registration in existing.Where(r => r.Plugin != null && r.Plugin.Id == plugin.Id))
                {
                    var info = registration.PageInfo;
                    if (info == null || string.Equals(info.Name, "StrmAssistantNativeSettings", StringComparison.Ordinal))
                        continue;

                    hiddenPages.Add(Tuple.Create(info, info.EnableInMainMenu, info.IsMainConfigPage));
                    info.EnableInMainMenu = false;
                    info.IsMainConfigPage = false;
                    status.LegacyPagesHidden++;
                }

                var controller = new NativeSettingsMainController(plugin.GetPluginInfo(), plugin, _jsonSerializer);
                status.TabCount = controller.TabPageControllers.Count;
                status.Registered = _registrar.RegisterPageController(plugin, controller);
                if (!status.Registered)
                    throw new InvalidOperationException("Emby IPluginUIPagesRegistrar rejected the native settings controller.");

                plugin.Logger.Info("Native settings UI registration: Registered={0}, Tabs={1}, LegacyPagesHidden={2}",
                    status.Registered, status.TabCount, status.LegacyPagesHidden);
            }
            catch (Exception ex)
            {
                foreach (var saved in hiddenPages)
                {
                    saved.Item1.EnableInMainMenu = saved.Item2;
                    saved.Item1.IsMainConfigPage = saved.Item3;
                }

                if (hiddenPages.Count > 0) status.LegacyRollbackPerformed = true;
                status.LegacyPagesHidden = 0;
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Error("Native settings UI registration failed: " + status.Error);
            }
        }

        public void Dispose()
        {
        }
    }
}
