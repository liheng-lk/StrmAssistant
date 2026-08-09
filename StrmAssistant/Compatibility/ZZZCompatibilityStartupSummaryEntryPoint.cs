using MediaBrowser.Controller.Plugins;
using System;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    /// <summary>
    /// Emits a compact, non-secret runtime compatibility summary after startup. This makes
    /// embyserver.txt sufficient for 4.10 compatibility triage without requiring a manually
    /// authenticated diagnostics HTTP request.
    /// </summary>
    public sealed class ZZZCompatibilityStartupSummaryEntryPoint : IServerEntryPoint
    {
        public void Run()
        {
            _ = LogAfterEntryPointsSettleAsync();
        }

        private static async Task LogAfterEntryPointsSettleAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            try
            {
                var plugin = Plugin.Instance;
                var logger = plugin?.Logger;
                if (logger == null) return;

                var emby = EmbyRuntimeCompatibility.Detect(plugin.ApplicationHost);
                var runtime = RuntimeModState.Status;
                var mediaInfo = MediaInfoRuntimeFallbackState.Status;
                var fingerprint = Fingerprint410CompatibilityState.Status;
                var strm = StrmMount410CompatibilityState.Status;
                var thumbnail = VideoThumbnail410CompatibilityState.Status;
                var assembly = AssemblyResolutionCompatibilityState.Status;
                var multiVersion = MultiVersionDisplayModState.Status;

                logger.Info(
                    "CompatibilitySummary - Emby={0}; Band={1}; Detect={2}; " +
                    "MediaInfo={3}/{4}; Fingerprint={5}/{6} native={7}/{8}; " +
                    "StrmMount={9}/{10} arg={11}; Thumbnail={12}/{13} nativeArgs={14}; " +
                    "ChineseSearch={15}/{16} target={17}; MultiVersion={18}/{19}; MovieDbLoaded={20}",
                    emby.ServerVersion?.ToString() ?? "unknown",
                    emby.Band,
                    emby.DetectionSource ?? "unknown",
                    mediaInfo?.TargetFound == true,
                    mediaInfo?.Patched == true || mediaInfo?.ReflectionStaticMediaSourceAvailable == true,
                    fingerprint?.SeasonFingerprintTargetFound == true && fingerprint?.UpdateSequenceTargetFound == true,
                    fingerprint?.SeasonFingerprintPatched == true && fingerprint?.UpdateSequencePatched == true,
                    fingerprint?.NativeSeasonFingerprintParameterCount ?? 0,
                    fingerprint?.NativeUpdateSequenceParameterCount ?? 0,
                    strm?.MountMethodFound == true,
                    strm?.Patched == true,
                    strm?.MountArgumentType ?? "unknown",
                    thumbnail?.NativeRefreshMethodFound == true,
                    thumbnail?.Patched == true,
                    thumbnail?.NativeParameterCount ?? 0,
                    runtime?.CreateSearchTermTargetFound == true,
                    runtime?.CreateSearchTermPatched == true,
                    runtime?.CreateSearchTermTarget ?? "unknown",
                    multiVersion?.TargetFound == true,
                    multiVersion?.Patched == true,
                    assembly?.MovieDbAlreadyLoaded == true);

                if (!string.IsNullOrWhiteSpace(mediaInfo?.Error))
                    logger.Warn("CompatibilitySummary - MediaInfo: " + mediaInfo.Error);
                if (!string.IsNullOrWhiteSpace(fingerprint?.Error))
                    logger.Warn("CompatibilitySummary - Fingerprint: " + fingerprint.Error);
                if (!string.IsNullOrWhiteSpace(strm?.Error))
                    logger.Warn("CompatibilitySummary - STRM mount: " + strm.Error);
                if (!string.IsNullOrWhiteSpace(thumbnail?.Error))
                    logger.Warn("CompatibilitySummary - Thumbnail: " + thumbnail.Error);
                if (!string.IsNullOrWhiteSpace(multiVersion?.Error))
                    logger.Warn("CompatibilitySummary - Multi-version: " + multiVersion.Error);
                if (!string.IsNullOrWhiteSpace(assembly?.Error))
                    logger.Warn("CompatibilitySummary - MovieDb assembly: " + assembly.Error);
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Debug("CompatibilitySummary generation skipped: " + ex.Message);
            }
        }

        public void Dispose()
        {
        }
    }
}
