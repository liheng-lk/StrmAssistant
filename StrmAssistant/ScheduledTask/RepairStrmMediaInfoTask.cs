using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using StrmAssistant.MediaEnhance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.ScheduledTask
{
    /// <summary>
    /// Manual-only repair pass for STRM items whose core internal A/V MediaInfo is incomplete.
    /// Validated persistence/shadow recovery is always attempted first. Only items with no working
    /// local recovery source are sent through one explicit Emby media-info refresh.
    /// </summary>
    public sealed class RepairStrmMediaInfoTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly StrmMediaInfoRepairService _repairService;

        public RepairStrmMediaInfoTask(ILibraryManager libraryManager, IProviderManager providerManager)
        {
            _libraryManager = libraryManager;
            _repairService = new StrmMediaInfoRepairService(libraryManager, providerManager);
        }

        public string Name => "修复缺失的 STRM 媒体信息";
        public string Key => "StrmAssistantRepairMissingStrmMediaInfo";
        public string Category => "Strm Assistant";
        public string Description =>
            "手工修复核心媒体信息不完整的 STRM。优先从本地持久化/可靠性缓存恢复；没有可用快照时才访问媒体源执行一次 Emby MediaInfo 重建。不会自动定时运行。";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var all = _libraryManager.GetItemList(new InternalItemsQuery
            {
                HasPath = true,
                MediaTypes = new[] { MediaType.Video, MediaType.Audio }
            }) ?? Array.Empty<BaseItem>();

            var candidates = all
                .Where(MediaInfoReliabilityShadowStore.AppliesTo)
                .Where(item => Plugin.LibraryApi?.IsLibraryInScope(item) == true)
                .Where(item => !MediaInfoIntegrityService.IsCoreMediaInfoComplete(item))
                .GroupBy(item => item.InternalId)
                .Select(group => group.First())
                .ToList();

            if (candidates.Count == 0)
            {
                progress?.Report(100);
                Plugin.Instance?.Logger?.Info("STRM MediaInfo repair task: no incomplete in-scope STRM items were found.");
                return;
            }

            var configured = Plugin.Instance?.GetPluginOptions()?.GeneralOptions?.MaxConcurrentCount ?? 1;
            var concurrency = Math.Max(1, Math.Min(2, configured));
            var completed = 0;
            var succeeded = 0;
            var localRecovered = 0;
            var remoteRebuilt = 0;
            var failed = 0;

            Plugin.Instance?.Logger?.Info(
                "STRM MediaInfo repair task starting: candidates={0}, concurrency={1}. Local recovery is attempted before remote rebuild.",
                candidates.Count, concurrency);

            for (var offset = 0; offset < candidates.Count; offset += concurrency)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = candidates.Skip(offset).Take(concurrency).ToArray();
                var tasks = batch.Select(async item =>
                {
                    try
                    {
                        var result = await _repairService.RepairAsync(item, true,
                                "Scheduled STRM MediaInfo Repair", cancellationToken)
                            .ConfigureAwait(false);
                        if (result.Success)
                        {
                            Interlocked.Increment(ref succeeded);
                            if (result.LocalRecoverySucceeded) Interlocked.Increment(ref localRecovered);
                            if (result.RemoteRebuildSucceeded) Interlocked.Increment(ref remoteRebuilt);
                        }
                        else
                        {
                            Interlocked.Increment(ref failed);
                            Plugin.Instance?.Logger?.Warn("STRM MediaInfo repair failed: {0} - {1}",
                                item.Path, result.Error);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        Plugin.Instance?.Logger?.Warn("STRM MediaInfo repair exception: {0} - {1}",
                            item.Path, ex.GetBaseException().Message);
                    }
                    finally
                    {
                        var done = Interlocked.Increment(ref completed);
                        progress?.Report(done * 100d / candidates.Count);
                    }
                }).ToArray();

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            progress?.Report(100);
            Plugin.Instance?.Logger?.Info(
                "STRM MediaInfo repair task completed: candidates={0}, succeeded={1}, localRecovered={2}, remoteRebuilt={3}, failed={4}.",
                candidates.Count, succeeded, localRecovered, remoteRebuilt, failed);
        }
    }
}
