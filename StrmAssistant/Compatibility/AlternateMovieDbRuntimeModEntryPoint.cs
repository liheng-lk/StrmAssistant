using HarmonyLib;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Plugins;
using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace StrmAssistant.Compatibility
{
    public sealed class AlternateMovieDbCapabilityStatus
    {
        public bool MovieDbAssemblyLoaded { get; set; }
        public string MovieDbAssemblyVersion { get; set; }
        public bool ApiRequestTargetFound { get; set; }
        public bool ApiRequestPatched { get; set; }
        public string ApiRequestTarget { get; set; }
        public bool ProviderImageTargetFound { get; set; }
        public bool ProviderImagePatched { get; set; }
        public string ProviderImageTarget { get; set; }
        public bool RemoteImageTargetFound { get; set; }
        public bool RemoteImagePatched { get; set; }
        public string RemoteImageTarget { get; set; }
        public string SystemApiKeyDetected { get; set; }
        public string Error { get; set; }
    }

    public static class AlternateMovieDbModState
    {
        public static AlternateMovieDbCapabilityStatus Status { get; internal set; } =
            new AlternateMovieDbCapabilityStatus();
    }

    /// <summary>
    /// Rewrites only MovieDb/TMDB request URLs. It does not replace providers, system proxy settings,
    /// or metadata storage. All patches read current options and become no-ops when disabled.
    /// </summary>
    public sealed class AlternateMovieDbRuntimeModEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.moviedb-alt";
        private Harmony _harmony;
        private ResolveEventHandler _movieDbResolveHandler;

        public void Run()
        {
            var status = new AlternateMovieDbCapabilityStatus();
            AlternateMovieDbModState.Status = status;

            try
            {
                var movieDbAssembly = TryLoad("MovieDb");
                if (movieDbAssembly == null)
                {
                    status.Error = "MovieDb plugin assembly is not loaded.";
                    return;
                }

                // Emby 4.10 can load plugin assemblies into a context where a later Assembly.Load("MovieDb")
                // does not resolve by simple name even though the assembly is already present. Keep a narrow
                // AppDomain resolver in place so the other runtime compatibility modules can bind to the
                // exact MovieDb assembly Emby has already loaded, without loading a second copy.
                _movieDbResolveHandler = (sender, args) =>
                {
                    try
                    {
                        var requested = new AssemblyName(args.Name).Name;
                        return string.Equals(requested, "MovieDb", StringComparison.OrdinalIgnoreCase)
                            ? movieDbAssembly
                            : null;
                    }
                    catch
                    {
                        return null;
                    }
                };
                AppDomain.CurrentDomain.AssemblyResolve += _movieDbResolveHandler;

                status.MovieDbAssemblyLoaded = true;
                status.MovieDbAssemblyVersion = movieDbAssembly.GetName().Version?.ToString();

                var providerBase = movieDbAssembly.GetType("MovieDb.MovieDbProviderBase");
                var apiRequestTarget = providerBase?.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .FirstOrDefault(m => string.Equals(m.Name, "GetMovieDbResponse", StringComparison.Ordinal) &&
                                         m.GetParameters().Any(p => p.ParameterType == typeof(HttpRequestOptions)));
                var apiKeyField = providerBase?.GetField("ApiKey", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                var systemApiKey = apiKeyField?.GetValue(null) as string;
                AlternateMovieDbPatches.SystemApiKey = systemApiKey;
                status.SystemApiKeyDetected = string.IsNullOrWhiteSpace(systemApiKey) ? null : "present";
                status.ApiRequestTargetFound = apiRequestTarget != null;
                status.ApiRequestTarget = apiRequestTarget?.ToString();

                var embyProviders = TryLoad("Emby.Providers");
                var providerManager = embyProviders?.GetType("Emby.Providers.Manager.ProviderManager");
                var providerImageTarget = providerManager?.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .FirstOrDefault(m => string.Equals(m.Name, "SaveImageFromRemoteUrl", StringComparison.Ordinal) &&
                                         m.GetParameters().Any(p => p.ParameterType == typeof(string) &&
                                                                    string.Equals(p.Name, "url", StringComparison.OrdinalIgnoreCase)));
                status.ProviderImageTargetFound = providerImageTarget != null;
                status.ProviderImageTarget = providerImageTarget?.ToString();

                var embyApi = TryLoad("Emby.Api");
                var remoteImageService = embyApi?.GetType("Emby.Api.Images.RemoteImageService");
                var remoteImageTarget = remoteImageService?.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .FirstOrDefault(m => string.Equals(m.Name, "DownloadImage", StringComparison.Ordinal) &&
                                         m.GetParameters().Any(p => p.ParameterType == typeof(string) &&
                                                                    string.Equals(p.Name, "url", StringComparison.OrdinalIgnoreCase)));
                status.RemoteImageTargetFound = remoteImageTarget != null;
                status.RemoteImageTarget = remoteImageTarget?.ToString();

                _harmony = new Harmony(HarmonyId);
                if (apiRequestTarget != null)
                {
                    var prefix = typeof(AlternateMovieDbPatches).GetMethod(
                        nameof(AlternateMovieDbPatches.GetMovieDbResponsePrefix),
                        BindingFlags.Static | BindingFlags.Public);
                    _harmony.Patch(apiRequestTarget, prefix: new HarmonyMethod(prefix));
                    status.ApiRequestPatched = true;
                }

                if (providerImageTarget != null)
                {
                    var prefix = typeof(AlternateMovieDbPatches).GetMethod(
                        nameof(AlternateMovieDbPatches.RemoteImageUrlPrefix),
                        BindingFlags.Static | BindingFlags.Public);
                    _harmony.Patch(providerImageTarget, prefix: new HarmonyMethod(prefix));
                    status.ProviderImagePatched = true;
                }

                if (remoteImageTarget != null)
                {
                    var prefix = typeof(AlternateMovieDbPatches).GetMethod(
                        nameof(AlternateMovieDbPatches.RemoteImageUrlPrefix),
                        BindingFlags.Static | BindingFlags.Public);
                    _harmony.Patch(remoteImageTarget, prefix: new HarmonyMethod(prefix));
                    status.RemoteImagePatched = true;
                }
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Alternate MovieDb runtime mod unavailable: " + status.Error);
            }
        }

        public void Dispose()
        {
            try
            {
                _harmony?.UnpatchAll(HarmonyId);
            }
            catch
            {
                // Best effort during plugin shutdown.
            }

            try
            {
                if (_movieDbResolveHandler != null)
                    AppDomain.CurrentDomain.AssemblyResolve -= _movieDbResolveHandler;
            }
            catch
            {
                // Best effort during plugin shutdown.
            }
        }

        private static Assembly TryLoad(string name)
        {
            try
            {
                return Assembly.Load(name);
            }
            catch
            {
                return AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.Ordinal));
            }
        }
    }

    public static class AlternateMovieDbPatches
    {
        private const string DefaultApiRoot = "https://api.themoviedb.org";
        private const string DefaultImageRoot = "https://image.tmdb.org";
        private static readonly Regex ApiKeyRegex = new Regex("^[a-fA-F0-9]{32}$", RegexOptions.Compiled);
        private static readonly Regex ApiKeyQueryRegex =
            new Regex("([?&]api_key=)[^&]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal static string SystemApiKey { get; set; }

        public static void GetMovieDbResponsePrefix(object[] __args)
        {
            try
            {
                var options = Plugin.Instance?.GetPluginOptions()?.MetadataEnhanceOptions;
                if (options?.EnableAlternateMovieDbConfig != true || __args == null) return;

                var request = __args.OfType<HttpRequestOptions>().FirstOrDefault();
                if (request == null || string.IsNullOrWhiteSpace(request.Url)) return;

                var url = request.Url;
                if (TryGetHttpRoot(options.AlternateMovieDbApiUrl, out var alternateApiRoot) &&
                    url.StartsWith(DefaultApiRoot, StringComparison.OrdinalIgnoreCase))
                {
                    url = alternateApiRoot + url.Substring(DefaultApiRoot.Length);
                }

                var configuredKey = options.AlternateMovieDbApiKey?.Trim();
                if (IsValidApiKey(configuredKey))
                {
                    if (!string.IsNullOrWhiteSpace(SystemApiKey))
                        url = url.Replace(SystemApiKey, configuredKey);
                    else if (ApiKeyQueryRegex.IsMatch(url))
                        url = ApiKeyQueryRegex.Replace(url, "$1" + configuredKey, 1);
                }

                request.Url = url;
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Alternate MovieDb API rewrite skipped: " + ex.Message);
            }
        }

        public static void RemoteImageUrlPrefix(object[] __args)
        {
            try
            {
                var options = Plugin.Instance?.GetPluginOptions()?.MetadataEnhanceOptions;
                if (options?.EnableAlternateMovieDbConfig != true || __args == null) return;
                if (!TryGetHttpRoot(options.AlternateMovieDbImageUrl, out var alternateImageRoot)) return;

                for (var i = 0; i < __args.Length; i++)
                {
                    if (!(__args[i] is string url) ||
                        !url.StartsWith(DefaultImageRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    __args[i] = alternateImageRoot + url.Substring(DefaultImageRoot.Length);
                    break;
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Alternate MovieDb image rewrite skipped: " + ex.Message);
            }
        }

        private static bool IsValidApiKey(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && ApiKeyRegex.IsMatch(value);
        }

        private static bool TryGetHttpRoot(string value, out string root)
        {
            root = null;
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return false;

            root = value.Trim().TrimEnd('/');
            return true;
        }
    }
}
