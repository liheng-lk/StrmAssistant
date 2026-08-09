using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Configuration;
using StrmAssistant.Common;
using System;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class MediaInfoRuntimeFallbackCapabilityStatus
    {
        public bool TargetFound { get; set; }
        public bool Patched { get; set; }
        public bool ReflectionStaticMediaSourceAvailable { get; set; }
        public int RuntimeStaticMediaSourceParameterCount { get; set; }
        public string RuntimeStaticMediaSourceTarget { get; set; }
        public bool LibraryMonitorIgnoreRuleApplied { get; set; }
        public string Target { get; set; }
        public string Error { get; set; }
    }

    public static class MediaInfoRuntimeFallbackState
    {
        public static MediaInfoRuntimeFallbackCapabilityStatus Status { get; internal set; } =
            new MediaInfoRuntimeFallbackCapabilityStatus();
    }

    /// <summary>
    /// Read-only capability probe plus LibraryMonitor ignore-rule compatibility. MediaInfoApi
    /// itself now discovers Emby's 7/8/10-parameter GetStaticMediaSources overload and invokes
    /// the matching signature directly, so no Harmony patch is required here.
    /// </summary>
    public sealed class MediaInfoRuntimeFallbackEntryPoint : IServerEntryPoint
    {
        private readonly ILibraryMonitor _libraryMonitor;

        public MediaInfoRuntimeFallbackEntryPoint(ILibraryMonitor libraryMonitor)
        {
            _libraryMonitor = libraryMonitor;
        }

        public void Run()
        {
            var status = new MediaInfoRuntimeFallbackCapabilityStatus();
            MediaInfoRuntimeFallbackState.Status = status;

            try
            {
                var wrapper = typeof(MediaInfoApi).GetMethod(
                    "GetStaticMediaSources",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(BaseItem), typeof(bool) },
                    null);
                status.TargetFound = wrapper != null;
                status.Target = wrapper?.ToString();

                var reflectedField = typeof(MediaInfoApi).GetField(
                    "_getStaticMediaSources",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var runtimeMethod = reflectedField?.GetValue(Plugin.MediaInfoApi) as MethodInfo;

                if (runtimeMethod == null)
                {
                    var manager = GetPrivateField<IMediaSourceManager>(Plugin.MediaInfoApi, "_mediaSourceManager");
                    runtimeMethod = manager?.GetType()
                        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(method => string.Equals(method.Name, "GetStaticMediaSources", StringComparison.Ordinal))
                        .OrderByDescending(method => method.GetParameters().Length)
                        .FirstOrDefault(method =>
                        {
                            var parameters = method.GetParameters();
                            return parameters.Length >= 7 &&
                                   parameters[0].ParameterType == typeof(BaseItem) &&
                                   parameters.Any(parameter => parameter.ParameterType == typeof(LibraryOptions));
                        });
                }

                status.ReflectionStaticMediaSourceAvailable = runtimeMethod != null;
                status.RuntimeStaticMediaSourceTarget = runtimeMethod?.ToString();
                var parameterCount = runtimeMethod?.GetParameters().Length ?? 0;
                status.RuntimeStaticMediaSourceParameterCount = parameterCount;
                status.Patched = wrapper != null && runtimeMethod != null &&
                                 (parameterCount == 7 || parameterCount == 8 || parameterCount == 10);
                status.LibraryMonitorIgnoreRuleApplied = TryApplyLibraryMonitorIgnoreRule(_libraryMonitor);

                if (!status.TargetFound)
                    status.Error = "MediaInfoApi.GetStaticMediaSources(BaseItem,bool) was not found.";
                else if (!status.ReflectionStaticMediaSourceAvailable)
                    status.Error = "No compatible Emby GetStaticMediaSources overload was found.";
                else if (!status.Patched)
                    status.Error = "Unsupported Emby GetStaticMediaSources parameter count: " + parameterCount;
                else if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug(
                        "MediaInfo native compatibility active: GetStaticMediaSources args={0}", parameterCount);
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("MediaInfo 4.10 capability probe failed: " + status.Error);
            }
        }

        public void Dispose()
        {
        }

        private static T GetPrivateField<T>(object target, string fieldName) where T : class
        {
            if (target == null) return null;
            return target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(target) as T;
        }

        private static bool TryApplyLibraryMonitorIgnoreRule(ILibraryMonitor libraryMonitor)
        {
            try
            {
                var assembly = TryLoad("Emby.Server.Implementations");
                var monitorType = assembly?.GetType("Emby.Server.Implementations.IO.LibraryMonitor");
                if (monitorType == null || libraryMonitor == null) return false;

                if (TryAppendStringArrayField(monitorType, libraryMonitor, "_alwaysIgnoreExtensions", ".json"))
                    return true;

                return TryAppendStringArrayField(monitorType, libraryMonitor,
                    "_alwaysIgnoreSubstrings", "-mediainfo.json");
            }
            catch
            {
                return false;
            }
        }

        private static bool TryAppendStringArrayField(Type targetType, object target, string fieldName, string value)
        {
            var field = targetType?.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (!(field?.GetValue(target) is string[] current)) return false;
            if (current.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase))) return true;

            var updated = new string[current.Length + 1];
            Array.Copy(current, updated, current.Length);
            updated[updated.Length - 1] = value;
            field.SetValue(target, updated);
            return true;
        }

        private static Assembly TryLoad(string name)
        {
            try { return Assembly.Load(name); }
            catch
            {
                return AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(assembly =>
                        string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
