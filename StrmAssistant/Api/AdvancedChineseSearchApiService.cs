using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Compatibility;
using StrmAssistant.Search;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    public sealed class AdvancedChineseSearchSettingsStatus
    {
        public AdvancedChineseSearchOptions Options { get; set; }
        public string SettingsPath { get; set; }
        public AdvancedChineseSearchConnectionCapabilityStatus ConnectionLoader { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    [Route("/StrmAssistant/Search/AdvancedChinese", "GET",
        Summary = "Get guarded advanced Chinese FTS settings")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetAdvancedChineseSearchSettings : IReturn<AdvancedChineseSearchSettingsStatus> { }

    [Route("/StrmAssistant/Search/AdvancedChinese", "POST",
        Summary = "Save guarded advanced Chinese FTS settings without changing library.db")]
    [Authenticated(Roles = "Admin")]
    public sealed class SaveAdvancedChineseSearchSettings : IReturn<AdvancedChineseSearchSettingsStatus>
    {
        public bool Enabled { get; set; }
        public string NativeExtensionPath { get; set; }
        public string SqliteExecutablePath { get; set; }
        public string DatabasePath { get; set; }
        public string BackupDirectory { get; set; }
        public string CustomDictionaryPath { get; set; }
        public bool EnablePinyin { get; set; } = true;
        public bool RequireBackup { get; set; } = true;
    }

    [Route("/StrmAssistant/Search/AdvancedChinese/Health", "GET",
        Summary = "Run read-only advanced Chinese tokenizer preflight")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetAdvancedChineseSearchHealth : IReturn<AdvancedChineseSearchHealthResult>
    {
        public bool RunActiveTokenizerTest { get; set; }
    }

    [Route("/StrmAssistant/Search/AdvancedChinese/Plan", "GET",
        Summary = "Plan an advanced Chinese FTS migration without modifying library.db")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetAdvancedChineseSearchPlan : IReturn<AdvancedChineseSearchPlanResult> { }

    [Route("/StrmAssistant/Search/AdvancedChinese/Apply", "POST",
        Summary = "Apply the simple tokenizer after backup, active preflight and runtime connection-loader verification")]
    [Authenticated(Roles = "Admin")]
    public sealed class ApplyAdvancedChineseSearch : IReturn<AdvancedChineseSearchMigrationResult>
    {
        public bool Confirm { get; set; }
        public bool AcknowledgeImmediateRestart { get; set; }
    }

    [Route("/StrmAssistant/Search/AdvancedChinese/Restore", "POST",
        Summary = "Restore the Emby search FTS tokenizer to unicode61 through a guarded rebuild")]
    [Authenticated(Roles = "Admin")]
    public sealed class RestoreAdvancedChineseSearch : IReturn<AdvancedChineseSearchMigrationResult>
    {
        public bool Confirm { get; set; }
        public bool AcknowledgeImmediateRestart { get; set; }
    }

    public sealed class AdvancedChineseSearchApiService : BaseApiService
    {
        private readonly AdvancedChineseSearchDiagnostics _diagnostics = new AdvancedChineseSearchDiagnostics();
        private readonly AdvancedChineseSearchMigration _migration = new AdvancedChineseSearchMigration();

        public object Get(GetAdvancedChineseSearchSettings request)
        {
            return BuildSettingsStatus();
        }

        public object Post(SaveAdvancedChineseSearchSettings request)
        {
            AdvancedChineseSearchRuntimeSettings.Save(new AdvancedChineseSearchOptions
            {
                Enabled = request?.Enabled == true,
                NativeExtensionPath = request?.NativeExtensionPath,
                SqliteExecutablePath = request?.SqliteExecutablePath,
                DatabasePath = request?.DatabasePath,
                BackupDirectory = request?.BackupDirectory,
                CustomDictionaryPath = request?.CustomDictionaryPath,
                EnablePinyin = request?.EnablePinyin != false,
                RequireBackup = request?.RequireBackup != false
            });
            return BuildSettingsStatus();
        }

        public async Task<object> Get(GetAdvancedChineseSearchHealth request)
        {
            return await _diagnostics.CheckAsync(request?.RunActiveTokenizerTest == true,
                CancellationToken.None).ConfigureAwait(false);
        }

        public object Get(GetAdvancedChineseSearchPlan request)
        {
            return _diagnostics.BuildPlan();
        }

        public async Task<object> Post(ApplyAdvancedChineseSearch request)
        {
            return await _migration.ApplyAsync(request?.Confirm == true,
                request?.AcknowledgeImmediateRestart == true, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<object> Post(RestoreAdvancedChineseSearch request)
        {
            return await _migration.RestoreAsync(request?.Confirm == true,
                request?.AcknowledgeImmediateRestart == true, CancellationToken.None).ConfigureAwait(false);
        }

        private static AdvancedChineseSearchSettingsStatus BuildSettingsStatus()
        {
            var options = AdvancedChineseSearchRuntimeSettings.GetSnapshot();
            var loader = AdvancedChineseSearchConnectionModState.Status;
            var result = new AdvancedChineseSearchSettingsStatus
            {
                Options = options,
                SettingsPath = AdvancedChineseSearchRuntimeSettings.SettingsPath,
                ConnectionLoader = loader
            };

            if (options.Enabled && string.IsNullOrWhiteSpace(options.NativeExtensionPath))
                result.Warnings.Add("Advanced Chinese search is enabled but NativeExtensionPath is empty.");
            if (options.Enabled && string.IsNullOrWhiteSpace(options.SqliteExecutablePath))
                result.Warnings.Add("No sqlite3 executable is configured, so the active in-memory tokenizer preflight cannot run.");
            if (options.Enabled && string.IsNullOrWhiteSpace(options.DatabasePath) &&
                string.IsNullOrWhiteSpace(AdvancedChineseSearchRuntimeSettings.ResolveDatabasePath(options)))
                result.Warnings.Add("library.db could not be auto-resolved; configure DatabasePath explicitly.");
            if (options.Enabled && loader?.Patched != true)
                result.Warnings.Add("The runtime SQLite CreateConnection loader is not patched; Apply will be refused.");
            if (options.Enabled && loader?.Patched == true && loader.LoadAttempts < 1)
                result.Warnings.Add("No runtime SQLite connection has yet been observed loading the tokenizer. Trigger a normal library query/restart and re-check before Apply.");
            if (options.Enabled && loader?.LoadFailures > 0)
                result.Warnings.Add("At least one observed runtime SQLite connection failed to load the tokenizer; Apply will be refused until the runtime issue is resolved.");
            result.Warnings.Add("Apply always creates and integrity-checks a sqlite3 backup, rebuilds only fts_search8/9 in one transaction, and requires immediate Emby restart afterwards.");
            result.Warnings.Add("Restore rebuilds only the FTS table back to unicode61; it never copies a full backup over a live library.db file.");
            return result;
        }
    }
}
