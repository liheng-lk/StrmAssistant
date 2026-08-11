using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Search
{
    public sealed class AdvancedChineseSearchHealthResult
    {
        public bool Success { get; set; }
        public bool Enabled { get; set; }
        public string Platform { get; set; }
        public string Architecture { get; set; }
        public string DatabasePath { get; set; }
        public bool DatabaseExists { get; set; }
        public string NativeExtensionPath { get; set; }
        public bool NativeExtensionExists { get; set; }
        public string SqliteExecutablePath { get; set; }
        public bool SqliteExecutableExists { get; set; }
        public string CustomDictionaryPath { get; set; }
        public bool CustomDictionaryExists { get; set; }
        public bool ActiveTokenizerTestRequested { get; set; }
        public bool ActiveTokenizerTestPassed { get; set; }
        public string SqliteVersionOutput { get; set; }
        public string ActiveTestOutput { get; set; }
        public string Error { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class AdvancedChineseSearchPlanResult
    {
        public bool Success { get; set; }
        public bool CanApply { get; set; }
        public string DatabasePath { get; set; }
        public string NativeExtensionPath { get; set; }
        public string SqliteExecutablePath { get; set; }
        public string BackupDirectory { get; set; }
        public string ProposedBackupPath { get; set; }
        public string CustomDictionaryPath { get; set; }
        public bool RequireBackup { get; set; }
        public bool EnablePinyin { get; set; }
        public List<string> PlannedOperations { get; set; } = new List<string>();
        public List<string> Preconditions { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    public sealed class AdvancedChineseSearchDiagnostics
    {
        public async Task<AdvancedChineseSearchHealthResult> CheckAsync(bool runActiveTokenizerTest,
            CancellationToken cancellationToken)
        {
            var options = AdvancedChineseSearchRuntimeSettings.GetSnapshot();
            var databasePath = AdvancedChineseSearchRuntimeSettings.ResolveDatabasePath(options);
            var result = new AdvancedChineseSearchHealthResult
            {
                Enabled = options.Enabled,
                Platform = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                DatabasePath = databasePath,
                DatabaseExists = !string.IsNullOrWhiteSpace(databasePath) && File.Exists(databasePath),
                NativeExtensionPath = options.NativeExtensionPath,
                NativeExtensionExists = !string.IsNullOrWhiteSpace(options.NativeExtensionPath) && File.Exists(options.NativeExtensionPath),
                SqliteExecutablePath = options.SqliteExecutablePath,
                SqliteExecutableExists = !string.IsNullOrWhiteSpace(options.SqliteExecutablePath) && File.Exists(options.SqliteExecutablePath),
                CustomDictionaryPath = options.CustomDictionaryPath,
                CustomDictionaryExists = string.IsNullOrWhiteSpace(options.CustomDictionaryPath) || File.Exists(options.CustomDictionaryPath),
                ActiveTokenizerTestRequested = runActiveTokenizerTest
            };

            if (!result.DatabaseExists)
                result.Warnings.Add("library.db was not resolved. Set DatabasePath explicitly if Emby stores it outside the standard data paths.");
            if (!result.NativeExtensionExists)
                result.Warnings.Add("Native simple tokenizer extension was not found. Advanced FTS mode cannot be applied.");
            if (!string.IsNullOrWhiteSpace(options.CustomDictionaryPath) && !result.CustomDictionaryExists)
                result.Warnings.Add("CustomDictionaryPath is configured but the file does not exist.");

            if (!runActiveTokenizerTest)
            {
                result.Success = result.NativeExtensionExists && result.DatabaseExists;
                return result;
            }

            if (!result.SqliteExecutableExists)
            {
                result.Error = "Active tokenizer test requires an explicit sqlite3 executable path.";
                return result;
            }
            if (!result.NativeExtensionExists)
            {
                result.Error = "Active tokenizer test requires an existing native extension path.";
                return result;
            }

            var version = await RunSqliteAsync(options.SqliteExecutablePath, ":memory:",
                ".bail on\nSELECT sqlite_version();\n.quit\n", 10, cancellationToken).ConfigureAwait(false);
            result.SqliteVersionOutput = version.StdOut?.Trim();
            if (version.ExitCode != 0)
            {
                result.Error = "sqlite3 version check failed: " + FirstNonEmpty(version.StdErr, version.StdOut);
                return result;
            }

            var extensionArg = EscapeDotCommandArgument(options.NativeExtensionPath);
            var script = new StringBuilder()
                .AppendLine(".bail on")
                .AppendLine(".load " + extensionArg)
                .AppendLine("CREATE VIRTUAL TABLE simple_health USING fts5(text, tokenize='simple');")
                .AppendLine("INSERT INTO simple_health(text) VALUES('中文搜索测试 Beijing 北京');")
                .AppendLine("SELECT count(*) FROM simple_health WHERE simple_health MATCH simple_query('中文搜索');")
                .AppendLine("DROP TABLE simple_health;")
                .AppendLine(".quit")
                .ToString();

            var smoke = await RunSqliteAsync(options.SqliteExecutablePath, ":memory:", script, 15,
                cancellationToken).ConfigureAwait(false);
            result.ActiveTestOutput = (smoke.StdOut + Environment.NewLine + smoke.StdErr).Trim();
            result.ActiveTokenizerTestPassed = smoke.ExitCode == 0 &&
                                               ContainsPositiveIntegerLine(smoke.StdOut);
            result.Success = result.ActiveTokenizerTestPassed && result.DatabaseExists;
            if (!result.ActiveTokenizerTestPassed)
                result.Error = "The simple tokenizer could not be loaded and queried in an in-memory sqlite database.";
            return result;
        }

        public AdvancedChineseSearchPlanResult BuildPlan()
        {
            var options = AdvancedChineseSearchRuntimeSettings.GetSnapshot();
            var databasePath = AdvancedChineseSearchRuntimeSettings.ResolveDatabasePath(options);
            var backupDirectory = AdvancedChineseSearchRuntimeSettings.ResolveBackupDirectory(options, databasePath);
            var result = new AdvancedChineseSearchPlanResult
            {
                DatabasePath = databasePath,
                NativeExtensionPath = options.NativeExtensionPath,
                SqliteExecutablePath = options.SqliteExecutablePath,
                BackupDirectory = backupDirectory,
                CustomDictionaryPath = options.CustomDictionaryPath,
                RequireBackup = options.RequireBackup,
                EnablePinyin = options.EnablePinyin
            };

            if (!string.IsNullOrWhiteSpace(databasePath))
            {
                var name = Path.GetFileNameWithoutExtension(databasePath);
                var extension = Path.GetExtension(databasePath);
                result.ProposedBackupPath = string.IsNullOrWhiteSpace(backupDirectory)
                    ? null
                    : Path.Combine(backupDirectory,
                        name + ".strmassistant-before-simple-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + extension);
            }

            result.Preconditions.Add("Stop or quiesce writes to the Emby library database before any future FTS rebuild operation.");
            result.Preconditions.Add("Run the active :memory: tokenizer Health test successfully with the exact native library that will be loaded by Emby.");
            result.Preconditions.Add("Verify the runtime SQLite connection loader can load the same extension on every pooled connection before rebuilding fts_search9.");
            if (options.RequireBackup)
                result.Preconditions.Add("Create and verify a restorable library.db backup before changing the FTS schema.");

            result.PlannedOperations.Add("Create a verified backup of library.db without overwriting an existing backup.");
            result.PlannedOperations.Add("Load the simple SQLite extension into a controlled maintenance connection.");
            if (!string.IsNullOrWhiteSpace(options.CustomDictionaryPath))
                result.PlannedOperations.Add("Load the configured custom dictionary through the extension's supported dictionary interface after capability verification.");
            result.PlannedOperations.Add("Validate the current fts_search9 schema and triggers before generating version-specific rebuild SQL.");
            result.PlannedOperations.Add("Rebuild the search FTS table with the simple tokenizer only after runtime connection loading is confirmed.");
            result.PlannedOperations.Add("Run Chinese, simplified/traditional and optional pinyin verification queries before marking the migration successful.");
            result.PlannedOperations.Add("On any failure, restore the verified backup rather than attempting partial schema repair.");

            if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
                result.Warnings.Add("DatabasePath does not resolve to an existing library.db.");
            if (string.IsNullOrWhiteSpace(options.NativeExtensionPath) || !File.Exists(options.NativeExtensionPath))
                result.Warnings.Add("NativeExtensionPath does not exist.");
            if (string.IsNullOrWhiteSpace(options.SqliteExecutablePath) || !File.Exists(options.SqliteExecutablePath))
                result.Warnings.Add("SqliteExecutablePath does not exist; active preflight cannot run.");
            if (options.RequireBackup && string.IsNullOrWhiteSpace(backupDirectory))
                result.Warnings.Add("BackupDirectory could not be resolved.");
            if (!string.IsNullOrWhiteSpace(options.CustomDictionaryPath) && !File.Exists(options.CustomDictionaryPath))
                result.Warnings.Add("CustomDictionaryPath does not exist.");

            result.CanApply = result.Warnings.Count == 0;
            result.Success = true;
            if (!result.CanApply)
                result.Error = "Advanced Chinese search is not ready for an Apply operation. Resolve all Plan warnings first.";
            return result;
        }

        private static async Task<ProcessResult> RunSqliteAsync(string executable, string database,
            string script, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = QuoteArgument(database),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();
                await process.StandardInput.WriteAsync(script).ConfigureAwait(false);
                process.StandardInput.Close();

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(3, timeoutSeconds));
                while (!process.HasExited)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        try { process.Kill(); } catch { }
                        return new ProcessResult(-1, await stdoutTask.ConfigureAwait(false),
                            "sqlite3 health test timed out. " + await stderrTask.ConfigureAwait(false));
                    }
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }

                return new ProcessResult(process.ExitCode,
                    await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
            }
        }

        private static string EscapeDotCommandArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static bool ContainsPositiveIntegerLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int value;
                if (int.TryParse(line.Trim(), out value) && value > 0) return true;
            }
            return false;
        }

        private static string FirstNonEmpty(string first, string second)
        {
            if (!string.IsNullOrWhiteSpace(first)) return first.Trim();
            return string.IsNullOrWhiteSpace(second) ? "unknown sqlite3 error" : second.Trim();
        }

        private sealed class ProcessResult
        {
            public ProcessResult(int exitCode, string stdOut, string stdErr)
            {
                ExitCode = exitCode;
                StdOut = stdOut;
                StdErr = stdErr;
            }

            public int ExitCode { get; private set; }
            public string StdOut { get; private set; }
            public string StdErr { get; private set; }
        }
    }
}
