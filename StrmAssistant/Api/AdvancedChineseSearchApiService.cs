using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
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

    public sealed class AdvancedChineseSearchApiService : BaseApiService
    {
        private readonly AdvancedChineseSearchDiagnostics _diagnostics = new AdvancedChineseSearchDiagnostics();

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

        private static AdvancedChineseSearchSettingsStatus BuildSettingsStatus()
        {
            var options = AdvancedChineseSearchRuntimeSettings.GetSnapshot();
            var result = new AdvancedChineseSearchSettingsStatus
            {
                Options = options,
                SettingsPath = AdvancedChineseSearchRuntimeSettings.SettingsPath
            };

            if (options.Enabled && string.IsNullOrWhiteSpace(options.NativeExtensionPath))
                result.Warnings.Add("Advanced Chinese search is enabled but NativeExtensionPath is empty.");
            if (options.Enabled && string.IsNullOrWhiteSpace(options.SqliteExecutablePath))
                result.Warnings.Add("No sqlite3 executable is configured, so the active in-memory tokenizer preflight cannot run.");
            if (options.Enabled && string.IsNullOrWhiteSpace(options.DatabasePath) &&
                string.IsNullOrWhiteSpace(AdvancedChineseSearchRuntimeSettings.ResolveDatabasePath(options)))
                result.Warnings.Add("library.db could not be auto-resolved; configure DatabasePath explicitly.");
            result.Warnings.Add("Apply/Restore is intentionally not exposed until the runtime pooled-connection extension loader and version-specific FTS schema adapter are verified.");
            return result;
        }
    }
}
