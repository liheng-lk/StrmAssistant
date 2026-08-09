using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class CopiedUserNotificationCleanupCapabilityStatus
    {
        public bool CloneCreateUserTargetFound { get; set; }
        public bool Patched { get; set; }
        public string Target { get; set; }
        public List<string> DiscoveredNotificationSettingKeys { get; set; } = new List<string>();
        public string LastCloneSourceUser { get; set; }
        public string LastCloneTargetUser { get; set; }
        public string LastCloneTargetUserId { get; set; }
        public List<string> LastCopyOptions { get; set; } = new List<string>();
        public List<string> LastResetKeys { get; set; } = new List<string>();
        public bool LastCleanupSucceeded { get; set; }
        public string Error { get; set; }
        public string LastCleanupError { get; set; }
    }

    public static class CopiedUserNotificationCleanupModState
    {
        public static CopiedUserNotificationCleanupCapabilityStatus Status { get; internal set; } =
            new CopiedUserNotificationCleanupCapabilityStatus();
    }

    /// <summary>
    /// Resets only per-user notification settings after Emby's explicit clone-user CreateUser
    /// overload succeeds. Normal CreateUser(name, policy) is never patched. The clone overload is
    /// discovered at runtime so Emby 4.8 remains compile-compatible when UserCopyOptions is absent.
    /// Notification configuration keys are also discovered from registered IUserConfigurationFactory
    /// instances; no file paths or guessed setting keys are deleted.
    /// </summary>
    public sealed class CopiedUserNotificationCleanupRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.copied-user-notification-cleanup";
        private readonly IUserManager _userManager;
        private Harmony _harmony;

        public CopiedUserNotificationCleanupRuntimeModEntryPoint(IUserManager userManager)
        {
            _userManager = userManager;
        }

        public void Run()
        {
            var status = new CopiedUserNotificationCleanupCapabilityStatus();
            CopiedUserNotificationCleanupModState.Status = status;
            try
            {
                var target = FindCloneCreateUserMethod(_userManager?.GetType());
                status.CloneCreateUserTargetFound = target != null;
                status.Target = target?.ToString();
                status.DiscoveredNotificationSettingKeys = DiscoverNotificationStores(_userManager)
                    .Select(v => v.Key)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (target == null) return;

                _harmony = new Harmony(HarmonyId);
                var postfix = typeof(CopiedUserNotificationCleanupPatches).GetMethod(
                    nameof(CopiedUserNotificationCleanupPatches.CloneCreateUserPostfix),
                    BindingFlags.Public | BindingFlags.Static);
                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                status.Patched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Copied-user notification cleanup patch unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch { }
        }

        internal static MethodInfo FindCloneCreateUserMethod(Type managerType)
        {
            if (managerType == null) return null;
            return managerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(v => string.Equals(v.Name, "CreateUser", StringComparison.Ordinal) ||
                            v.Name.EndsWith(".CreateUser", StringComparison.Ordinal))
                .FirstOrDefault(v =>
                {
                    var parameters = v.GetParameters();
                    if (parameters.Length != 3 || parameters[0].ParameterType != typeof(string) ||
                        !typeof(User).IsAssignableFrom(parameters[1].ParameterType)) return false;
                    if (!parameters[2].ParameterType.IsArray) return false;
                    var elementType = parameters[2].ParameterType.GetElementType();
                    if (elementType == null ||
                        !string.Equals(elementType.Name, "UserCopyOptions", StringComparison.Ordinal)) return false;
                    return v.ReturnType.IsGenericType &&
                           v.ReturnType.GetGenericTypeDefinition() == typeof(Task<>) &&
                           typeof(User).IsAssignableFrom(v.ReturnType.GetGenericArguments()[0]);
                });
        }

        internal static List<NotificationSettingStore> DiscoverNotificationStores(object userManager)
        {
            var result = new List<NotificationSettingStore>();
            if (userManager == null) return result;

            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var field in GetAllFields(userManager.GetType()))
            {
                if (!LooksLikeFactoryContainer(field.Name, field.FieldType)) continue;
                try { ScanFactoryValue(field.GetValue(userManager), result, visited, 0); }
                catch { }
            }

            foreach (var property in GetAllProperties(userManager.GetType()))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0 ||
                    !LooksLikeFactoryContainer(property.Name, property.PropertyType)) continue;
                try { ScanFactoryValue(property.GetValue(userManager), result, visited, 0); }
                catch { }
            }

            return result
                .Where(v => v != null && !string.IsNullOrWhiteSpace(v.Key) && v.ConfigurationType != null)
                .GroupBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
                .Select(v => v.First())
                .ToList();
        }

        private static bool LooksLikeFactoryContainer(string name, Type type)
        {
            var text = (name ?? string.Empty) + " " + (type?.FullName ?? string.Empty);
            return text.IndexOf("config", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("factor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("part", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ScanFactoryValue(object value, List<NotificationSettingStore> result,
            HashSet<object> visited, int depth)
        {
            if (value == null || depth > 2) return;
            if (!(value is string) && !value.GetType().IsValueType)
            {
                if (!visited.Add(value)) return;
            }

            var type = value.GetType();
            if (ImplementsUserConfigurationFactory(type))
            {
                ReadFactoryConfigurations(value, result);
                return;
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                var count = 0;
                foreach (var item in enumerable)
                {
                    if (++count > 256) break;
                    ScanFactoryValue(item, result, visited, depth + 1);
                }
            }
        }

        private static bool ImplementsUserConfigurationFactory(Type type)
        {
            try
            {
                return type.GetInterfaces().Any(v => string.Equals(v.FullName,
                    "MediaBrowser.Controller.Configuration.IUserConfigurationFactory",
                    StringComparison.Ordinal));
            }
            catch { return false; }
        }

        private static void ReadFactoryConfigurations(object factory, List<NotificationSettingStore> result)
        {
            try
            {
                var method = factory.GetType().GetMethod("GetConfigurations",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (!(method?.Invoke(factory, null) is IEnumerable stores)) return;

                foreach (var store in stores)
                {
                    if (store == null) continue;
                    var type = store.GetType();
                    var key = Convert.ToString(type.GetProperty("Key",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(store));
                    var configurationType = type.GetProperty("ConfigurationType",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(store) as Type;
                    if (!IsNotificationStore(key, configurationType)) continue;
                    result.Add(new NotificationSettingStore { Key = key, ConfigurationType = configurationType });
                }
            }
            catch
            {
                // A third-party configuration factory must never block user creation.
            }
        }

        private static bool IsNotificationStore(string key, Type configurationType)
        {
            var text = (key ?? string.Empty) + " " + (configurationType?.FullName ?? string.Empty);
            return text.IndexOf("notification", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("notifier", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IEnumerable<FieldInfo> GetAllFields(Type type)
        {
            while (type != null && type != typeof(object))
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    yield return field;
                type = type.BaseType;
            }
        }

        private static IEnumerable<PropertyInfo> GetAllProperties(Type type)
        {
            while (type != null && type != typeof(object))
            {
                foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public |
                                                            BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    yield return property;
                type = type.BaseType;
            }
        }
    }

    public sealed class NotificationSettingStore
    {
        public string Key { get; set; }
        public Type ConfigurationType { get; set; }
    }

    public static class CopiedUserNotificationCleanupPatches
    {
        public static void CloneCreateUserPostfix(object __instance, object[] __args, ref Task<User> __result)
        {
            try
            {
                var options = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
                if (options?.EnableNotificationEnhance != true ||
                    options.ClearCopiedUserNotificationSettings != true || __result == null) return;

                var source = __args?.OfType<User>().FirstOrDefault();
                var copyOptions = ReadCopyOptionNames(__args);
                __result = CompleteCloneAndResetAsync(__result, __instance, source, copyOptions);
            }
            catch (Exception ex)
            {
                CopiedUserNotificationCleanupModState.Status.LastCleanupError = ex.GetBaseException().Message;
                Plugin.Instance?.Logger?.Warn("Unable to wrap cloned-user notification cleanup: " + ex.GetBaseException().Message);
            }
        }

        private static async Task<User> CompleteCloneAndResetAsync(Task<User> originalTask, object userManager,
            User sourceUser, List<string> copyOptions)
        {
            var newUser = await originalTask.ConfigureAwait(false);
            var status = CopiedUserNotificationCleanupModState.Status;
            status.LastCleanupSucceeded = false;
            status.LastCleanupError = null;
            status.LastResetKeys = new List<string>();
            status.LastCopyOptions = copyOptions ?? new List<string>();
            status.LastCloneSourceUser = sourceUser?.Name;
            status.LastCloneTargetUser = newUser?.Name;
            status.LastCloneTargetUserId = newUser?.InternalId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (newUser == null || userManager == null) return newUser;

            try
            {
                var stores = CopiedUserNotificationCleanupRuntimeModEntryPoint.DiscoverNotificationStores(userManager);
                status.DiscoveredNotificationSettingKeys = stores.Select(v => v.Key)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (stores.Count == 0)
                {
                    status.LastCleanupError = "No unambiguous notification/notifier user configuration store was discovered; no settings were modified.";
                    return newUser;
                }

                var setter = FindSetTypedUserSetting(userManager.GetType());
                if (setter == null)
                {
                    status.LastCleanupError = "SetTypedUserSetting(long,string,object) was not found; no settings were modified.";
                    return newUser;
                }

                foreach (var store in stores)
                {
                    if (store.ConfigurationType == null || store.ConfigurationType.IsAbstract ||
                        store.ConfigurationType.IsInterface) continue;
                    object defaultConfiguration;
                    try { defaultConfiguration = Activator.CreateInstance(store.ConfigurationType); }
                    catch { continue; }
                    if (defaultConfiguration == null) continue;

                    setter.Invoke(userManager, new object[] { newUser.InternalId, store.Key, defaultConfiguration });
                    status.LastResetKeys.Add(store.Key);
                }

                status.LastCleanupSucceeded = status.LastResetKeys.Count > 0;
                if (!status.LastCleanupSucceeded && string.IsNullOrWhiteSpace(status.LastCleanupError))
                    status.LastCleanupError = "Notification configuration stores were discovered but none had a safe default constructor; no settings were modified.";
            }
            catch (Exception ex)
            {
                status.LastCleanupError = ex.GetBaseException().Message;
                Plugin.Instance?.Logger?.Warn("Copied-user notification cleanup failed for {0}: {1}",
                    newUser.Name, status.LastCleanupError);
            }

            return newUser;
        }

        private static MethodInfo FindSetTypedUserSetting(Type type)
        {
            return type?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(v =>
                {
                    if (!string.Equals(v.Name, "SetTypedUserSetting", StringComparison.Ordinal) &&
                        !v.Name.EndsWith(".SetTypedUserSetting", StringComparison.Ordinal)) return false;
                    var p = v.GetParameters();
                    return p.Length == 3 && p[0].ParameterType == typeof(long) &&
                           p[1].ParameterType == typeof(string) && p[2].ParameterType == typeof(object);
                });
        }

        private static List<string> ReadCopyOptionNames(object[] args)
        {
            var result = new List<string>();
            if (args == null) return result;
            foreach (var arg in args)
            {
                if (!(arg is Array array)) continue;
                var elementType = array.GetType().GetElementType();
                if (elementType == null || !string.Equals(elementType.Name, "UserCopyOptions", StringComparison.Ordinal)) continue;
                foreach (var value in array)
                {
                    var name = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
                }
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
