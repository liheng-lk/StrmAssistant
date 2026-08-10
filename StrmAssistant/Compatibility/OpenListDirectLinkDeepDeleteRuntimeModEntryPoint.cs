using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.Experience;
using System;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class OpenListDirectLinkDeepDeleteStatus
    {
        public bool TargetFound { get; set; }
        public bool Patched { get; set; }
        public long AutoMappedPlans { get; set; }
        public long RejectedAuthorityMismatch { get; set; }
        public long RejectedOutsideAllowedRoots { get; set; }
        public string LastRemotePath { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class OpenListDirectLinkDeepDeleteState
    {
        public static OpenListDirectLinkDeepDeleteStatus Status { get; internal set; } =
            new OpenListDirectLinkDeepDeleteStatus();
    }

    /// <summary>
    /// OpenList-generated STRM links normally expose the mounted path through /d/&lt;path&gt;.
    /// Manual mappings remain authoritative. This fallback only activates when BuildPlan failed
    /// specifically because no manual mapping matched, the direct-link authority equals the configured
    /// OpenList BaseUrl authority, and the decoded path remains inside AllowedRemoteRoots.
    /// </summary>
    public sealed class OpenListDirectLinkDeepDeleteRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.openlist-directlink-delete";
        private Harmony _harmony;

        public void Run()
        {
            var status = new OpenListDirectLinkDeepDeleteStatus();
            OpenListDirectLinkDeepDeleteState.Status = status;
            try
            {
                var target = typeof(RemoteDeepDeleteService).GetMethod(nameof(RemoteDeepDeleteService.BuildPlan),
                    BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(BaseItem) }, null);
                status.TargetFound = target != null;
                if (target == null)
                {
                    status.Error = "RemoteDeepDeleteService.BuildPlan(BaseItem) was not found.";
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(target, postfix: new HarmonyMethod(
                    typeof(OpenListDirectLinkDeepDeletePatches).GetMethod(
                        nameof(OpenListDirectLinkDeepDeletePatches.BuildPlanPostfix),
                        BindingFlags.Public | BindingFlags.Static)));
                status.Patched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Error("OpenList direct-link deep-delete patch failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
        }
    }

    public static class OpenListDirectLinkDeepDeletePatches
    {
        private static readonly object StatusSync = new object();

        public static void BuildPlanPostfix(BaseItem item, ref RemoteDeepDeletePlan __result)
        {
            var plan = __result;
            if (plan == null || plan.Allowed) return;

            var options = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            if (!options.Enabled || options.Provider != RemoteDeepDeleteProviderType.OpenList) return;
            if (string.IsNullOrWhiteSpace(plan.SourceTarget)) return;

            // A valid manual mapping always wins. Only recover the explicit "no mapping" failure.
            if (string.IsNullOrWhiteSpace(plan.Error) ||
                plan.Error.IndexOf("did not match any configured remote path mapping", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            if (!TryResolveDirectLink(plan.SourceTarget, options.BaseUrl, out var remotePath, out var error))
            {
                RecordError(error);
                return;
            }

            var allowedRoots = RemoteDeepDeleteRuntimeSettings.ParseAllowedRoots(options.AllowedRemoteRoots);
            if (!RemoteDeepDeleteRuntimeSettings.IsWithinAllowedRoot(remotePath, allowedRoots))
            {
                lock (StatusSync)
                {
                    var status = OpenListDirectLinkDeepDeleteState.Status;
                    status.RejectedOutsideAllowedRoots++;
                    status.LastRemotePath = remotePath;
                    status.LastError = "Auto-mapped /d/ path is outside AllowedRemoteRoots.";
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(options.AccessToken))
            {
                RecordError("OpenList AccessToken is empty; destructive calls remain blocked.");
                return;
            }

            plan.Applicable = true;
            plan.Allowed = true;
            plan.MatchedSourcePrefix = "[OpenList same-origin /d/ auto-map]";
            plan.RemotePath = remotePath;
            plan.RemoteDirectory = PosixDirName(remotePath);
            plan.RemoteName = PosixBaseName(remotePath);
            plan.EndpointHost = SafeAuthority(options.BaseUrl);
            plan.Error = null;
            plan.Warnings.Add("Remote path was derived automatically from a same-origin OpenList /d/ direct link; manual mapping was not required.");

            lock (StatusSync)
            {
                var status = OpenListDirectLinkDeepDeleteState.Status;
                status.AutoMappedPlans++;
                status.LastRemotePath = remotePath;
                status.LastError = null;
            }
        }

        private static bool TryResolveDirectLink(string sourceTarget, string baseUrl, out string remotePath,
            out string error)
        {
            remotePath = null;
            error = null;

            if (!Uri.TryCreate(baseUrl?.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
                (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            {
                error = "Configured OpenList BaseUrl is invalid.";
                return false;
            }

            Uri targetUri;
            if (Uri.TryCreate(sourceTarget, UriKind.Absolute, out var absolute))
            {
                targetUri = absolute;
            }
            else if (sourceTarget.StartsWith("/d/", StringComparison.OrdinalIgnoreCase) &&
                     Uri.TryCreate(baseUri, sourceTarget, out var relative))
            {
                targetUri = relative;
            }
            else
            {
                error = "STRM target is not an absolute/same-origin OpenList /d/ URL.";
                return false;
            }

            if (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps)
            {
                error = "OpenList /d/ auto-map only accepts HTTP/HTTPS targets.";
                return false;
            }

            if (!SameAuthority(baseUri, targetUri))
            {
                lock (StatusSync)
                {
                    OpenListDirectLinkDeepDeleteState.Status.RejectedAuthorityMismatch++;
                }
                error = "STRM /d/ target authority does not match configured OpenList BaseUrl; use an explicit mapping for aliases/reverse proxies.";
                return false;
            }

            var escapedPath = targetUri.AbsolutePath ?? string.Empty;
            if (!escapedPath.StartsWith("/d/", StringComparison.OrdinalIgnoreCase) || escapedPath.Length <= 3)
            {
                error = "Target is not an OpenList /d/<mount-path> direct link.";
                return false;
            }

            string decoded;
            try { decoded = Uri.UnescapeDataString(escapedPath.Substring(2)); }
            catch (Exception ex)
            {
                error = "Unable to URL-decode OpenList /d/ path: " + ex.Message;
                return false;
            }

            remotePath = RemoteDeepDeleteRuntimeSettings.NormalizeRemotePath(decoded);
            if (string.IsNullOrWhiteSpace(remotePath) || remotePath == "/")
            {
                error = "OpenList /d/ direct link resolved to an invalid/root path.";
                remotePath = null;
                return false;
            }

            return true;
        }

        private static bool SameAuthority(Uri left, Uri right)
        {
            if (!string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)) return false;
            return EffectivePort(left) == EffectivePort(right);
        }

        private static int EffectivePort(Uri uri)
        {
            if (!uri.IsDefaultPort) return uri.Port;
            return uri.Scheme == Uri.UriSchemeHttps ? 443 : 80;
        }

        private static string PosixDirName(string path)
        {
            var index = path?.LastIndexOf('/') ?? -1;
            return index <= 0 ? "/" : path.Substring(0, index);
        }

        private static string PosixBaseName(string path)
        {
            var index = path?.LastIndexOf('/') ?? -1;
            return index < 0 ? path : path.Substring(index + 1);
        }

        private static string SafeAuthority(string baseUrl)
        {
            return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;
        }

        private static void RecordError(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return;
            lock (StatusSync)
            {
                OpenListDirectLinkDeepDeleteState.Status.LastError = error;
            }
        }
    }
}
