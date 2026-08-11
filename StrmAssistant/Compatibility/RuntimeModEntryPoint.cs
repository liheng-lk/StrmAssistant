using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.International.Converters.TraditionalChineseToSimplifiedConverter;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class RuntimeModCapabilityStatus
    {
        public bool HarmonyLoaded { get; set; }
        public bool HttpHandlerTargetFound { get; set; }
        public bool HttpHandlerPatched { get; set; }
        public string HttpHandlerTarget { get; set; }
        public bool AttachPeopleTargetFound { get; set; }
        public bool AttachPeoplePatched { get; set; }
        public string AttachPeopleTarget { get; set; }
        public bool CreateSearchTermTargetFound { get; set; }
        public bool CreateSearchTermPatched { get; set; }
        public string CreateSearchTermTarget { get; set; }
        public string Error { get; set; }
    }

    public static class RuntimeModState
    {
        public static RuntimeModCapabilityStatus Status { get; internal set; } = new RuntimeModCapabilityStatus();
    }

    public sealed class RuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.runtime-mods";
        private Harmony _harmony;

        public void Run()
        {
            var status = new RuntimeModCapabilityStatus();
            RuntimeModState.Status = status;

            try
            {
                _harmony = new Harmony(HarmonyId);
                status.HarmonyLoaded = true;

                PatchHttpHandler(status);
                PatchAttachPeople(status);
                PatchCreateSearchTerm(status);
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("RuntimeModHost initialization failed: " + status.Error);
                if (Plugin.Instance?.DebugMode == true) Plugin.Instance.Logger.Debug(ex.StackTrace);
            }
        }

        public void Dispose()
        {
            try
            {
                _harmony?.UnpatchAll(HarmonyId);
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Debug("RuntimeModHost unpatch failed: " + ex.Message);
            }
        }

        private void PatchHttpHandler(RuntimeModCapabilityStatus status)
        {
            try
            {
                var assembly = Assembly.Load("Emby.Server.Implementations");
                var type = assembly.GetType("Emby.Server.Implementations.ApplicationHost");
                var target = type?.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(m => string.Equals(m.Name, "CreateHttpClientHandler", StringComparison.Ordinal))
                    .OrderBy(m => m.GetParameters().Length)
                    .FirstOrDefault(m => typeof(HttpMessageHandler).IsAssignableFrom(m.ReturnType));

                status.HttpHandlerTargetFound = target != null;
                status.HttpHandlerTarget = target?.ToString();
                if (target == null)
                {
                    Plugin.Instance?.Logger?.Warn("RuntimeModHost - CreateHttpClientHandler target not found; proxy enhancement disabled.");
                    return;
                }

                var postfix = typeof(RuntimeModPatches).GetMethod(nameof(RuntimeModPatches.HttpHandlerPostfix),
                    BindingFlags.Static | BindingFlags.Public);
                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                status.HttpHandlerPatched = true;
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("RuntimeModHost - proxy patch unavailable: " + ex.Message);
            }
        }

        private void PatchAttachPeople(RuntimeModCapabilityStatus status)
        {
            try
            {
                var assembly = Assembly.Load("Emby.Server.Implementations");
                var type = assembly.GetType("Emby.Server.Implementations.Dto.DtoService");
                var target = type?.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(m => string.Equals(m.Name, "AttachPeople", StringComparison.Ordinal))
                    .OrderByDescending(m => m.GetParameters().Length)
                    .FirstOrDefault(m => m.GetParameters().Any(p => p.ParameterType == typeof(BaseItemDto)) &&
                                         m.GetParameters().Any(p => typeof(BaseItem).IsAssignableFrom(p.ParameterType)));

                status.AttachPeopleTargetFound = target != null;
                status.AttachPeopleTarget = target?.ToString();
                if (target == null)
                {
                    Plugin.Instance?.Logger?.Warn("RuntimeModHost - DtoService.AttachPeople target not found; people display filter disabled.");
                    return;
                }

                var postfix = typeof(RuntimeModPatches).GetMethod(nameof(RuntimeModPatches.AttachPeoplePostfix),
                    BindingFlags.Static | BindingFlags.Public);
                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                status.AttachPeoplePatched = true;
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("RuntimeModHost - people patch unavailable: " + ex.Message);
            }
        }

        private void PatchCreateSearchTerm(RuntimeModCapabilityStatus status)
        {
            try
            {
                var assembly = Assembly.Load("Emby.Server.Implementations");
                var type = assembly.GetType("Emby.Server.Implementations.Data.SqliteItemRepository");
                var target = type?.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(m => string.Equals(m.Name, "CreateSearchTerm", StringComparison.Ordinal) &&
                                m.ReturnType == typeof(string))
                    .FirstOrDefault(m =>
                    {
                        var parameters = m.GetParameters();
                        return parameters.Length == 1 && parameters[0].ParameterType == typeof(string);
                    });

                status.CreateSearchTermTargetFound = target != null;
                status.CreateSearchTermTarget = target?.ToString();
                if (target == null)
                {
                    if (Plugin.Instance?.DebugMode == true)
                        Plugin.Instance.Logger.Debug(
                            "RuntimeModHost - legacy CreateSearchTerm(string) signature not found; late compatibility discovery will inspect alternate Emby signatures.");
                    return;
                }

                RuntimeModPatches.CreateSearchTermMethod = target;
                var postfix = typeof(RuntimeModPatches).GetMethod(nameof(RuntimeModPatches.CreateSearchTermPostfix),
                    BindingFlags.Static | BindingFlags.Public);
                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                status.CreateSearchTermPatched = true;
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("RuntimeModHost - Chinese search patch unavailable: " + ex.Message);
            }
        }
    }

    public static class RuntimeModPatches
    {
        [ThreadStatic]
        private static bool _buildingSearchAlternative;

        internal static MethodInfo CreateSearchTermMethod { get; set; }

        public static void HttpHandlerPostfix(ref HttpMessageHandler __result)
        {
            try
            {
                var options = Plugin.Instance?.GetPluginOptions()?.GeneralOptions;
                if (options?.EnableProxyServerEnhance != true || __result == null) return;
                if (!Uri.TryCreate(options.ProxyServerUrl?.Trim(), UriKind.Absolute, out var proxyUri)) return;
                if (proxyUri.Scheme != Uri.UriSchemeHttp && proxyUri.Scheme != Uri.UriSchemeHttps) return;

                var proxy = new SelectiveWebProxy(proxyUri, options.ProxyMode,
                    options.ProxyWhitelistDomains, options.ProxyBypassHosts, options.ProxyLocalDiscoveryAddress);

                if (__result is HttpClientHandler httpClientHandler)
                {
                    httpClientHandler.Proxy = proxy;
                    httpClientHandler.UseProxy = true;
                    return;
                }

                var handlerType = __result.GetType();
                var proxyProperty = handlerType.GetProperty("Proxy", BindingFlags.Instance | BindingFlags.Public);
                var useProxyProperty = handlerType.GetProperty("UseProxy", BindingFlags.Instance | BindingFlags.Public);
                if (proxyProperty?.CanWrite == true && typeof(IWebProxy).IsAssignableFrom(proxyProperty.PropertyType))
                    proxyProperty.SetValue(__result, proxy);
                if (useProxyProperty?.CanWrite == true && useProxyProperty.PropertyType == typeof(bool))
                    useProxyProperty.SetValue(__result, true);
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Proxy enhancement postfix failed: " + ex.Message);
            }
        }

        public static void AttachPeoplePostfix(object[] __args)
        {
            try
            {
                var options = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
                if (options?.EnablePeopleDisplayFilter != true || __args == null) return;

                var dto = __args.OfType<BaseItemDto>().FirstOrDefault();
                var item = __args.OfType<BaseItem>().FirstOrDefault();
                if (dto?.People == null || item == null) return;
                if (!(item is Movie) && !(item is Series) && !(item is Season) && !(item is Episode)) return;

                var filtered = dto.People.AsEnumerable();
                if (options.HidePeopleWithoutImage)
                    filtered = filtered.Where(p => p?.HasPrimaryImage == true);

                if (options.ShowActorsOnly)
                    filtered = filtered.Where(p => p != null &&
                        (p.Type == PersonType.Actor || p.Type == PersonType.GuestStar));

                if (options.HideNonChinesePeopleNames)
                    filtered = filtered.Where(p => p != null && ContainsCjkIdeograph(p.Name));

                dto.People = filtered.ToArray();
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("People display filter postfix failed: " + ex.Message);
            }
        }

        public static void CreateSearchTermPostfix(object[] __args, ref string __result)
        {
            if (_buildingSearchAlternative || string.IsNullOrWhiteSpace(__result)) return;

            try
            {
                var options = Plugin.Instance?.GetPluginOptions()?.GeneralOptions;
                if (options?.EnableChineseSearchEnhance != true ||
                    options.EnableSimplifiedTraditionalSearch != true || __args == null) return;

                var input = __args.OfType<string>().FirstOrDefault();
                if (!ContainsCjkIdeograph(input) || CreateSearchTermMethod == null) return;

                var variants = new HashSet<string>(StringComparer.Ordinal)
                {
                    input
                };

                TryAddChineseVariant(variants, input, ChineseConversionDirection.TraditionalToSimplified);
                TryAddChineseVariant(variants, input, ChineseConversionDirection.SimplifiedToTraditional);
                if (variants.Count <= 1) return;

                var searchTerms = new List<string> { __result };
                foreach (var variant in variants.Where(v => !string.Equals(v, input, StringComparison.Ordinal)))
                {
                    try
                    {
                        _buildingSearchAlternative = true;
                        var alternative = CreateSearchTermMethod.Invoke(null, new object[] { variant }) as string;
                        if (!string.IsNullOrWhiteSpace(alternative) &&
                            !searchTerms.Contains(alternative, StringComparer.Ordinal))
                            searchTerms.Add(alternative);
                    }
                    finally
                    {
                        _buildingSearchAlternative = false;
                    }
                }

                if (searchTerms.Count > 1)
                    __result = string.Join(" OR ", searchTerms.Select(term => "(" + term + ")"));
            }
            catch (Exception ex)
            {
                _buildingSearchAlternative = false;
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Chinese search expansion skipped: " + ex.Message);
            }
        }

        private static void TryAddChineseVariant(ISet<string> variants, string input,
            ChineseConversionDirection direction)
        {
            try
            {
                var converted = ChineseConverter.Convert(input, direction);
                if (!string.IsNullOrWhiteSpace(converted)) variants.Add(converted);
            }
            catch
            {
                // A failed conversion must never affect the original search term.
            }
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

    public sealed class SelectiveWebProxy : IWebProxy
    {
        private readonly Uri _proxyUri;
        private readonly GeneralOptions.ProxyRoutingMode _mode;
        private readonly string[] _whitelist;
        private readonly string[] _bypass;

        public SelectiveWebProxy(Uri proxyUri, GeneralOptions.ProxyRoutingMode mode,
            string whitelist, string bypass, string localDiscoveryAddress)
        {
            _proxyUri = proxyUri ?? throw new ArgumentNullException(nameof(proxyUri));
            _mode = mode;
            _whitelist = SplitPatterns(whitelist);
            _bypass = SplitPatterns((bypass ?? string.Empty) + "," + ExtractHost(localDiscoveryAddress));

            if (!string.IsNullOrWhiteSpace(proxyUri.UserInfo))
            {
                var parts = proxyUri.UserInfo.Split(new[] { ':' }, 2);
                var username = Uri.UnescapeDataString(parts[0]);
                var password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
                Credentials = new NetworkCredential(username, password);
            }
        }

        public ICredentials Credentials { get; set; }

        public Uri GetProxy(Uri destination)
        {
            return IsBypassed(destination) ? destination : _proxyUri;
        }

        public bool IsBypassed(Uri host)
        {
            if (host == null) return true;
            var hostname = host.DnsSafeHost;
            if (string.IsNullOrWhiteSpace(hostname)) return true;
            if (IsLocalOrPrivate(hostname)) return true;
            if (_bypass.Any(pattern => HostMatches(hostname, pattern))) return true;

            if (_mode == GeneralOptions.ProxyRoutingMode.Whitelist)
                return !_whitelist.Any(pattern => HostMatches(hostname, pattern));

            return false;
        }

        private static bool IsLocalOrPrivate(string host)
        {
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (!IPAddress.TryParse(host, out var address)) return false;
            if (IPAddress.IsLoopback(address)) return true;

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                if (bytes[0] == 10) return true;
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                if (bytes[0] == 192 && bytes[1] == 168) return true;
                if (bytes[0] == 169 && bytes[1] == 254) return true;
                return false;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
                return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;

            return false;
        }

        private static bool HostMatches(string host, string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return false;
            pattern = pattern.Trim().Trim('.');
            if (pattern.StartsWith("*.", StringComparison.Ordinal)) pattern = pattern.Substring(2);

            return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith("." + pattern, StringComparison.OrdinalIgnoreCase);
        }

        private static string[] SplitPatterns(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => ExtractHost(v.Trim()))
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string ExtractHost(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            value = value.Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) return uri.DnsSafeHost;
            var colon = value.LastIndexOf(':');
            if (colon > 0 && value.IndexOf(':') == colon && int.TryParse(value.Substring(colon + 1), out _))
                value = value.Substring(0, colon);
            return value.Trim().Trim('[', ']', '.');
        }
    }
}
