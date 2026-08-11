using HarmonyLib;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.Search;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class AdvancedChineseSearchConnectionCapabilityStatus
    {
        public bool BaseRepositoryTypeFound { get; set; }
        public bool CreateConnectionTargetFound { get; set; }
        public bool Patched { get; set; }
        public string Target { get; set; }
        public bool RawAssemblyFound { get; set; }
        public bool EnableLoadExtensionMethodFound { get; set; }
        public int LoadAttempts { get; set; }
        public int LoadSuccesses { get; set; }
        public int LoadFailures { get; set; }
        public string LastConnectionType { get; set; }
        public string LastExtensionPath { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class AdvancedChineseSearchConnectionModState
    {
        public static AdvancedChineseSearchConnectionCapabilityStatus Status { get; internal set; } =
            new AdvancedChineseSearchConnectionCapabilityStatus();
    }

    /// <summary>
    /// Loads the configured simple tokenizer into each SQLite connection created after the patch is
    /// installed. Everything is runtime-reflected so the plugin keeps its 4.8 compile baseline.
    /// This class does not rebuild fts_search9; it only establishes the prerequisite that a pooled
    /// connection can understand the tokenizer after a guarded migration.
    /// </summary>
    public sealed class AdvancedChineseSearchConnectionRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.advanced-chinese-search-connections";
        private Harmony _harmony;

        public void Run()
        {
            var status = new AdvancedChineseSearchConnectionCapabilityStatus();
            AdvancedChineseSearchConnectionModState.Status = status;
            try
            {
                var assembly = TryLoadAssembly("Emby.Sqlite");
                var type = assembly?.GetType("Emby.Sqlite.BaseSqliteRepository", false);
                status.BaseRepositoryTypeFound = type != null;
                if (type == null) return;

                var target = type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(v => string.Equals(v.Name, "CreateConnection", StringComparison.Ordinal))
                    .OrderBy(v => v.GetParameters().Length)
                    .FirstOrDefault(v => v.GetParameters().Length <= 1);
                status.CreateConnectionTargetFound = target != null;
                status.Target = target?.ToString();
                if (target == null) return;

                AdvancedChineseSearchConnectionPatches.InitializeRawBridge(status);
                _harmony = new Harmony(HarmonyId);
                var postfix = typeof(AdvancedChineseSearchConnectionPatches).GetMethod(
                    nameof(AdvancedChineseSearchConnectionPatches.CreateConnectionPostfix),
                    BindingFlags.Public | BindingFlags.Static);
                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                status.Patched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Advanced Chinese search connection loader unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
        }

        private static Assembly TryLoadAssembly(string name)
        {
            try
            {
                return AppDomain.CurrentDomain.GetAssemblies()
                           .FirstOrDefault(v => string.Equals(v.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                       ?? Assembly.Load(name);
            }
            catch { return null; }
        }
    }

    public static class AdvancedChineseSearchConnectionPatches
    {
        private static MethodInfo _enableLoadExtension;
        private static Type _rawType;

        public static void InitializeRawBridge(AdvancedChineseSearchConnectionCapabilityStatus status)
        {
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                                   .FirstOrDefault(v => string.Equals(v.GetName().Name, "SQLitePCLRawEx.core", StringComparison.OrdinalIgnoreCase))
                               ?? TryLoad("SQLitePCLRawEx.core");
                _rawType = assembly?.GetType("SQLitePCLEx.raw", false);
                status.RawAssemblyFound = _rawType != null;
                _enableLoadExtension = _rawType?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(v => string.Equals(v.Name, "sqlite3_enable_load_extension", StringComparison.Ordinal) &&
                                         v.GetParameters().Length == 2);
                status.EnableLoadExtensionMethodFound = _enableLoadExtension != null;
            }
            catch (Exception ex)
            {
                status.LastError = ex.GetBaseException().Message;
            }
        }

        public static void CreateConnectionPostfix(object __result)
        {
            var status = AdvancedChineseSearchConnectionModState.Status;
            try
            {
                var options = AdvancedChineseSearchRuntimeSettings.GetSnapshot();
                if (!options.Enabled || __result == null) return;
                if (string.IsNullOrWhiteSpace(options.NativeExtensionPath) ||
                    !File.Exists(options.NativeExtensionPath)) return;

                status.LoadAttempts++;
                status.LastConnectionType = __result.GetType().FullName;
                status.LastExtensionPath = options.NativeExtensionPath;

                if (_enableLoadExtension == null)
                    InitializeRawBridge(status);
                if (_enableLoadExtension == null)
                    throw new InvalidOperationException("SQLitePCLEx.raw.sqlite3_enable_load_extension was not resolved.");

                var handle = FindNativeDbHandle(__result);
                if (handle == null)
                    throw new InvalidOperationException("The runtime SQLite connection does not expose a compatible native db handle.");

                var enableParameters = _enableLoadExtension.GetParameters();
                var enableArg = ConvertFlag(1, enableParameters[1].ParameterType);
                var enableResult = _enableLoadExtension.Invoke(null, new[] { handle, enableArg });
                if (enableResult != null && TryConvertInt(enableResult, out var rc) && rc != 0)
                    throw new InvalidOperationException("sqlite3_enable_load_extension returned " + rc + ".");

                ExecuteSql(__result, "SELECT load_extension('" + EscapeSqlLiteral(options.NativeExtensionPath) + "');");
                ExecuteSql(__result, "SELECT simple_query('中文搜索');");

                status.LoadSuccesses++;
                status.LastError = null;
            }
            catch (Exception ex)
            {
                status.LoadFailures++;
                status.LastError = ex.GetBaseException().Message;
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Advanced Chinese search connection extension load failed: " + status.LastError);
            }
        }

        private static object FindNativeDbHandle(object connection)
        {
            var type = connection?.GetType();
            while (type != null && type != typeof(object))
            {
                foreach (var name in new[] { "db", "_db", "Db", "Handle", "_handle" })
                {
                    try
                    {
                        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public |
                                                       BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        var value = field?.GetValue(connection);
                        if (value != null && IsCompatibleHandle(value)) return value;
                    }
                    catch { }
                }
                type = type.BaseType;
            }
            return null;
        }

        private static bool IsCompatibleHandle(object value)
        {
            if (value == null || _enableLoadExtension == null) return false;
            try
            {
                var expected = _enableLoadExtension.GetParameters()[0].ParameterType;
                return expected.IsInstanceOfType(value) || expected.IsAssignableFrom(value.GetType());
            }
            catch { return false; }
        }

        private static void ExecuteSql(object connection, string sql)
        {
            var method = FindExecuteMethod(connection?.GetType());
            if (method == null)
                throw new MissingMethodException("A runtime SQLite Execute(string) method was not found.");
            try
            {
                method.Invoke(connection, new object[] { sql });
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        private static MethodInfo FindExecuteMethod(Type type)
        {
            while (type != null && type != typeof(object))
            {
                var method = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                             BindingFlags.DeclaredOnly)
                    .FirstOrDefault(v => string.Equals(v.Name, "Execute", StringComparison.Ordinal) &&
                                         v.GetParameters().Length == 1 &&
                                         v.GetParameters()[0].ParameterType == typeof(string));
                if (method != null) return method;
                type = type.BaseType;
            }
            return null;
        }

        private static object ConvertFlag(int value, Type type)
        {
            if (type == typeof(int)) return value;
            if (type == typeof(uint)) return (uint)value;
            if (type == typeof(bool)) return value != 0;
            if (type.IsEnum) return Enum.ToObject(type, value);
            return Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool TryConvertInt(object value, out int result)
        {
            try
            {
                result = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                result = 0;
                return false;
            }
        }

        private static string EscapeSqlLiteral(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        private static Assembly TryLoad(string name)
        {
            try { return Assembly.Load(name); }
            catch { return null; }
        }
    }
}
