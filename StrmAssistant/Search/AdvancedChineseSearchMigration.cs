using StrmAssistant.Compatibility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Search
{
    public sealed class AdvancedChineseSearchMigrationResult
    {
        public bool Success { get; set; }
        public bool Applied { get; set; }
        public bool Restored { get; set; }
        public bool RestartRequired { get; set; }
        public string DatabasePath { get; set; }
        public string FtsTableName { get; set; }
        public string TokenizerBefore { get; set; }
        public string TokenizerAfter { get; set; }
        public string BackupPath { get; set; }
        public long BackupBytes { get; set; }
        public string BackupIntegrity { get; set; }
        public int ConnectionLoadAttempts { get; set; }
        public int ConnectionLoadSuccesses { get; set; }
        public int ConnectionLoadFailures { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// Guarded FTS migration based on Emby's published/community schema behavior. The operation
    /// never replaces a live library.db file. A consistent sqlite3 .backup is created and checked,
    /// then only fts_search8/9 is rebuilt inside one transaction. Restore rebuilds the same table
    /// back to unicode61 rather than copying the full database backup over a running server.
    /// </summary>
    public sealed class AdvancedChineseSearchMigration
    {
        private readonly AdvancedChineseSearchDiagnostics _diagnostics = new AdvancedChineseSearchDiagnostics();

        public async Task<AdvancedChineseSearchMigrationResult> ApplyAsync(bool confirm,
            bool acknowledgeImmediateRestart, CancellationToken cancellationToken)
        {
            return await ExecuteAsync(false, confirm, acknowledgeImmediateRestart, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<AdvancedChineseSearchMigrationResult> RestoreAsync(bool confirm,
            bool acknowledgeImmediateRestart, CancellationToken cancellationToken)
        {
            return await ExecuteAsync(true, confirm, acknowledgeImmediateRestart, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<AdvancedChineseSearchMigrationResult> ExecuteAsync(bool restore, bool confirm,
            bool acknowledgeImmediateRestart, CancellationToken cancellationToken)
        {
            var options = AdvancedChineseSearchRuntimeSettings.GetSnapshot();
            var databasePath = AdvancedChineseSearchRuntimeSettings.ResolveDatabasePath(options);
            var backupDirectory = AdvancedChineseSearchRuntimeSettings.ResolveBackupDirectory(options, databasePath);
            var appVersion = ResolveApplicationVersion();
            var ftsTable = appVersion >= new Version(4, 8, 3, 0) ? "fts_search9" : "fts_search8";
            var result = new AdvancedChineseSearchMigrationResult
            {
                DatabasePath = databasePath,
                FtsTableName = ftsTable
            };

            if (!confirm)
            {
                result.Error = "Confirm=true is required.";
                return result;
            }
            if (!acknowledgeImmediateRestart)
            {
                result.Error = "AcknowledgeImmediateRestart=true is required. Restart Emby immediately after a successful FTS migration.";
                return result;
            }
            if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            {
                result.Error = "library.db does not exist at the resolved DatabasePath.";
                return result;
            }
            if (string.IsNullOrWhiteSpace(options.SqliteExecutablePath) || !File.Exists(options.SqliteExecutablePath))
            {
                result.Error = "SqliteExecutablePath must point to an existing sqlite3 executable.";
                return result;
            }
            if (string.IsNullOrWhiteSpace(backupDirectory))
            {
                result.Error = "A backup directory could not be resolved.";
                return result;
            }

            if (!restore)
            {
                if (!options.Enabled)
                {
                    result.Error = "Advanced Chinese search must be enabled before Apply.";
                    return result;
                }
                if (string.IsNullOrWhiteSpace(options.NativeExtensionPath) || !File.Exists(options.NativeExtensionPath))
                {
                    result.Error = "NativeExtensionPath must point to an existing tokenizer extension.";
                    return result;
                }

                var health = await _diagnostics.CheckAsync(true, cancellationToken).ConfigureAwait(false);
                if (!health.ActiveTokenizerTestPassed)
                {
                    result.Error = health.Error ?? "The active tokenizer smoke test did not pass.";
                    return result;
                }

                var loader = AdvancedChineseSearchConnectionModState.Status;
                result.ConnectionLoadAttempts = loader?.LoadAttempts ?? 0;
                result.ConnectionLoadSuccesses = loader?.LoadSuccesses ?? 0;
                result.ConnectionLoadFailures = loader?.LoadFailures ?? 0;
                if (loader?.Patched != true)
                {
                    result.Error = "The runtime BaseSqliteRepository.CreateConnection loader is not patched.";
                    return result;
                }
                if (result.ConnectionLoadAttempts < 1 || result.ConnectionLoadSuccesses < 1 ||
                    result.ConnectionLoadFailures > 0 || result.ConnectionLoadSuccesses != result.ConnectionLoadAttempts)
                {
                    result.Error = "Apply is blocked until every observed runtime SQLite connection has loaded the tokenizer successfully and at least one successful load has been observed.";
                    return result;
                }
                if (!string.IsNullOrWhiteSpace(options.CustomDictionaryPath))
                    result.Warnings.Add("CustomDictionaryPath is configured, but dictionary injection is not performed by this migration until the tokenizer's dictionary ABI is verified on the target runtime.");
            }

            var schema = await QuerySchemaAsync(options.SqliteExecutablePath, databasePath, ftsTable,
                cancellationToken).ConfigureAwait(false);
            if (!schema.Success)
            {
                result.Error = schema.Error;
                return result;
            }
            result.TokenizerBefore = DetectTokenizer(schema.Output);
            if (string.Equals(result.TokenizerBefore, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                result.Error = "The existing " + ftsTable + " tokenizer could not be identified; migration was refused.";
                return result;
            }

            var desired = restore ? "unicode61 remove_diacritics 2" : "simple";
            if (string.Equals(result.TokenizerBefore, desired, StringComparison.OrdinalIgnoreCase))
            {
                result.Success = true;
                result.TokenizerAfter = desired;
                result.Warnings.Add("No FTS rebuild was necessary because the requested tokenizer is already active.");
                return result;
            }

            Directory.CreateDirectory(backupDirectory);
            result.BackupPath = Path.Combine(backupDirectory,
                Path.GetFileNameWithoutExtension(databasePath) + ".strmassistant-before-" +
                (restore ? "restore-" : "simple-") + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") +
                Path.GetExtension(databasePath));
            if (File.Exists(result.BackupPath))
            {
                result.Error = "The proposed backup path already exists; refusing to overwrite it.";
                return result;
            }

            var backup = await RunSqliteAsync(options.SqliteExecutablePath, databasePath,
                ".bail on\n.backup " + EscapeDotCommandArgument(result.BackupPath) + "\n.quit\n",
                120, cancellationToken).ConfigureAwait(false);
            if (backup.ExitCode != 0 || !File.Exists(result.BackupPath))
            {
                result.Error = "sqlite3 backup failed: " + FirstNonEmpty(backup.StdErr, backup.StdOut);
                return result;
            }
            result.BackupBytes = new FileInfo(result.BackupPath).Length;
            if (result.BackupBytes <= 0)
            {
                result.Error = "The sqlite3 backup file is empty.";
                return result;
            }

            var integrity = await RunSqliteAsync(options.SqliteExecutablePath, result.BackupPath,
                ".bail on\nPRAGMA integrity_check;\n.quit\n", 120, cancellationToken).ConfigureAwait(false);
            result.BackupIntegrity = integrity.StdOut?.Trim();
            if (integrity.ExitCode != 0 ||
                !string.Equals(result.BackupIntegrity, "ok", StringComparison.OrdinalIgnoreCase))
            {
                result.Error = "The backup did not pass PRAGMA integrity_check: " +
                               FirstNonEmpty(integrity.StdErr, integrity.StdOut);
                return result;
            }

            var migrationScript = BuildMigrationScript(ftsTable, desired, appVersion,
                restore ? null : options.NativeExtensionPath);
            var migration = await RunSqliteAsync(options.SqliteExecutablePath, databasePath,
                migrationScript, 600, cancellationToken).ConfigureAwait(false);
            result.Output = (migration.StdOut + Environment.NewLine + migration.StdErr).Trim();
            if (migration.ExitCode != 0)
            {
                result.Error = "FTS rebuild failed. The SQLite transaction is expected to roll back; keep the verified backup for recovery. " +
                               FirstNonEmpty(migration.StdErr, migration.StdOut);
                return result;
            }

            var after = await QuerySchemaAsync(options.SqliteExecutablePath, databasePath, ftsTable,
                cancellationToken).ConfigureAwait(false);
            result.TokenizerAfter = after.Success ? DetectTokenizer(after.Output) : "unknown";
            if (!after.Success || !string.Equals(result.TokenizerAfter, desired, StringComparison.OrdinalIgnoreCase))
            {
                result.Error = "The FTS rebuild command returned successfully, but the resulting tokenizer could not be verified as " + desired + ".";
                return result;
            }

            result.Success = true;
            result.Applied = !restore;
            result.Restored = restore;
            result.RestartRequired = true;
            result.Warnings.Add("Restart Emby immediately so all SQLite connections are recreated under the current connection-loader policy.");
            return result;
        }

        private static string BuildMigrationScript(string tableName, string tokenizer, Version appVersion,
            string nativeExtensionPath)
        {
            var albumExpression = appVersion >= new Version(4, 9, 0, 0)
                ? "(select case when AlbumId is null then null else (select name from MediaItems where Id = AlbumId limit 1) end)"
                : "Album";
            string Normalize(string expression) => "replace(replace(" + expression + ",'''',''),'.','')";

            var builder = new StringBuilder().AppendLine(".bail on");
            if (!string.IsNullOrWhiteSpace(nativeExtensionPath))
                builder.AppendLine(".load " + EscapeDotCommandArgument(nativeExtensionPath));
            builder.AppendLine("BEGIN IMMEDIATE;")
                .AppendLine("DROP TABLE IF EXISTS " + tableName + ";")
                .AppendLine("CREATE VIRTUAL TABLE " + tableName +
                            " USING FTS5 (Name, OriginalTitle, SeriesName, Album, tokenize=\"" + tokenizer +
                            "\", prefix='1 2 3 4');")
                .AppendLine("INSERT INTO " + tableName + "(RowId, Name, OriginalTitle, SeriesName, Album) " +
                            "SELECT id, " + Normalize("Name") + ", " + Normalize("OriginalTitle") + ", " +
                            Normalize("SeriesName") + ", " + Normalize(albumExpression) + " FROM MediaItems;")
                .AppendLine("COMMIT;")
                .AppendLine("SELECT sql FROM sqlite_master WHERE type='table' AND name='" + tableName + "';")
                .AppendLine("SELECT count(*) FROM " + tableName + ";");
            if (string.Equals(tokenizer, "simple", StringComparison.OrdinalIgnoreCase))
                builder.AppendLine("SELECT simple_query('中文搜索');");
            builder.AppendLine(".quit");
            return builder.ToString();
        }

        private static async Task<QueryResult> QuerySchemaAsync(string executable, string database,
            string tableName, CancellationToken cancellationToken)
        {
            var script = ".bail on\nSELECT sql FROM sqlite_master WHERE type='table' AND name='" + tableName + "';\n.quit\n";
            var run = await RunSqliteAsync(executable, database, script, 30, cancellationToken).ConfigureAwait(false);
            return new QueryResult
            {
                Success = run.ExitCode == 0 && !string.IsNullOrWhiteSpace(run.StdOut),
                Output = run.StdOut,
                Error = run.ExitCode == 0
                    ? (string.IsNullOrWhiteSpace(run.StdOut) ? tableName + " was not found in library.db." : null)
                    : FirstNonEmpty(run.StdErr, run.StdOut)
            };
        }

        private static string DetectTokenizer(string schema)
        {
            if (string.IsNullOrWhiteSpace(schema)) return "unknown";
            var normalized = schema.ToLowerInvariant().Replace(" ", string.Empty)
                .Replace("'", "\"").Replace("`", string.Empty);
            if (normalized.Contains("tokenize=\"simple\"") || normalized.Contains("tokenize=simple"))
                return "simple";
            if (normalized.Contains("tokenize=\"unicode61remove_diacritics2\"") ||
                normalized.Contains("tokenize=unicode61remove_diacritics2"))
                return "unicode61 remove_diacritics 2";
            return "unknown";
        }

        private static Version ResolveApplicationVersion()
        {
            try
            {
                var value = Plugin.Instance?.ApplicationHost?.ApplicationVersion?.ToString();
                if (Version.TryParse(value, out var version)) return version;
            }
            catch { }
            return new Version(4, 9, 0, 0);
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
                            "sqlite3 operation timed out. " + await stderrTask.ConfigureAwait(false));
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

        private static string FirstNonEmpty(string first, string second)
        {
            if (!string.IsNullOrWhiteSpace(first)) return first.Trim();
            return string.IsNullOrWhiteSpace(second) ? "unknown sqlite3 error" : second.Trim();
        }

        private sealed class QueryResult
        {
            public bool Success { get; set; }
            public string Output { get; set; }
            public string Error { get; set; }
        }

        private sealed class ProcessResult
        {
            public ProcessResult(int exitCode, string stdOut, string stdErr)
            {
                ExitCode = exitCode;
                StdOut = stdOut;
                StdErr = stdErr;
            }

            public int ExitCode { get; }
            public string StdOut { get; }
            public string StdErr { get; }
        }
    }
}
