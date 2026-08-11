using MediaBrowser.Controller.Plugins;
using StrmAssistant.Common;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace StrmAssistant.Compatibility
{
    public sealed class StrmMount410CapabilityStatus
    {
        public bool TargetFound { get; set; }
        public bool Patched { get; set; }
        public bool MountMethodFound { get; set; }
        public string MountArgumentType { get; set; }
        public string MountedPathProperty { get; set; }
        public string Error { get; set; }
    }

    public static class StrmMount410CompatibilityState
    {
        public static StrmMount410CapabilityStatus Status { get; internal set; } =
            new StrmMount410CapabilityStatus();
    }

    /// <summary>
    /// Read-only capability probe. LibraryApi now performs STRM mounting through runtime
    /// reflection and supports both string/ReadOnlyMemory&lt;char&gt; mount inputs plus
    /// MountedPath/MountedPathInfo.FullName results, so no Harmony patch is required.
    /// </summary>
    public sealed class StrmMount410RuntimeCompatibilityEntryPoint : IServerEntryPoint
    {
        public void Run()
        {
            var status = new StrmMount410CapabilityStatus();
            StrmMount410CompatibilityState.Status = status;

            try
            {
                var target = typeof(LibraryApi).GetMethod(
                    "GetStrmMountPath",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(string) },
                    null);
                status.TargetFound = target != null;

                var nativeWrapper = typeof(LibraryApi).GetMethod(
                    "MountStrmPath",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(string) },
                    null);
                var mountedPathWrapper = typeof(LibraryApi).GetMethod(
                    "GetMountedPath",
                    BindingFlags.Static | BindingFlags.NonPublic);

                var manager = typeof(LibraryApi).GetField(
                    "_mediaMountManager",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Plugin.LibraryApi);
                var mountMethod = FindMountMethod(manager);
                status.MountMethodFound = mountMethod != null;
                status.MountArgumentType = mountMethod?.GetParameters().FirstOrDefault()?.ParameterType.FullName;
                status.MountedPathProperty = mountedPathWrapper != null
                    ? "dynamic:MountedPath|MountedPathInfo.FullName"
                    : null;

                status.Patched = target != null && nativeWrapper != null && mountedPathWrapper != null &&
                                 mountMethod != null;

                if (!status.TargetFound)
                    status.Error = "LibraryApi.GetStrmMountPath(string) was not found.";
                else if (nativeWrapper == null || mountedPathWrapper == null)
                    status.Error = "LibraryApi native adaptive STRM wrappers were not found.";
                else if (!status.MountMethodFound)
                    status.Error = "No compatible IMediaMountManager.Mount overload was found.";
                else if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug(
                        "STRM mount native compatibility active: argument={0}",
                        status.MountArgumentType ?? "unknown");
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("STRM mount 4.10 capability probe failed: " + status.Error);
            }
        }

        private static MethodInfo FindMountMethod(object manager)
        {
            return manager?.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method =>
                {
                    if (!string.Equals(method.Name, "Mount", StringComparison.Ordinal)) return false;
                    var parameters = method.GetParameters();
                    return parameters.Length == 3 &&
                           parameters[2].ParameterType == typeof(CancellationToken) &&
                           (parameters[0].ParameterType == typeof(string) ||
                            parameters[0].ParameterType == typeof(ReadOnlyMemory<char>));
                });
        }

        public void Dispose()
        {
        }
    }
}
