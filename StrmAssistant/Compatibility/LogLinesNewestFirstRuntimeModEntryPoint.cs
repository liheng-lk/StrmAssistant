using HarmonyLib;
using MediaBrowser.Controller.Plugins;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class LogLinesNewestFirstCapabilityStatus
    {
        public bool RequestTypeFound { get; set; }
        public bool TargetFound { get; set; }
        public bool Patched { get; set; }
        public bool AsyncTargetUnsupported { get; set; }
        public string RequestType { get; set; }
        public string Target { get; set; }
        public string ReturnType { get; set; }
        public string Route { get; set; }
        public bool LastTransformSucceeded { get; set; }
        public string Error { get; set; }
        public string LastTransformError { get; set; }
    }

    public static class LogLinesNewestFirstModState
    {
        public static LogLinesNewestFirstCapabilityStatus Status { get; internal set; } =
            new LogLinesNewestFirstCapabilityStatus();
    }

    /// <summary>
    /// Runtime-discovered patch for GET /System/Logs/{Name}/Lines. It changes only the order of
    /// the returned QueryResult&lt;string&gt;.Items collection. Log files, file-list ordering and
    /// TotalRecordCount are untouched. If the runtime handler is asynchronous or cannot be
    /// discovered, the feature remains a no-op and exposes that state through diagnostics.
    /// </summary>
    public sealed class LogLinesNewestFirstRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.log-lines-newest-first";
        private const string TargetRoute = "/System/Logs/{Name}/Lines";
        private Harmony _harmony;

        public void Run()
        {
            var status = new LogLinesNewestFirstCapabilityStatus { Route = TargetRoute };
            LogLinesNewestFirstModState.Status = status;

            try
            {
                LoadApiAssembliesBestEffort();
                var requestType = FindRequestType(TargetRoute);
                status.RequestTypeFound = requestType != null;
                status.RequestType = requestType?.FullName;
                if (requestType == null) return;

                var target = FindServiceMethod(requestType);
                status.TargetFound = target != null;
                status.Target = target?.DeclaringType?.FullName + "." + target?.Name;
                status.ReturnType = target?.ReturnType?.FullName;
                if (target == null) return;

                if (typeof(Task).IsAssignableFrom(target.ReturnType))
                {
                    status.AsyncTargetUnsupported = true;
                    status.Error = "The runtime log-lines handler returns Task; the safe in-place QueryResult postfix was not installed.";
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                var postfix = typeof(LogLinesNewestFirstPatches).GetMethod(
                    nameof(LogLinesNewestFirstPatches.LogLinesPostfix),
                    BindingFlags.Public | BindingFlags.Static);
                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                status.Patched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Log newest-first runtime patch unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
        }

        private static void LoadApiAssembliesBestEffort()
        {
            foreach (var name in new[] { "Emby.Api", "MediaBrowser.Api", "Emby.Server.Implementations" })
            {
                try
                {
                    if (!AppDomain.CurrentDomain.GetAssemblies().Any(a =>
                            string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase)))
                        Assembly.Load(name);
                }
                catch
                {
                    // Assembly names differ between Emby generations. Discovery below is authoritative.
                }
            }
        }

        private static Type FindRequestType(string route)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    if (type == null || !type.IsClass) continue;
                    foreach (var attribute in SafeGetAttributes(type))
                    {
                        var path = ReadAttributeString(attribute, "Path") ??
                                   ReadAttributeString(attribute, "Route") ??
                                   ReadAttributeString(attribute, "Template");
                        if (string.Equals(NormalizeRoute(path), NormalizeRoute(route),
                                StringComparison.OrdinalIgnoreCase))
                            return type;
                    }
                }
            }
            return null;
        }

        private static MethodInfo FindServiceMethod(Type requestType)
        {
            var candidates = new List<MethodInfo>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    if (type == null || type.IsAbstract) continue;
                    MethodInfo[] methods;
                    try
                    {
                        methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var method in methods)
                    {
                        if (!string.Equals(method.Name, "Get", StringComparison.OrdinalIgnoreCase) &&
                            method.Name.IndexOf("Log", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        var parameters = method.GetParameters();
                        if (parameters.Length == 1 && parameters[0].ParameterType == requestType)
                            candidates.Add(method);
                    }
                }
            }

            // Prefer synchronous concrete QueryResult/object handlers. They allow a no-copy,
            // post-return reordering of Items without changing the REST contract.
            return candidates
                .OrderBy(method => typeof(Task).IsAssignableFrom(method.ReturnType) ? 1 : 0)
                .ThenBy(method => method.ReturnType == typeof(object) ? 0 : 1)
                .ThenBy(method => method.DeclaringType?.FullName, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(v => v != null); }
            catch { return Array.Empty<Type>(); }
        }

        private static IEnumerable<object> SafeGetAttributes(Type type)
        {
            try { return type.GetCustomAttributes(false); }
            catch { return Array.Empty<object>(); }
        }

        private static string ReadAttributeString(object attribute, string propertyName)
        {
            try
            {
                var property = attribute?.GetType().GetProperty(propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return property?.CanRead == true ? Convert.ToString(property.GetValue(attribute)) : null;
            }
            catch { return null; }
        }

        private static string NormalizeRoute(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            value = value.Trim();
            if (!value.StartsWith("/", StringComparison.Ordinal)) value = "/" + value;
            return value.TrimEnd('/');
        }
    }

    public static class LogLinesNewestFirstPatches
    {
        public static void LogLinesPostfix(object __result)
        {
            var status = LogLinesNewestFirstModState.Status;
            status.LastTransformSucceeded = false;
            status.LastTransformError = null;

            try
            {
                var options = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
                if (options?.DisplayLogLinesNewestFirst != true || __result == null) return;

                var result = UnwrapResult(__result);
                if (result == null)
                {
                    status.LastTransformError = "The log-lines handler returned no inspectable result object.";
                    return;
                }

                var itemsProperty = result.GetType().GetProperty("Items",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (itemsProperty?.CanRead != true)
                {
                    status.LastTransformError = "The runtime QueryResult object has no readable Items property.";
                    return;
                }

                var items = itemsProperty.GetValue(result);
                if (items == null)
                {
                    status.LastTransformSucceeded = true;
                    return;
                }

                if (items is Array array)
                {
                    Array.Reverse(array);
                    status.LastTransformSucceeded = true;
                    return;
                }

                if (items is IList list)
                {
                    for (var left = 0; left < list.Count / 2; left++)
                    {
                        var right = list.Count - 1 - left;
                        var temp = list[left];
                        list[left] = list[right];
                        list[right] = temp;
                    }
                    status.LastTransformSucceeded = true;
                    return;
                }

                // Some serializers expose IEnumerable<string> through an interface type. If the
                // property is writable, create a string[] replacement without changing count.
                if (items is IEnumerable enumerable && itemsProperty.CanWrite)
                {
                    var values = enumerable.Cast<object>().Reverse().ToArray();
                    if (itemsProperty.PropertyType.IsAssignableFrom(values.GetType()))
                    {
                        itemsProperty.SetValue(result, values);
                        status.LastTransformSucceeded = true;
                        return;
                    }
                }

                status.LastTransformError = "The runtime Items collection is not reversible in place.";
            }
            catch (Exception ex)
            {
                status.LastTransformError = ex.GetBaseException().Message;
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Log newest-first transform skipped: " + status.LastTransformError);
            }
        }

        private static object UnwrapResult(object result)
        {
            if (result == null) return null;
            if (HasItemsProperty(result)) return result;

            foreach (var propertyName in new[] { "Result", "Data", "Value", "Response" })
            {
                try
                {
                    var property = result.GetType().GetProperty(propertyName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var nested = property?.CanRead == true ? property.GetValue(result) : null;
                    if (nested != null && HasItemsProperty(nested)) return nested;
                }
                catch { }
            }
            return result;
        }

        private static bool HasItemsProperty(object value)
        {
            try
            {
                return value?.GetType().GetProperty("Items",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;
            }
            catch { return false; }
        }
    }
}
