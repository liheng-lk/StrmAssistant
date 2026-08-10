using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.MediaEnhance
{
    public sealed class MediaInfoReliabilitySeedTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;

        public MediaInfoReliabilitySeedTask(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public string Name => "构建 STRM 媒体信息可靠性缓存";
        public string Key => "StrmAssistantMediaInfoReliabilitySeed";
        public string Category => "Strm Assistant";
        public string Description => "把 Emby 数据库中当前已完整的 STRM 核心媒体信息复制到插件可靠性影子缓存。不执行 ffprobe，也不访问 STRM 指向的远端媒体。";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                HasPath = true,
                MediaTypes = new[] { MediaType.Video, MediaType.Audio }
            }) ?? Array.Empty<BaseItem>();

            var candidates = items
                .Where(MediaInfoReliabilityShadowStore.AppliesTo)
                .GroupBy(item => item.InternalId)
                .Select(group => group.First())
                .ToList();

            var captured = 0;
            var incomplete = 0;
            for (var index = 0; index < candidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = candidates[index];
                if (MediaInfoIntegrityService.IsCoreMediaInfoComplete(item))
                {
                    if (MediaInfoReliabilityShadowStore.Capture(item, true)) captured++;
                }
                else
                {
                    incomplete++;
                }

                progress?.Report(candidates.Count == 0 ? 100 : (index + 1) * 100d / candidates.Count);
                if ((index + 1) % 100 == 0) await Task.Yield();
            }

            progress?.Report(100);
            Plugin.Instance?.Logger?.Info(
                "STRM MediaInfo reliability seed completed: candidates={0}, captured={1}, incompleteSkipped={2}",
                candidates.Count, captured, incomplete);
        }
    }
}
