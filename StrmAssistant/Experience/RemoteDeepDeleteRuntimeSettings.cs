using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace StrmAssistant.Experience
{
    public enum RemoteDeepDeleteProviderType
    {
        None,
        OpenList,
        WebDav
    }

    public sealed class RemoteDeepDeleteOptions
    {
        public bool Enabled { get; set; }
        public RemoteDeepDeleteProviderType Provider { get; set; } = RemoteDeepDeleteProviderType.None;
        public string BaseUrl { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string PathMappings { get; set; } = string.Empty;
        public string AllowedRemoteRoots { get; set; } = string.Empty;
        public int TimeoutSeconds { get; set; } = 30;
        public bool TreatNotFoundAsSuccess { get; set; } = true;
    }

    public sealed class RemoteDeepDeletePathMapping
    {
        public string SourcePrefix { get; set; }
        public string RemoteRoot { get; set; }
    }

    public static class RemoteDeepDeleteRuntimeSettings
    {
        private static readonly object Sync = new object();
        private static RemoteDeepDeleteOptions _options;
        private static string _path;

        public static string SettingsPath
        {
            get { EnsureLoaded(); return _path; }
        }

        public static RemoteDeepDeleteOptions GetSnapshot()
        {
            EnsureLoaded();
            lock (Sync) return Clone(_options);
        }

        public static RemoteDeepDeleteOptions Save(RemoteDeepDeleteOptions value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            EnsureLoaded();

            lock (Sync)
            {
                _options = Sanitize(value);
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.WriteAllLines(_path, new[]
                {
                    "Enabled=" + _options.Enabled,
                    "Provider=" + _options.Provider,
                    "BaseUrl=" + Escape(_options.BaseUrl),
                    "AccessToken=" + Escape(_options.AccessToken),
                    "Username=" + Escape(_options.Username),
                    "Password=" + Escape(_options.Password),
                    "PathMappings=" + Escape(_options.PathMappings),
                    "AllowedRemoteRoots=" + Escape(_options.AllowedRemoteRoots),
                    "TimeoutSeconds=" + _options.TimeoutSeconds,
                    "TreatNotFoundAsSuccess=" + _options.TreatNotFoundAsSuccess
                });
                return Clone(_options);
            }
        }

        public static IReadOnlyList<RemoteDeepDeletePathMapping> ParseMappings(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<RemoteDeepDeletePathMapping>();
            var result = new List<RemoteDeepDeletePathMapping>();
            foreach (var line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var text = line.Trim();
                if (text.Length == 0 || text.StartsWith("#", StringComparison.Ordinal)) continue;
                var index = text.IndexOf("=>", StringComparison.Ordinal);
                if (index <= 0) continue;
                var source = text.Substring(0, index).Trim();
                var root = NormalizeRemotePath(text.Substring(index + 2).Trim());
                if (source.Length == 0 || root == null) continue;
                result.Add(new RemoteDeepDeletePathMapping { SourcePrefix = source, RemoteRoot = root });
            }
            return result.OrderByDescending(mapping => mapping.SourcePrefix.Length).ToList();
        }

        public static IReadOnlyList<string> ParseAllowedRoots(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
            return raw.Split(new[] { '\r', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => NormalizeRemotePath(value.Trim()))
                .Where(value => value != null)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(value => value.Length)
                .ToList();
        }

        public static string NormalizeRemotePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var text = path.Trim().Replace('\\', '/');
            if (!text.StartsWith("/", StringComparison.Ordinal)) text = "/" + text;
            while (text.Contains("//")) text = text.Replace("//", "/");

            var parts = new List<string>();
            foreach (var segment in text.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".") continue;
                if (segment == "..")
                {
                    if (parts.Count == 0) return null;
                    parts.RemoveAt(parts.Count - 1);
                    continue;
                }
                parts.Add(segment);
            }
            return "/" + string.Join("/", parts);
        }

        public static bool IsWithinAllowedRoot(string remotePath, IReadOnlyList<string> roots)
        {
            if (string.IsNullOrWhiteSpace(remotePath) || roots == null || roots.Count == 0) return false;
            var normalized = NormalizeRemotePath(remotePath);
            if (normalized == null) return false;
            return roots.Any(root => string.Equals(normalized, root, StringComparison.Ordinal) ||
                                     normalized.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal));
        }

        private static void EnsureLoaded()
        {
            if (Volatile.Read(ref _options) != null) return;
            lock (Sync)
            {
                if (_options != null) return;
                var root = Plugin.Instance?.DataFolderPath;
                if (string.IsNullOrWhiteSpace(root))
                    root = Plugin.Instance?.ApplicationPaths?.PluginConfigurationsPath;
                if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
                _path = Path.Combine(root, "remote-deep-delete.conf");
                _options = Load(_path);
            }
        }

        private static RemoteDeepDeleteOptions Load(string path)
        {
            var result = new RemoteDeepDeleteOptions();
            try
            {
                if (!File.Exists(path)) return result;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var index = raw.IndexOf('=');
                    if (index <= 0) continue;
                    var key = raw.Substring(0, index).Trim();
                    var value = Unescape(raw.Substring(index + 1));
                    switch (key)
                    {
                        case "Enabled": result.Enabled = ParseBool(value, false); break;
                        case "Provider":
                            if (Enum.TryParse(value, true, out RemoteDeepDeleteProviderType provider)) result.Provider = provider;
                            break;
                        case "BaseUrl": result.BaseUrl = value; break;
                        case "AccessToken": result.AccessToken = value; break;
                        case "Username": result.Username = value; break;
                        case "Password": result.Password = value; break;
                        case "PathMappings": result.PathMappings = value; break;
                        case "AllowedRemoteRoots": result.AllowedRemoteRoots = value; break;
                        case "TimeoutSeconds": if (int.TryParse(value, out var timeout)) result.TimeoutSeconds = timeout; break;
                        case "TreatNotFoundAsSuccess": result.TreatNotFoundAsSuccess = ParseBool(value, true); break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Remote Deep Delete settings load failed: " + ex.Message);
            }
            return Sanitize(result);
        }

        private static RemoteDeepDeleteOptions Sanitize(RemoteDeepDeleteOptions value)
        {
            var baseUrl = value.BaseUrl?.Trim() ?? string.Empty;
            if (baseUrl.Length > 0 && (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                                      (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
                baseUrl = string.Empty;

            return new RemoteDeepDeleteOptions
            {
                Enabled = value.Enabled,
                Provider = value.Provider,
                BaseUrl = baseUrl.TrimEnd('/'),
                AccessToken = value.AccessToken?.Trim() ?? string.Empty,
                Username = value.Username ?? string.Empty,
                Password = value.Password ?? string.Empty,
                PathMappings = value.PathMappings ?? string.Empty,
                AllowedRemoteRoots = value.AllowedRemoteRoots ?? string.Empty,
                TimeoutSeconds = Math.Max(5, Math.Min(120, value.TimeoutSeconds <= 0 ? 30 : value.TimeoutSeconds)),
                TreatNotFoundAsSuccess = value.TreatNotFoundAsSuccess
            };
        }

        private static RemoteDeepDeleteOptions Clone(RemoteDeepDeleteOptions value)
        {
            return Sanitize(value ?? new RemoteDeepDeleteOptions());
        }

        private static bool ParseBool(string value, bool fallback)
        {
            return bool.TryParse(value?.Trim(), out var parsed) ? parsed : fallback;
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static string Unescape(string value)
        {
            var text = value ?? string.Empty;
            var result = new System.Text.StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\\' || i + 1 >= text.Length)
                {
                    result.Append(text[i]);
                    continue;
                }
                var next = text[++i];
                if (next == 'n') result.Append('\n');
                else if (next == 'r') result.Append('\r');
                else result.Append(next);
            }
            return result.ToString();
        }
    }
}
