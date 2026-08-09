using HarmonyLib;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.Common;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

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
    /// Emby 4.10 can expose IMediaMountManager.Mount with either string or ReadOnlyMemory&lt;char&gt;
    /// input, and the mounted path can move from MountedPath to MountedPathInfo.FullName.
    /// This patch keeps the public LibraryApi contract unchanged.
    /// </summary>
    public sealed class StrmMount410RuntimeCompatibilityEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.strm-mount-410";
        private Harmony _harmony;

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
                if (target == null)
                {
                    status.Error = "LibraryApi.GetStrmMountPath(string) was not found.";
                    return;
                }

                var manager = typeof(LibraryApi).GetField(
                    "_mediaMountManager",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Plugin.LibraryApi);
                var mountMethod = FindMountMethod(manager);
                status.MountMethodFound = mountMethod != null;
                status.MountArgumentType = mountMethod?.GetParameters().FirstOrDefault()?.ParameterType.FullName;

                _harmony = new Harmony(HarmonyId);
                var prefix = typeof(StrmMount410Patches).GetMethod(
                    nameof(StrmMount410Patches.GetStrmMountPathPrefix),
                    BindingFlags.Static | BindingFlags.Public);
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                status.Patched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("STRM mount 4.10 compatibility unavailable: " + status.Error);
            }
        }

        private static MethodInfo FindMountMethod(object manager)
        {
            return manager?.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method =>
                {
                    if (!string.Equals(method.Name, "Mount", StringComparison.Ordinal)) return false;
                    var parameters = method.GetParameters();
                    if (parameters.Length != 3) return false;
                    var first = parameters[0].ParameterType;
                    return first == typeof(string) || first == typeof(ReadOnlyMemory<char>);
                });
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }
    }

    public static class StrmMount410Patches
    {
        public static bool GetStrmMountPathPrefix(LibraryApi __instance, string strmPath, ref Task<string> __result)
        {
            __result = MountAsync(__instance, strmPath);
            return false;
        }

        private static async Task<string> MountAsync(LibraryApi instance, string strmPath)
        {
            try
            {
                var manager = typeof(LibraryApi).GetField(
                    "_mediaMountManager",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance);
                if (manager == null) return null;

                var mountMethod = manager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(method =>
                    {
                        if (!string.Equals(method.Name, "Mount", StringComparison.Ordinal)) return false;
                        var parameters = method.GetParameters();
                        if (parameters.Length != 3) return false;
                        var first = parameters[0].ParameterType;
                        return first == typeof(string) || first == typeof(ReadOnlyMemory<char>);
                    });
                if (mountMethod == null) return null;

                var parameters = mountMethod.GetParameters();
                var invocation = mountMethod.Invoke(manager, new[]
                {
                    GetMountArgument(parameters[0].ParameterType, strmPath),
                    GetMountArgument(parameters[1].ParameterType, null),
                    (object)CancellationToken.None
                });

                if (!(invocation is Task task)) return null;
                await task.ConfigureAwait(false);

                var mediaMount = task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(task);
                if (mediaMount == null) return null;

                try
                {
                    var mountedPath = mediaMount.GetType().GetProperty(
                        "MountedPath", BindingFlags.Instance | BindingFlags.Public)?.GetValue(mediaMount) as string;
                    if (!string.IsNullOrWhiteSpace(mountedPath))
                    {
                        StrmMount410CompatibilityState.Status.MountedPathProperty = "MountedPath";
                        return mountedPath;
                    }

                    var mountedPathInfo = mediaMount.GetType().GetProperty(
                        "MountedPathInfo", BindingFlags.Instance | BindingFlags.Public)?.GetValue(mediaMount);
                    var fullName = mountedPathInfo?.GetType().GetProperty(
                        "FullName", BindingFlags.Instance | BindingFlags.Public)?.GetValue(mountedPathInfo) as string;
                    if (!string.IsNullOrWhiteSpace(fullName))
                    {
                        StrmMount410CompatibilityState.Status.MountedPathProperty = "MountedPathInfo.FullName";
                        return fullName;
                    }

                    return null;
                }
                finally
                {
                    (mediaMount as IDisposable)?.Dispose();
                }
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                Plugin.Instance?.Logger?.Warn("STRM mount compatibility failed: " + ex.InnerException.Message);
                return null;
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("STRM mount compatibility failed: " + ex.Message);
                return null;
            }
        }

        private static object GetMountArgument(Type parameterType, string value)
        {
            if (parameterType == typeof(string)) return value;
            if (parameterType == typeof(ReadOnlyMemory<char>))
                return value == null ? ReadOnlyMemory<char>.Empty : value.AsMemory();
            return value;
        }
    }
}
