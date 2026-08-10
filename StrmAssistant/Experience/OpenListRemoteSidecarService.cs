using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Experience
{
    internal sealed class OpenListListResponse
    {
        public int code { get; set; }
        public string message { get; set; }
        public OpenListListData data { get; set; }
    }

    internal sealed class OpenListListData
    {
        public List<OpenListListEntry> content { get; set; }
        public long total { get; set; }
    }

    internal sealed class OpenListListEntry
    {
        public string name { get; set; }
        public bool is_dir { get; set; }
        public long size { get; set; }
        public int type { get; set; }
    }

    public sealed class OpenListRemoteSidecarPlan
    {
        public bool Supported { get; set; }
        public bool Success { get; set; }
        public bool Enabled { get; set; }
        public bool DirectoryListingTruncated { get; set; }
        public string RemoteDirectory { get; set; }
        public string MainRemoteName { get; set; }
        public long DirectoryTotal { get; set; }
        public List<string> Candidates { get; set; } = new List<string>();
        public string Error { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class OpenListRemoteSidecarExecutionResult
    {
        public bool Success { get; set; }
        public bool Executed { get; set; }
        public int HttpStatusCode { get; set; }
        public int ApiCode { get; set; }
        public List<string> RequestedNames { get; set; } = new List<string>();
        public List<string> RemainingNames { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    /// <summary>
    /// OpenList-only conservative sidecar cleanup. Candidates must come from the real remote directory
    /// listing, share the exact main-file stem (or a language/role suffix separated by '.'/'-'), and use
    /// a non-video allowlisted metadata/subtitle/image extension. Generic poster.jpg and other video
    /// versions are intentionally never inferred/deleted.
    /// </summary>
    public sealed class OpenListRemoteSidecarService
    {
        private const int ListPageSize = 1000;
        private const int MaxCandidates = 64;
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        private static readonly HashSet<string> AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".nfo", ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx",
            ".jpg", ".jpeg", ".png", ".webp", ".json", ".xml"
        };

        public async Task<OpenListRemoteSidecarPlan> PlanAsync(RemoteDeepDeletePlan mainPlan,
            CancellationToken cancellationToken)
        {
            var options = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            var plan = new OpenListRemoteSidecarPlan
            {
                Enabled = options.DeleteAssociatedSidecars,
                Supported = options.Provider == RemoteDeepDeleteProviderType.OpenList,
                RemoteDirectory = mainPlan?.RemoteDirectory,
                MainRemoteName = mainPlan?.RemoteName
            };

            if (!plan.Supported)
            {
                plan.Error = "Remote sidecar cleanup is currently supported only for OpenList.";
                return plan;
            }
            if (mainPlan == null || !mainPlan.Applicable || !mainPlan.Allowed ||
                string.IsNullOrWhiteSpace(mainPlan.RemoteDirectory) || string.IsNullOrWhiteSpace(mainPlan.RemoteName))
            {
                plan.Error = "The main remote deep-delete plan is incomplete or not allowed.";
                return plan;
            }
            if (string.IsNullOrWhiteSpace(options.BaseUrl) || string.IsNullOrWhiteSpace(options.AccessToken))
            {
                plan.Error = "OpenList BaseUrl/AccessToken is incomplete.";
                return plan;
            }

            var listing = await ListDirectoryAsync(mainPlan.RemoteDirectory, options, cancellationToken)
                .ConfigureAwait(false);
            if (!listing.Success)
            {
                plan.Error = listing.Error;
                return plan;
            }

            plan.DirectoryTotal = listing.Total;
            plan.DirectoryListingTruncated = listing.Total > ListPageSize;
            if (plan.DirectoryListingTruncated)
            {
                plan.Error = "OpenList directory contains more than " + ListPageSize +
                             " entries; conservative sidecar cleanup refuses a truncated listing.";
                return plan;
            }

            var mainStem = FileStem(mainPlan.RemoteName);
            if (string.IsNullOrWhiteSpace(mainStem))
            {
                plan.Error = "Unable to derive the main remote filename stem.";
                return plan;
            }

            plan.Candidates = listing.Entries
                .Where(entry => entry != null && !entry.is_dir && IsSafeName(entry.name))
                .Select(entry => entry.name)
                .Where(name => !string.Equals(name, mainPlan.RemoteName, StringComparison.Ordinal))
                .Where(name => IsAssociatedSidecar(mainStem, name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            if (plan.Candidates.Count > MaxCandidates)
            {
                plan.Error = "More than " + MaxCandidates +
                             " same-stem sidecar candidates were found; automatic cleanup is blocked for safety.";
                return plan;
            }

            plan.Success = true;
            if (!options.DeleteAssociatedSidecars)
                plan.Warnings.Add("Preview only: DeleteAssociatedSidecars is currently disabled.");
            if (plan.Candidates.Count == 0)
                plan.Warnings.Add("No conservative same-stem metadata/subtitle/image sidecars were found.");
            return plan;
        }

        public async Task<OpenListRemoteSidecarExecutionResult> DeleteAndVerifyAsync(RemoteDeepDeletePlan mainPlan,
            OpenListRemoteSidecarPlan sidecarPlan, CancellationToken cancellationToken)
        {
            var options = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            var result = new OpenListRemoteSidecarExecutionResult();
            if (!options.DeleteAssociatedSidecars)
            {
                result.Success = true;
                return result;
            }
            if (options.Provider != RemoteDeepDeleteProviderType.OpenList || sidecarPlan?.Success != true)
            {
                result.Error = sidecarPlan?.Error ?? "OpenList sidecar plan is not valid.";
                return result;
            }
            if (sidecarPlan.Candidates == null || sidecarPlan.Candidates.Count == 0)
            {
                result.Success = true;
                return result;
            }
            if (sidecarPlan.Candidates.Count > MaxCandidates)
            {
                result.Error = "Sidecar candidate safety limit exceeded.";
                return result;
            }

            result.RequestedNames = sidecarPlan.Candidates.ToList();
            var endpoint = options.BaseUrl.TrimEnd('/') + "/api/fs/remove";
            var body = "{\"dir\":" + JsonString(mainPlan.RemoteDirectory) + ",\"names\":[" +
                       string.Join(",", sidecarPlan.Candidates.Select(JsonString)) + "]}";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", options.AccessToken.Trim());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            try
            {
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                    .ConfigureAwait(false);
                var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                result.Executed = true;
                result.HttpStatusCode = (int)response.StatusCode;
                var parsed = Deserialize<OpenListListResponse>(responseText);
                result.ApiCode = parsed?.code ?? 0;
                var accepted = result.HttpStatusCode >= 200 && result.HttpStatusCode < 300 &&
                               (parsed == null || parsed.code == 0 || parsed.code == 200);
                if (!accepted)
                {
                    result.Error = "OpenList sidecar remove returned HTTP " + result.HttpStatusCode +
                                   (parsed != null ? ", API code " + parsed.code + ": " + parsed.message : string.Empty);
                    return result;
                }

                var verify = await ListDirectoryAsync(mainPlan.RemoteDirectory, options, cancellationToken)
                    .ConfigureAwait(false);
                if (!verify.Success)
                {
                    result.Error = "Sidecar deletion was accepted but verification listing failed: " + verify.Error;
                    return result;
                }
                var remaining = new HashSet<string>(verify.Entries.Where(entry => entry != null && !entry.is_dir)
                    .Select(entry => entry.name), StringComparer.Ordinal);
                result.RemainingNames = sidecarPlan.Candidates.Where(remaining.Contains).ToList();
                result.Success = result.RemainingNames.Count == 0;
                if (!result.Success)
                    result.Error = "OpenList accepted sidecar deletion but some candidates still exist: " +
                                   string.Join(", ", result.RemainingNames);
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Error = "OpenList sidecar deletion/verification timed out or was cancelled.";
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "OpenList sidecar deletion failed: " + ex.GetBaseException().Message;
                return result;
            }
        }

        private async Task<DirectoryListResult> ListDirectoryAsync(string remoteDirectory,
            RemoteDeepDeleteOptions options, CancellationToken cancellationToken)
        {
            var result = new DirectoryListResult();
            var endpoint = options.BaseUrl.TrimEnd('/') + "/api/fs/list";
            var body = "{\"path\":" + JsonString(remoteDirectory) +
                       ",\"password\":\"\",\"page\":1,\"per_page\":" + ListPageSize +
                       ",\"refresh\":false}";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", options.AccessToken.Trim());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            try
            {
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                    .ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                {
                    result.Error = "OpenList /api/fs/list returned HTTP " + (int)response.StatusCode;
                    return result;
                }
                var parsed = Deserialize<OpenListListResponse>(text);
                if (parsed == null)
                {
                    result.Error = "OpenList /api/fs/list returned an unreadable JSON response.";
                    return result;
                }
                if (parsed.code != 0 && parsed.code != 200)
                {
                    result.Error = "OpenList /api/fs/list API code " + parsed.code + ": " + parsed.message;
                    return result;
                }
                result.Success = true;
                result.Total = parsed.data?.total ?? 0;
                result.Entries = parsed.data?.content ?? new List<OpenListListEntry>();
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Error = "OpenList directory listing timed out or was cancelled.";
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "OpenList directory listing failed: " + ex.GetBaseException().Message;
                return result;
            }
        }

        private static T Deserialize<T>(string text) where T : class
        {
            try
            {
                var serializer = Plugin.Instance?.ApplicationHost?.Resolve<IJsonSerializer>();
                return serializer?.DeserializeFromString<T>(text);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsAssociatedSidecar(string mainStem, string candidateName)
        {
            if (string.IsNullOrWhiteSpace(candidateName)) return false;
            var extension = Extension(candidateName);
            if (!AllowedExtensions.Contains(extension)) return false;
            var candidateStem = candidateName.Substring(0, candidateName.Length - extension.Length);
            return string.Equals(candidateStem, mainStem, StringComparison.Ordinal) ||
                   candidateStem.StartsWith(mainStem + ".", StringComparison.Ordinal) ||
                   candidateStem.StartsWith(mainStem + "-", StringComparison.Ordinal);
        }

        private static string FileStem(string name)
        {
            if (!IsSafeName(name)) return null;
            var extension = Extension(name);
            return extension.Length > 0 ? name.Substring(0, name.Length - extension.Length) : name;
        }

        private static string Extension(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            var index = name.LastIndexOf('.');
            return index >= 0 ? name.Substring(index).ToLowerInvariant() : string.Empty;
        }

        private static bool IsSafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..") return false;
            return name.IndexOf('/') < 0 && name.IndexOf('\\') < 0 && name.IndexOf('\0') < 0;
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

        private sealed class DirectoryListResult
        {
            public bool Success { get; set; }
            public long Total { get; set; }
            public List<OpenListListEntry> Entries { get; set; } = new List<OpenListListEntry>();
            public string Error { get; set; }
        }
    }
}
