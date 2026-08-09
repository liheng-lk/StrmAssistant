using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace StrmAssistant.Compatibility
{
    public sealed class MediaInfoRuntimeFallbackCapabilityStatus
    {
        public bool TargetFound { get; set; }
        public bool Patched { get; set; }
        public bool ReflectionStaticMediaSourceAvailable { get; set; }
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
    /// Compatibility guard for Emby builds where IMediaSourceManager.GetStaticMediaSources
    /// changed its runtime signature. The fallback mirrors the tested Emby 4.10 compatibility
    /// strategy used by ODJ0930/StrmAssistant: discover the longest compatible overload and
    /// build its argument list according to the runtime parameter count.
    /// </summary>
    public sealed class MediaInfoRuntimeFallbackEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.mediainfo-fallback";
        private readonly ILibraryMonitor _libraryMonitor;
        private Harmony _harmony;

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
                var target = typeof(MediaInfoApi).GetMethod(
                    "GetStaticMediaSources",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(BaseItem), typeof(bool) },
                    null);

                status.TargetFound = target != null;
                status.Target = target?.ToString();
                if (target == null)
                {
                    status.Error = "MediaInfoApi.GetStaticMediaSources(BaseItem,bool) was not found.";
                    return;
                }

                var reflectedField = typeof(MediaInfoApi).GetField(
                    "_getStaticMediaSources",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                status.ReflectionStaticMediaSourceAvailable =
                    reflectedField?.GetValue(Plugin.MediaInfoApi) is MethodInfo;

                if (!status.ReflectionStaticMediaSourceAvailable)
                {
                    var mediaSourceManager = GetPrivateField<IMediaSourceManager>(Plugin.MediaInfoApi, "_mediaSourceManager");
                    MediaInfoRuntimeFallbackPatches.RuntimeStaticMediaSources = mediaSourceManager?.GetType()
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

                    status.RuntimeStaticMediaSourceTarget =
                        MediaInfoRuntimeFallbackPatches.RuntimeStaticMediaSources?.ToString();
                }

                status.LibraryMonitorIgnoreRuleApplied = TryApplyLibraryMonitorIgnoreRule(_libraryMonitor);

                _harmony = new Harmony(HarmonyId);
                var prefix = typeof(MediaInfoRuntimeFallbackPatches).GetMethod(
                    nameof(MediaInfoRuntimeFallbackPatches.GetStaticMediaSourcesPrefix),
                    BindingFlags.Static | BindingFlags.Public);
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                status.Patched = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("MediaInfo runtime fallback unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
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

                return TryAppendStringArrayField(monitorType, libraryMonitor, "_alwaysIgnoreSubstrings", "-mediainfo.json");
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
                    .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, name, StringComparison.Ordinal));
            }
        }
    }

    public static class MediaInfoRuntimeFallbackPatches
    {
        internal static MethodInfo RuntimeStaticMediaSources { get; set; }

        public static bool GetStaticMediaSourcesPrefix(MediaInfoApi __instance, BaseItem item,
            bool enableAlternateMediaSources, ref List<MediaSourceInfo> __result)
        {
            try
            {
                if (__instance == null || item == null)
                {
                    __result = new List<MediaSourceInfo>();
                    return false;
                }

                var originalReflectedField = typeof(MediaInfoApi).GetField(
                    "_getStaticMediaSources",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (originalReflectedField?.GetValue(__instance) is MethodInfo)
                    return true;

                var mediaSourceManager = GetPrivateField<IMediaSourceManager>(__instance, "_mediaSourceManager");
                var libraryManager = GetPrivateField<ILibraryManager>(__instance, "_libraryManager");
                var method = RuntimeStaticMediaSources;

                if (mediaSourceManager == null || libraryManager == null || method == null)
                {
                    Plugin.Instance?.Logger?.Warn("MediaInfo runtime fallback could not resolve the Emby 4.10 GetStaticMediaSources overload.");
                    __result = new List<MediaSourceInfo>();
                    return false;
                }

                var parameters = method.GetParameters();
                var libraryOptions = libraryManager.GetLibraryOptions(item);
                var collectionFolders = (BaseItem[])libraryManager.GetCollectionFolders(item);
                object[] args;

                switch (parameters.Length)
                {
                    case 10:
                        args = new object[]
                        {
                            item, enableAlternateMediaSources, false, true, true, collectionFolders,
                            libraryOptions, null, null, CancellationToken.None
                        };
                        break;
                    case 8:
                        args = new object[]
                        {
                            item, enableAlternateMediaSources, false, true, collectionFolders,
                            libraryOptions, null, null
                        };
                        break;
                    case 7:
                        args = new object[]
                        {
                            item, enableAlternateMediaSources, false, true, libraryOptions, null, null
                        };
                        break;
                    default:
                        Plugin.Instance?.Logger?.Warn("MediaInfo runtime fallback found an unsupported GetStaticMediaSources signature: " + method);
                        __result = new List<MediaSourceInfo>();
                        return false;
                }

                var result = method.Invoke(mediaSourceManager, args);
                if (result is List<MediaSourceInfo> list)
                    __result = list;
                else if (result is IEnumerable<MediaSourceInfo> enumerable)
                    __result = enumerable.ToList();
                else
                    __result = new List<MediaSourceInfo>();

                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("MediaInfo runtime fallback used {0} for {1}", method, item.Path);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("MediaInfo runtime fallback failed: " + ex.Message);
                __result = new List<MediaSourceInfo>();
                return false;
            }
        }

        private static T GetPrivateField<T>(object target, string fieldName) where T : class
        {
            return target?.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(target) as T;
        }
    }
}
