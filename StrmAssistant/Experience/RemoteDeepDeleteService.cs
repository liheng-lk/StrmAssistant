using MediaBrowser.Controller.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Experience
{
    public sealed class RemoteDeepDeletePlan
    {
        public bool Applicable { get; set; }
        public bool Allowed { get; set; }
        public bool TargetLooksRemote { get; set; }
        public string Provider { get; set; }
        public string SourceTarget { get; set; }
        public string MatchedSourcePrefix { get; set; }
        public string RemotePath { get; set; }
        public string RemoteDirectory { get; set; }
        public string RemoteName { get; set; }
        public string EndpointHost { get; set; }
        public string Error { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class RemoteDeepDeleteProbeResult
    {
        public bool Success { get; set; }
        public bool Exists { get; set; }
        public bool Missing { get; set; }
        public int HttpStatusCode { get; set; }
        public int? ApiCode { get; set; }
        public string Provider { get; set; }
        public string RemotePath { get; set; }
        public string Error { get; set; }
    }

    public sealed class RemoteDeepDeleteExecutionResult
    {
        public bool Success { get; set; }
        public bool DeleteAccepted { get; set; }
        public bool VerifiedDeleted { get; set; }
        public bool AlreadyMissing { get; set; }
        public bool PreProbeVerifiedExists { get; set; }
        public bool PreProbeAlreadyMissing { get; set; }
        public int PreProbeStatusCode { get; set; }
        public string PreProbeError { get; set; }
        public int HttpStatusCode { get; set; }
        public int VerificationStatusCode { get; set; }
        public string Provider { get; set; }
        public string RemotePath { get; set; }
        public string VerificationError { get; set; }
        public string Error { get; set; }
    }

    public sealed class RemoteDeepDeleteService
    {
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false
        });

        // Contract tests replace only the final transport boundary. Production never assigns this.
        // Request construction, auth headers, bodies, state transitions and response parsing remain
        // the exact production code paths under test.
        internal static Func<HttpRequestMessage, HttpCompletionOption, CancellationToken,
            Task<HttpResponseMessage>> SendAsyncOverride { get; set; }

        public RemoteDeepDeletePlan BuildPlan(BaseItem item)
        {
            var options = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            var plan = new RemoteDeepDeletePlan { Provider = options.Provider.ToString() };

            if (!options.Enabled || options.Provider == RemoteDeepDeleteProviderType.None)
            {
                plan.Error = "Remote deep delete is disabled.";
                return plan;
            }
            if (item == null || string.IsNullOrWhiteSpace(item.Path))
            {
                plan.Error = "The Emby item has no source path.";
                return plan;
            }

            var target = ResolveTarget(item);
            if (string.IsNullOrWhiteSpace(target))
            {
                plan.Error = "No STRM/symlink target could be resolved.";
                return plan;
            }

            plan.SourceTarget = RedactQuery(target);
            plan.TargetLooksRemote = IsHttpTarget(target);
            var mappings = RemoteDeepDeleteRuntimeSettings.ParseMappings(options.PathMappings);
            var targetWithoutQuery = StripQueryAndFragment(target);
            RemotePathMapping mapping = null;
            string suffix = null;
            foreach (var candidate in mappings)
            {
                if (!TryMatchHttpMapping(targetWithoutQuery, candidate.SourcePrefix, out var candidateSuffix))
                    continue;
                mapping = candidate;
                suffix = candidateSuffix;
                break;
            }

            if (mapping == null)
            {
                plan.Applicable = plan.TargetLooksRemote;
                plan.Error = "The resolved media target did not match any configured remote path mapping.";
                if (plan.TargetLooksRemote)
                    plan.Warnings.Add("Remote URL detected. Configure a SourcePrefix => RemoteRoot mapping before destructive execution.");
                return plan;
            }

            plan.Applicable = true;
            var remotePath = RemoteDeepDeleteRuntimeSettings.NormalizeRemotePath(
                mapping.RemoteRoot.TrimEnd('/') + "/" + (suffix ?? string.Empty).Replace('\\', '/'));
            if (remotePath == null || remotePath == "/")
            {
                plan.Error = "The mapping resolved to an invalid/root remote path.";
                return plan;
            }

            plan.MatchedSourcePrefix = mapping.SourcePrefix;
            plan.RemotePath = remotePath;
            plan.RemoteDirectory = PosixDirName(remotePath);
            plan.RemoteName = PosixBaseName(remotePath);
            plan.EndpointHost = SafeHost(options.BaseUrl);

            var allowedRoots = RemoteDeepDeleteRuntimeSettings.ParseAllowedRoots(options.AllowedRemoteRoots);
            if (!RemoteDeepDeleteRuntimeSettings.IsWithinAllowedRoot(remotePath, allowedRoots))
            {
                plan.Error = allowedRoots.Count == 0
                    ? "No allowed remote roots are configured. Remote deletion is blocked."
                    : "The mapped remote path is outside all configured allowed remote roots.";
                return plan;
            }
            if (string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                plan.Error = "Remote provider BaseUrl is empty or invalid.";
                return plan;
            }
            if (options.Provider == RemoteDeepDeleteProviderType.OpenList &&
                string.IsNullOrWhiteSpace(options.AccessToken))
            {
                plan.Error = "OpenList AccessToken is empty. Anonymous destructive calls are not permitted by this plugin.";
                return plan;
            }

            plan.Allowed = true;
            if (item.IsShortcut)
                plan.Warnings.Add("The local STRM/Emby item is removed only after the remote provider deletion is verified.");
            return plan;
        }

        public async Task<RemoteDeepDeleteProbeResult> ProbeAsync(RemoteDeepDeletePlan plan,
            CancellationToken cancellationToken)
        {
            var options = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            if (plan == null || !plan.Applicable || string.IsNullOrWhiteSpace(plan.RemotePath))
                return ProbeFail(plan, "Remote probe plan is incomplete.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            switch (options.Provider)
            {
                case RemoteDeepDeleteProviderType.OpenList:
                    return await ProbeOpenListAsync(plan, options, timeout.Token).ConfigureAwait(false);
                case RemoteDeepDeleteProviderType.WebDav:
                    return await ProbeWebDavAsync(plan, options, timeout.Token).ConfigureAwait(false);
                default:
                    return ProbeFail(plan, "No supported remote provider is selected.");
            }
        }

        public async Task<RemoteDeepDeleteExecutionResult> ExecuteAsync(RemoteDeepDeletePlan plan,
            CancellationToken cancellationToken)
        {
            var options = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            if (plan == null || !plan.Applicable || !plan.Allowed)
                return Fail(plan, "Remote deletion plan is not allowed.");

            var preProbe = await ProbeAsync(plan, cancellationToken).ConfigureAwait(false);
            if (!preProbe.Success)
            {
                var failed = Fail(plan, "Remote pre-delete probe failed: " +
                                        (preProbe.Error ?? "unknown probe failure"));
                failed.PreProbeStatusCode = preProbe.HttpStatusCode;
                failed.PreProbeError = preProbe.Error;
                return failed;
            }

            if (preProbe.Missing)
            {
                if (!options.TreatNotFoundAsSuccess)
                {
                    var missingFailure = Fail(plan,
                        "Remote target is already missing and TreatNotFoundAsSuccess is disabled.");
                    missingFailure.PreProbeStatusCode = preProbe.HttpStatusCode;
                    missingFailure.PreProbeAlreadyMissing = true;
                    return missingFailure;
                }

                return new RemoteDeepDeleteExecutionResult
                {
                    Success = true,
                    DeleteAccepted = false,
                    VerifiedDeleted = true,
                    AlreadyMissing = true,
                    PreProbeAlreadyMissing = true,
                    PreProbeStatusCode = preProbe.HttpStatusCode,
                    VerificationStatusCode = preProbe.HttpStatusCode,
                    Provider = plan.Provider,
                    RemotePath = plan.RemotePath
                };
            }

            if (!preProbe.Exists)
            {
                var ambiguous = Fail(plan, "Remote pre-delete probe succeeded but did not confirm target existence.");
                ambiguous.PreProbeStatusCode = preProbe.HttpStatusCode;
                return ambiguous;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

            RemoteDeepDeleteExecutionResult result;
            switch (options.Provider)
            {
                case RemoteDeepDeleteProviderType.OpenList:
                    result = await ExecuteOpenListDeleteAsync(plan, options, timeout.Token).ConfigureAwait(false);
                    break;
                case RemoteDeepDeleteProviderType.WebDav:
                    result = await ExecuteWebDavDeleteAsync(plan, options, timeout.Token).ConfigureAwait(false);
                    break;
                default:
                    return Fail(plan, "No supported remote delete provider is selected.");
            }

            result.PreProbeVerifiedExists = true;
            result.PreProbeStatusCode = preProbe.HttpStatusCode;
            if (!result.DeleteAccepted) return result;

            var verification = await ProbeAsync(plan, cancellationToken).ConfigureAwait(false);
            result.VerificationStatusCode = verification.HttpStatusCode;
            result.VerifiedDeleted = verification.Success && verification.Missing;
            result.VerificationError = verification.Error;
            result.Success = result.VerifiedDeleted;
            if (!result.Success)
            {
                result.Error = verification.Success && verification.Exists
                    ? "Remote provider accepted deletion but the target still exists during verification."
                    : "Remote deletion could not be verified: " + (verification.Error ?? "unknown verification failure");
            }
            return result;
        }

        private static async Task<RemoteDeepDeleteExecutionResult> ExecuteOpenListDeleteAsync(RemoteDeepDeletePlan plan,
            RemoteDeepDeleteOptions options, CancellationToken cancellationToken)
        {
            var endpoint = options.BaseUrl.TrimEnd('/') + "/api/fs/remove";
            var body = "{\"dir\":" + JsonString(plan.RemoteDirectory) + ",\"names\":[" +
                       JsonString(plan.RemoteName) + "]}";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            AddOpenListAuthorization(request, options.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            try
            {
                using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead,
                    cancellationToken).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var http = (int)response.StatusCode;
                var apiCode = TryReadApiCode(text);
                var transportSuccess = http >= 200 && http < 300;
                var apiSuccess = !apiCode.HasValue || apiCode.Value == 200;
                var explicitMissing = options.TreatNotFoundAsSuccess &&
                                      (response.StatusCode == HttpStatusCode.NotFound ||
                                       response.StatusCode == HttpStatusCode.Gone ||
                                       (transportSuccess && apiCode.HasValue && apiCode.Value != 200 && LooksMissing(text)));
                var accepted = transportSuccess && apiSuccess || explicitMissing;
                return new RemoteDeepDeleteExecutionResult
                {
                    Success = false,
                    DeleteAccepted = accepted,
                    AlreadyMissing = explicitMissing,
                    HttpStatusCode = http,
                    Provider = RemoteDeepDeleteProviderType.OpenList.ToString(),
                    RemotePath = plan.RemotePath,
                    Error = accepted ? null : BuildRemoteError(http, apiCode, text)
                };
            }
            catch (OperationCanceledException)
            {
                return Fail(plan, "OpenList delete timed out or was cancelled.");
            }
            catch (Exception ex)
            {
                return Fail(plan, "OpenList delete failed: " + ex.Message);
            }
        }

        private static async Task<RemoteDeepDeleteExecutionResult> ExecuteWebDavDeleteAsync(RemoteDeepDeletePlan plan,
            RemoteDeepDeleteOptions options, CancellationToken cancellationToken)
        {
            var endpoint = BuildWebDavUri(options.BaseUrl, plan.RemotePath);
            if (endpoint == null) return Fail(plan, "Unable to build a valid WebDAV target URI.");
            using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
            AddWebDavAuthorization(request, options);

            try
            {
                using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead,
                    cancellationToken).ConfigureAwait(false);
                var http = (int)response.StatusCode;
                var missing = options.TreatNotFoundAsSuccess &&
                              (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone);
                var accepted = (http >= 200 && http < 300) || missing;
                var body = accepted ? null : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return new RemoteDeepDeleteExecutionResult
                {
                    Success = false,
                    DeleteAccepted = accepted,
                    AlreadyMissing = missing,
                    HttpStatusCode = http,
                    Provider = RemoteDeepDeleteProviderType.WebDav.ToString(),
                    RemotePath = plan.RemotePath,
                    Error = accepted ? null : "WebDAV DELETE returned HTTP " + http + TruncateBody(body)
                };
            }
            catch (OperationCanceledException)
            {
                return Fail(plan, "WebDAV delete timed out or was cancelled.");
            }
            catch (Exception ex)
            {
                return Fail(plan, "WebDAV delete failed: " + ex.Message);
            }
        }

        private static async Task<RemoteDeepDeleteProbeResult> ProbeOpenListAsync(RemoteDeepDeletePlan plan,
            RemoteDeepDeleteOptions options, CancellationToken cancellationToken)
        {
            var endpoint = options.BaseUrl.TrimEnd('/') + "/api/fs/get";
            var body = "{\"path\":" + JsonString(plan.RemotePath) + ",\"password\":\"\"}";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            AddOpenListAuthorization(request, options.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            try
            {
                using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead,
                    cancellationToken).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var http = (int)response.StatusCode;
                var apiCode = TryReadApiCode(text);

                // Core fail-closed semantics. Do not depend on the optional Harmony normalization layer.
                if (http == 401 || http == 403 || apiCode == 401 || apiCode == 403)
                    return ProbeFail(plan, "OpenList authorization failed; missing state was not accepted.", http, apiCode);

                var transportSuccess = http >= 200 && http < 300;
                if (transportSuccess && (!apiCode.HasValue || apiCode.Value == 200))
                {
                    return new RemoteDeepDeleteProbeResult
                    {
                        Success = true, Missing = false, Exists = true, HttpStatusCode = http,
                        ApiCode = apiCode, Provider = RemoteDeepDeleteProviderType.OpenList.ToString(),
                        RemotePath = plan.RemotePath
                    };
                }

                if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone ||
                    (transportSuccess && apiCode.HasValue && apiCode.Value != 200 && LooksMissing(text)))
                {
                    return new RemoteDeepDeleteProbeResult
                    {
                        Success = true, Missing = true, Exists = false, HttpStatusCode = http,
                        ApiCode = apiCode, Provider = RemoteDeepDeleteProviderType.OpenList.ToString(),
                        RemotePath = plan.RemotePath
                    };
                }

                return ProbeFail(plan, "OpenList /api/fs/get returned HTTP " + http +
                                       (apiCode.HasValue ? ", API code " + apiCode.Value : string.Empty) +
                                       TruncateBody(text), http, apiCode);
            }
            catch (OperationCanceledException)
            {
                return ProbeFail(plan, "OpenList verification timed out or was cancelled.");
            }
            catch (Exception ex)
            {
                return ProbeFail(plan, "OpenList verification failed: " + ex.Message);
            }
        }

        private static async Task<RemoteDeepDeleteProbeResult> ProbeWebDavAsync(RemoteDeepDeletePlan plan,
            RemoteDeepDeleteOptions options, CancellationToken cancellationToken)
        {
            var endpoint = BuildWebDavUri(options.BaseUrl, plan.RemotePath);
            if (endpoint == null) return ProbeFail(plan, "Unable to build a valid WebDAV target URI.");

            var head = await SendWebDavProbeAsync(HttpMethod.Head, endpoint, options, cancellationToken)
                .ConfigureAwait(false);
            if (head.HttpStatusCode == (int)HttpStatusCode.MethodNotAllowed || head.HttpStatusCode == 501)
            {
                var propFind = new HttpMethod("PROPFIND");
                return await SendWebDavProbeAsync(propFind, endpoint, options, cancellationToken, true)
                    .ConfigureAwait(false);
            }
            return head;
        }

        private static async Task<RemoteDeepDeleteProbeResult> SendWebDavProbeAsync(HttpMethod method, Uri endpoint,
            RemoteDeepDeleteOptions options, CancellationToken cancellationToken, bool depthZero = false)
        {
            using var request = new HttpRequestMessage(method, endpoint);
            AddWebDavAuthorization(request, options);
            if (depthZero) request.Headers.TryAddWithoutValidation("Depth", "0");
            try
            {
                using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                var http = (int)response.StatusCode;
                if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone)
                    return new RemoteDeepDeleteProbeResult
                    {
                        Success = true, Missing = true, HttpStatusCode = http,
                        Provider = RemoteDeepDeleteProviderType.WebDav.ToString(),
                        RemotePath = endpoint.AbsolutePath
                    };
                if (http >= 200 && http < 300)
                    return new RemoteDeepDeleteProbeResult
                    {
                        Success = true, Exists = true, HttpStatusCode = http,
                        Provider = RemoteDeepDeleteProviderType.WebDav.ToString(),
                        RemotePath = endpoint.AbsolutePath
                    };
                return new RemoteDeepDeleteProbeResult
                {
                    Success = false, HttpStatusCode = http,
                    Provider = RemoteDeepDeleteProviderType.WebDav.ToString(),
                    RemotePath = endpoint.AbsolutePath,
                    Error = method.Method + " verification returned HTTP " + http
                };
            }
            catch (OperationCanceledException)
            {
                return new RemoteDeepDeleteProbeResult
                {
                    Success = false, Provider = RemoteDeepDeleteProviderType.WebDav.ToString(),
                    RemotePath = endpoint.AbsolutePath, Error = "WebDAV verification timed out or was cancelled."
                };
            }
            catch (Exception ex)
            {
                return new RemoteDeepDeleteProbeResult
                {
                    Success = false, Provider = RemoteDeepDeleteProviderType.WebDav.ToString(),
                    RemotePath = endpoint.AbsolutePath, Error = "WebDAV verification failed: " + ex.Message
                };
            }
        }

        private static Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            HttpCompletionOption completionOption, CancellationToken cancellationToken)
        {
            var sendOverride = SendAsyncOverride;
            return sendOverride != null
                ? sendOverride(request, completionOption, cancellationToken)
                : Client.SendAsync(request, completionOption, cancellationToken);
        }

        private static void AddOpenListAuthorization(HttpRequestMessage request, string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            request.Headers.TryAddWithoutValidation("Authorization", token.Trim());
        }

        private static void AddWebDavAuthorization(HttpRequestMessage request, RemoteDeepDeleteOptions options)
        {
            if (string.IsNullOrEmpty(options.Username) && string.IsNullOrEmpty(options.Password)) return;
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                (options.Username ?? string.Empty) + ":" + (options.Password ?? string.Empty)));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        }

        private static bool TryMatchHttpMapping(string target, string sourcePrefix, out string suffix)
        {
            suffix = null;
            if (!Uri.TryCreate(target, UriKind.Absolute, out var targetUri) ||
                !Uri.TryCreate(sourcePrefix, UriKind.Absolute, out var prefixUri))
                return false;
            if (!IsHttp(targetUri) || !IsHttp(prefixUri)) return false;
            if (!string.Equals(targetUri.Scheme, prefixUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(targetUri.Host, prefixUri.Host, StringComparison.OrdinalIgnoreCase) ||
                EffectivePort(targetUri) != EffectivePort(prefixUri))
                return false;

            var targetPath = DecodePath(targetUri.AbsolutePath);
            var prefixPath = DecodePath(prefixUri.AbsolutePath).TrimEnd('/');
            if (string.Equals(targetPath, prefixPath, StringComparison.Ordinal))
            {
                suffix = string.Empty;
                return true;
            }
            if (!targetPath.StartsWith(prefixPath + "/", StringComparison.Ordinal)) return false;
            suffix = targetPath.Substring(prefixPath.Length).TrimStart('/');
            return true;
        }

        private static bool IsHttp(Uri uri)
        {
            return uri != null && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static int EffectivePort(Uri uri)
        {
            if (!uri.IsDefaultPort) return uri.Port;
            return uri.Scheme == Uri.UriSchemeHttps ? 443 : 80;
        }

        private static string DecodePath(string value)
        {
            try { return Uri.UnescapeDataString(value ?? string.Empty); }
            catch { return value ?? string.Empty; }
        }

        private static string ResolveTarget(BaseItem item)
        {
            var path = item.Path;
            try
            {
                if (item.IsShortcut || string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(path)) return null;
                    return File.ReadLines(path).Select(line => line?.Trim())
                        .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
                }

                var info = new FileInfo(path);
                var linkTarget = TryGetLinkTarget(info);
                if (!string.IsNullOrWhiteSpace(linkTarget))
                {
                    return Path.IsPathRooted(linkTarget)
                        ? linkTarget
                        : Path.GetFullPath(Path.Combine(info.DirectoryName ?? string.Empty, linkTarget));
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Remote Deep Delete target resolution failed: " + ex.Message);
            }
            return path;
        }

        private static string TryGetLinkTarget(FileInfo info)
        {
            if (info == null) return null;
            try
            {
                var property = typeof(FileSystemInfo).GetProperty("LinkTarget",
                                   BindingFlags.Instance | BindingFlags.Public)
                               ?? info.GetType().GetProperty("LinkTarget",
                                   BindingFlags.Instance | BindingFlags.Public);
                return property?.GetValue(info) as string;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsHttpTarget(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsHttp(uri);
        }

        private static string StripQueryAndFragment(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            var index = value.IndexOfAny(new[] { '?', '#' });
            return index >= 0 ? value.Substring(0, index) : value;
        }

        private static string RedactQuery(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
            if (!IsHttp(uri)) return value;
            return uri.GetLeftPart(UriPartial.Path);
        }

        private static string PosixDirName(string path)
        {
            var index = path.LastIndexOf('/');
            return index <= 0 ? "/" : path.Substring(0, index);
        }

        private static string PosixBaseName(string path)
        {
            var index = path.LastIndexOf('/');
            return index < 0 ? path : path.Substring(index + 1);
        }

        private static string SafeHost(string baseUrl)
        {
            return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;
        }

        private static Uri BuildWebDavUri(string baseUrl, string remotePath)
        {
            if (!Uri.TryCreate(baseUrl?.TrimEnd('/') + "/", UriKind.Absolute, out var root)) return null;
            var escaped = string.Join("/", (remotePath ?? string.Empty)
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
            return Uri.TryCreate(root, escaped, out var result) ? result : null;
        }

        private static int? TryReadApiCode(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var match = Regex.Match(json, "\\\"code\\\"\\s*:\\s*(-?\\d+)", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : (int?)null;
        }

        private static bool LooksMissing(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            return body.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   body.IndexOf("object not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   body.IndexOf("no such file", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   body.IndexOf("file not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   body.IndexOf("不存在", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildRemoteError(int http, int? apiCode, string body)
        {
            return "OpenList delete returned HTTP " + http +
                   (apiCode.HasValue ? ", API code " + apiCode.Value : string.Empty) + TruncateBody(body);
        }

        private static string TruncateBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;
            var clean = body.Replace("\r", " ").Replace("\n", " ").Trim();
            if (clean.Length > 240) clean = clean.Substring(0, 240) + "…";
            return ": " + clean;
        }

        private static string JsonString(string value)
        {
            var text = value ?? string.Empty;
            var builder = new StringBuilder(text.Length + 2).Append('"');
            foreach (var ch in text)
            {
                switch (ch)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (ch < 32) builder.Append("\\u").Append(((int)ch).ToString("x4"));
                        else builder.Append(ch);
                        break;
                }
            }
            return builder.Append('"').ToString();
        }

        private static RemoteDeepDeleteExecutionResult Fail(RemoteDeepDeletePlan plan, string error)
        {
            return new RemoteDeepDeleteExecutionResult
            {
                Success = false,
                DeleteAccepted = false,
                Provider = plan?.Provider,
                RemotePath = plan?.RemotePath,
                Error = error
            };
        }

        private static RemoteDeepDeleteProbeResult ProbeFail(RemoteDeepDeletePlan plan, string error,
            int httpStatus = 0, int? apiCode = null)
        {
            return new RemoteDeepDeleteProbeResult
            {
                Success = false,
                HttpStatusCode = httpStatus,
                ApiCode = apiCode,
                Provider = plan?.Provider,
                RemotePath = plan?.RemotePath,
                Error = error
            };
        }
    }
}
