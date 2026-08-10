using HarmonyLib;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.Experience;
using StrmAssistant.Options;
using System;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class RemoteDeepDeleteUiAuthorityStatus
    {
        public bool Patched { get; set; }
        public long UiSnapshotsApplied { get; set; }
        public string Error { get; set; }
    }

    public static class RemoteDeepDeleteUiAuthorityState
    {
        public static RemoteDeepDeleteUiAuthorityStatus Status { get; internal set; } =
            new RemoteDeepDeleteUiAuthorityStatus();
    }

    /// <summary>
    /// Once the current GenericUI has been explicitly saved, an empty/default remote configuration is
    /// still authoritative. This prevents an older remote-deep-delete.conf from silently re-enabling
    /// destructive settings after the user clears them in the main plugin UI.
    /// </summary>
    public sealed class RemoteDeepDeleteUiAuthorityEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.remote-delete-ui-authority";
        private Harmony _harmony;

        public void Run()
        {
            var status = new RemoteDeepDeleteUiAuthorityStatus();
            RemoteDeepDeleteUiAuthorityState.Status = status;
            try
            {
                var target = typeof(RemoteDeepDeleteRuntimeSettings).GetMethod(
                    nameof(RemoteDeepDeleteRuntimeSettings.GetSnapshot), BindingFlags.Public | BindingFlags.Static);
                if (target == null)
                {
                    status.Error = "RemoteDeepDeleteRuntimeSettings.GetSnapshot was not found.";
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(target, postfix: new HarmonyMethod(
                    typeof(RemoteDeepDeleteUiAuthorityPatches).GetMethod(
                        nameof(RemoteDeepDeleteUiAuthorityPatches.Postfix),
                        BindingFlags.Public | BindingFlags.Static)));
                status.Patched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Error("Remote delete UI authority patch failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }
    }

    public static class RemoteDeepDeleteUiAuthorityPatches
    {
        private static readonly object Sync = new object();

        public static void Postfix(ref RemoteDeepDeleteOptions __result)
        {
            var ui = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            if (ui?.RemoteDeepDeleteUiAuthoritative != true) return;

            if (!Enum.TryParse(ui.RemoteDeepDeleteProvider.ToString(), true,
                    out RemoteDeepDeleteProviderType provider))
                provider = RemoteDeepDeleteProviderType.None;

            var baseUrl = ui.RemoteDeepDeleteBaseUrl?.Trim() ?? string.Empty;
            if (baseUrl.Length > 0 && (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                                      (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
                baseUrl = string.Empty;

            __result = new RemoteDeepDeleteOptions
            {
                Enabled = ui.EnableRemoteDeepDelete,
                Provider = provider,
                BaseUrl = baseUrl.TrimEnd('/'),
                AccessToken = ui.RemoteDeepDeleteAccessToken?.Trim() ?? string.Empty,
                Username = ui.RemoteDeepDeleteUsername ?? string.Empty,
                Password = ui.RemoteDeepDeletePassword ?? string.Empty,
                PathMappings = ui.RemoteDeepDeletePathMappings ?? string.Empty,
                AllowedRemoteRoots = ui.RemoteDeepDeleteAllowedRoots ?? string.Empty,
                TimeoutSeconds = Math.Max(5, Math.Min(120,
                    ui.RemoteDeepDeleteTimeoutSeconds <= 0 ? 30 : ui.RemoteDeepDeleteTimeoutSeconds)),
                TreatNotFoundAsSuccess = ui.RemoteDeepDeleteTreatNotFoundAsSuccess,
                DeleteAssociatedSidecars = ui.RemoteDeepDeleteAssociatedFiles
            };

            lock (Sync)
            {
                if (RemoteDeepDeleteUiAuthorityState.Status != null)
                    RemoteDeepDeleteUiAuthorityState.Status.UiSnapshotsApplied++;
            }
        }
    }
}
