using HarmonyLib;
using MediaBrowser.Controller.Plugins;
using Microsoft.International.Converters.TraditionalChineseToSimplifiedConverter;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    /// <summary>
    /// Late compatibility pass for Emby builds where SqliteItemRepository.CreateSearchTerm
    /// changed from the older static one-string signature. Runs after RuntimeModEntryPoint,
    /// and only activates when the primary patch did not succeed.
    /// </summary>
    public sealed class ZZChineseSearchCompatibilityEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.chinese-search-late-fallback";
        private Harmony _harmony;

        public void Run()
        {
            try
            {
                if (RuntimeModState.Status?.CreateSearchTermPatched == true) return;

                var assembly = TryLoad("Emby.Server.Implementations");
                var type = assembly?.GetType("Emby.Server.Implementations.Data.SqliteItemRepository");
                var target = type?.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                               BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(method => string.Equals(method.Name, "CreateSearchTerm", StringComparison.Ordinal) &&
                                     method.ReturnType == typeof(string) &&
                                     method.GetParameters().Any(parameter => parameter.ParameterType == typeof(string)))
                    .OrderBy(method => method.GetParameters().Length)
                    .FirstOrDefault();

                if (target == null)
                {
                    target = type?.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                               BindingFlags.NonPublic | BindingFlags.Public)
                        .Where(method => method.ReturnType == typeof(string) &&
                                         method.Name.IndexOf("SearchTerm", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                         method.GetParameters().Any(parameter => parameter.ParameterType == typeof(string)))
                        .OrderBy(method => method.GetParameters().Length)
                        .FirstOrDefault();
                }

                if (RuntimeModState.Status != null)
                {
                    RuntimeModState.Status.CreateSearchTermTargetFound = target != null;
                    RuntimeModState.Status.CreateSearchTermTarget = target?.ToString();
                }

                if (target == null)
                {
                    Plugin.Instance?.Logger?.Warn("Chinese search fallback - no compatible SearchTerm target found on Emby 4.10 runtime.");
                    return;
                }

                FlexibleChineseSearchPatches.TargetMethod = target;
                _harmony = new Harmony(HarmonyId);
                var postfixName = target.IsStatic
                    ? nameof(FlexibleChineseSearchPatches.StaticPostfix)
                    : nameof(FlexibleChineseSearchPatches.InstancePostfix);
                var postfix = typeof(FlexibleChineseSearchPatches).GetMethod(
                    postfixName, BindingFlags.Static | BindingFlags.Public);
                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));

                if (RuntimeModState.Status != null)
                    RuntimeModState.Status.CreateSearchTermPatched = true;

                Plugin.Instance?.Logger?.Info("Chinese search fallback patched: " + target);
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Chinese search fallback unavailable: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
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

    public static class FlexibleChineseSearchPatches
    {
        [ThreadStatic]
        private static bool _buildingAlternative;

        internal static MethodInfo TargetMethod { get; set; }

        public static void StaticPostfix(object[] __args, ref string __result)
        {
            Apply(null, __args, ref __result);
        }

        public static void InstancePostfix(object __instance, object[] __args, ref string __result)
        {
            Apply(__instance, __args, ref __result);
        }

        private static void Apply(object instance, object[] args, ref string result)
        {
            if (_buildingAlternative || string.IsNullOrWhiteSpace(result) || TargetMethod == null || args == null)
                return;

            try
            {
                var options = Plugin.Instance?.GetPluginOptions()?.GeneralOptions;
                if (options?.EnableChineseSearchEnhance != true ||
                    options.EnableSimplifiedTraditionalSearch != true)
                    return;

                var parameters = TargetMethod.GetParameters();
                var stringIndex = -1;
                string input = null;
                for (var i = 0; i < parameters.Length && i < args.Length; i++)
                {
                    if (parameters[i].ParameterType != typeof(string) || !(args[i] is string value) ||
                        !ContainsCjkIdeograph(value)) continue;
                    stringIndex = i;
                    input = value;
                    break;
                }

                if (stringIndex < 0 || string.IsNullOrWhiteSpace(input)) return;

                var variants = new HashSet<string>(StringComparer.Ordinal) { input };
                TryAddVariant(variants, input, ChineseConversionDirection.TraditionalToSimplified);
                TryAddVariant(variants, input, ChineseConversionDirection.SimplifiedToTraditional);
                if (variants.Count <= 1) return;

                var searchTerms = new List<string> { result };
                foreach (var variant in variants.Where(value => !string.Equals(value, input, StringComparison.Ordinal)))
                {
                    var invokeArgs = (object[])args.Clone();
                    invokeArgs[stringIndex] = variant;

                    try
                    {
                        _buildingAlternative = true;
                        var alternative = TargetMethod.Invoke(TargetMethod.IsStatic ? null : instance, invokeArgs) as string;
                        if (!string.IsNullOrWhiteSpace(alternative) &&
                            !searchTerms.Contains(alternative, StringComparer.Ordinal))
                            searchTerms.Add(alternative);
                    }
                    finally
                    {
                        _buildingAlternative = false;
                    }
                }

                if (searchTerms.Count > 1)
                    result = string.Join(" OR ", searchTerms.Select(term => "(" + term + ")"));
            }
            catch (Exception ex)
            {
                _buildingAlternative = false;
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Chinese search fallback expansion skipped: " + ex.Message);
            }
        }

        private static void TryAddVariant(ISet<string> variants, string input,
            ChineseConversionDirection direction)
        {
            try
            {
                var converted = ChineseConverter.Convert(input, direction);
                if (!string.IsNullOrWhiteSpace(converted)) variants.Add(converted);
            }
            catch { }
        }

        private static bool ContainsCjkIdeograph(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            foreach (var ch in value)
            {
                if ((ch >= '\u3400' && ch <= '\u4DBF') ||
                    (ch >= '\u4E00' && ch <= '\u9FFF') ||
                    (ch >= '\uF900' && ch <= '\uFAFF'))
                    return true;
            }
            return false;
        }
    }
}
