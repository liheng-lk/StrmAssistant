using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace StrmAssistant.Search
{
    public sealed class AdvancedChineseSearchOptions
    {
        public bool Enabled { get; set; }
        public string NativeExtensionPath { get; set; } = string.Empty;
        public string SqliteExecutablePath { get; set; } = string.Empty;
        public string DatabasePath { get; set; } = string.Empty;
        public string BackupDirectory { get; set; } = string.Empty;
        public string CustomDictionaryPath { get; set; } = string.Empty;
        public bool EnablePinyin { get; set; } = true;
        public bool RequireBackup { get; set; } = true;
    }

    public static class AdvancedChineseSearchRuntimeSettings
    {
        private static readonly object Sync = new object();
        private static AdvancedChineseSearchOptions _options;
        private static string _path;

        public static string SettingsPath
        {
            get { EnsureLoaded(); return _path; }
        }

        public static AdvancedChineseSearchOptions GetSnapshot()
        {
            EnsureLoaded();
            lock (Sync) return Clone(_options);
        }

        public static AdvancedChineseSearchOptions Save(AdvancedChineseSearchOptions options)
        {
            EnsureLoaded();
            lock (Sync)
            {
                _options = Sanitize(options ?? new AdvancedChineseSearchOptions());
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.WriteAllLines(_path, new[]
                {
                    "Enabled=" + _options.Enabled,
                    "NativeExtensionPath=" + _options.NativeExtensionPath,
                    "SqliteExecutablePath=" + _options.SqliteExecutablePath,
                    "DatabasePath=" + _options.DatabasePath,
                    "BackupDirectory=" + _options.BackupDirectory,
                    "CustomDictionaryPath=" + _options.CustomDictionaryPath,
                    "EnablePinyin=" + _options.EnablePinyin,
                    "RequireBackup=" + _options.RequireBackup
                });
                return Clone(_options);
            }
        }

        public static string ResolveDatabasePath(AdvancedChineseSearchOptions options)
        {
            options = options ?? GetSnapshot();
            if (!string.IsNullOrWhiteSpace(options.DatabasePath))
                return Normalize(options.DatabasePath);

            var applicationPaths = Plugin.Instance?.ApplicationPaths;
            if (applicationPaths == null) return null;

            foreach (var propertyName in new[] { "DataPath", "ProgramDataPath" })
            {
                try
                {
                    var property = applicationPaths.GetType().GetProperty(propertyName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var root = property?.GetValue(applicationPaths) as string;
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    foreach (var candidate in new[]
                    {
                        Path.Combine(root, "library.db"),
                        Path.Combine(root, "data", "library.db")
                    }.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                    }
                }
                catch
                {
                    // Continue probing the next public/runtime path property.
                }
            }
            return null;
        }

        public static string ResolveBackupDirectory(AdvancedChineseSearchOptions options, string databasePath)
        {
            options = options ?? GetSnapshot();
            if (!string.IsNullOrWhiteSpace(options.BackupDirectory))
                return Normalize(options.BackupDirectory);
            if (!string.IsNullOrWhiteSpace(databasePath))
            {
                var parent = Path.GetDirectoryName(databasePath);
                if (!string.IsNullOrWhiteSpace(parent))
                    return Path.Combine(parent, "strmassistant-search-backups");
            }
            return null;
        }

        private static void EnsureLoaded()
        {
            if (Volatile.Read(ref _options) != null) return;
            lock (Sync)
            {
                if (_options != null) return;
                var root = Plugin.Instance?.ApplicationPaths?.CachePath;
                if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
                _path = Path.Combine(root, "strmassistant-custom", "advanced-chinese-search.conf");
                _options = Load(_path);
            }
        }

        private static AdvancedChineseSearchOptions Load(string path)
        {
            var result = new AdvancedChineseSearchOptions();
            try
            {
                if (!File.Exists(path)) return result;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var index = raw.IndexOf('=');
                    if (index <= 0) continue;
                    var key = raw.Substring(0, index).Trim();
                    var value = raw.Substring(index + 1).Trim();
                    switch (key)
                    {
                        case "Enabled": result.Enabled = ParseBool(value, false); break;
                        case "NativeExtensionPath": result.NativeExtensionPath = value; break;
                        case "SqliteExecutablePath": result.SqliteExecutablePath = value; break;
                        case "DatabasePath": result.DatabasePath = value; break;
                        case "BackupDirectory": result.BackupDirectory = value; break;
                        case "CustomDictionaryPath": result.CustomDictionaryPath = value; break;
                        case "EnablePinyin": result.EnablePinyin = ParseBool(value, true); break;
                        case "RequireBackup": result.RequireBackup = ParseBool(value, true); break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("Advanced Chinese search settings load failed: " + ex.Message);
            }
            return Sanitize(result);
        }

        private static AdvancedChineseSearchOptions Sanitize(AdvancedChineseSearchOptions value)
        {
            return new AdvancedChineseSearchOptions
            {
                Enabled = value.Enabled,
                NativeExtensionPath = Normalize(value.NativeExtensionPath),
                SqliteExecutablePath = Normalize(value.SqliteExecutablePath),
                DatabasePath = Normalize(value.DatabasePath),
                BackupDirectory = Normalize(value.BackupDirectory),
                CustomDictionaryPath = Normalize(value.CustomDictionaryPath),
                EnablePinyin = value.EnablePinyin,
                RequireBackup = value.RequireBackup
            };
        }

        private static AdvancedChineseSearchOptions Clone(AdvancedChineseSearchOptions value)
        {
            return Sanitize(value ?? new AdvancedChineseSearchOptions());
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            try { return Path.GetFullPath(value.Trim()); }
            catch { return value.Trim(); }
        }

        private static bool ParseBool(string value, bool fallback)
        {
            return bool.TryParse(value, out var parsed) ? parsed : fallback;
        }
    }
}
