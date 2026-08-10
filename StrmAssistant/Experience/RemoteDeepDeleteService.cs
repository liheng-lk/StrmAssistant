using MediaBrowser.Controller.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

    public sealed class RemoteDeepDeleteExecutionResult
    {
        public bool Success { get; set; }
        public bool AlreadyMissing { get; set; }
        public int HttpStatusCode { get; set; }
        public string Provider { get; set; }
        public string RemotePath { get; set; }
        public string Error { get; set; }
    }

    public sealed class RemoteDeepDeleteService
    {
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false
        });

        public RemoteDeepDeletePlan BuildPlan(BaseItem item)
        {
            var options = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            var plan = new RemoteDeepDeletePlan
            {
                Provider = options.Provider.ToString()
            };

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

            var mappings = RemoteDeepDeleteRuntimeSettings.ParseMappings(options.PathMappings);
            var targetWithoutQuery = StripQueryAndFragment(target);
            var mapping = mappings.FirstOrDefault(candidate =>
                targetWithoutQuery.StartsWith(candidate.SourcePrefix, StringComparison.OrdinalIgnoreCase));
            if (mapping == null)
            {
                plan.Error = "The resolved media target did not match any configured remote path mapping.";
                return plan;
            }

            var suffix = targetWithoutQuery.Substring(mapping.SourcePrefix.Length).TrimStart('/', '\\');
            try { suffix = Uri.UnescapeDataString(suffix); }
            catch { }

            var remotePath = RemoteDeepDeleteRuntimeSettings.NormalizeRemotePath(
                mapping.RemoteRoot.TrimEnd('/') + "/" + suffix.Replace('\\', '/'));
            if (remotePath == null || remotePath == "/")
            {
                plan.Error = "The mapping resolved to an invalid/root remote path.";
                return plan;
            }

            plan.Applicable = true;
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
                plan.Warnings.Add("The STRM file itself will be removed by Emby only after the remote provider confirms deletion.");
            return plan;
        }

        public async Task<RemoteDeepDeleteExecutionResult> ExecuteAsync(RemoteDeepDeletePlan plan,
            CancellationToken cancellationToken)
        {
            var options = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            if (plan == null || !plan.Applicable || !plan.Allowed)
                return Fail(plan, "Remote deletion plan is not allowed.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

            switch (options.Provider)
            {
                case RemoteDeepDeleteProviderType.OpenList:
                    return await ExecuteOpenListAsync(plan, options, timeout.Token).ConfigureAwait(false);
                case RemoteDeepDeleteProviderType.WebDav:
                    return await ExecuteWebDavAsync(plan, options, timeout.Token).ConfigureAwait(false);
                default:
                    return Fail(plan, "No supported remote delete provider is selected.");
            }
        }

        private static async Task<RemoteDeepDeleteExecutionResult> ExecuteOpenListAsync(RemoteDeepDeletePlan plan,
            RemoteDeepDeleteOptions options, CancellationToken cancellationToken)
        {
            var endpoint = options.BaseUrl.TrimEnd('/') + "/api/fs/remove";
            var body = "{\"dir\":" + JsonString(plan.RemoteDirectory) + ",\"names\":[" +
                       JsonString(plan.RemoteName) + "]}";

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", options.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            try
            {
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead,
                    cancellationToken).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var http = (int)response.StatusCode;
                var apiCode = TryReadApiCode(text);
                var missing = options.TreatNotFoundAsSuccess &&
                              (response.StatusCode == HttpStatusCode.NotFound || LooksMissing(text));
                var success = (http >= 200 && http < 300 && (!apiCode.HasValue || apiCode.Value == 200)) || missing;

                return new RemoteDeepDeleteExecutionResult
                {
                    Success = success,
                    AlreadyMissing = missing,
                    HttpStatusCode = http,
                    Provider = RemoteDeepDeleteProviderType.OpenList.ToString(),
                    RemotePath = plan.RemotePath,
                    Error = success ? null : BuildRemoteError(http, apiCode, text)
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

        private static async Task<RemoteDeepDeleteExecutionResult> ExecuteWebDavAsync(RemoteDeepDeletePlan plan,
            RemoteDeepDeleteOptions options, CancellationToken cancellationToken)
        {
            var endpoint = BuildWebDavUri(options.BaseUrl, plan.RemotePath);
            if (endpoint == null) return Fail(plan, "Unable to build a valid WebDAV target URI.");

            using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
            if (!string.IsNullOrEmpty(options.Username) || !string.IsNullOrEmpty(options.Password))
            {
                var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    (options.Username ?? string.Empty) + ":" + (options.Password ?? string.Empty)));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
            }

            try
            {
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead,
                    cancellationToken).ConfigureAwait(false);
                var http = (int)response.StatusCode;
                var missing = options.TreatNotFoundAsSuccess && response.StatusCode == HttpStatusCode.NotFound;
                var success = (http >= 200 && http < 300) || missing;
                var body = success ? null : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return new RemoteDeepDeleteExecutionResult
                {
                    Success = success,
                    AlreadyMissing = missing,
                    HttpStatusCode = http,
                    Provider = RemoteDeepDeleteProviderType.WebDav.ToString(),
                    RemotePath = plan.RemotePath,
                    Error = success ? null : "WebDAV DELETE returned HTTP " + http + TruncateBody(body)
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
                if (!string.IsNullOrWhiteSpace(info.LinkTarget))
                {
                    var target = info.LinkTarget;
                    return Path.IsPathRooted(target)
                        ? target
                        : Path.GetFullPath(Path.Combine(info.DirectoryName ?? string.Empty, target));
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.DebugMode == true)
                    Plugin.Instance.Logger.Debug("Remote Deep Delete target resolution failed: " + ex.Message);
            }
            return path;
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
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return value;
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
                   body.IndexOf("no such file", StringComparison.OrdinalIgnoreCase) >= 0;
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
                Provider = plan?.Provider,
                RemotePath = plan?.RemotePath,
                Error = error
            };
        }
    }
}
