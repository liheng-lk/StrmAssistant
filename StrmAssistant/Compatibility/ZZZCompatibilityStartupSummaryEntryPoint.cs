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
                var pluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown";

                logger.Info(
                    "CompatibilitySummary - Plugin={0}; Emby={1}; Band={2}; Detect={3}; " +
                    "MediaInfo={4}/{5} nativeArgs={6}; Fingerprint={7}/{8} native={9}/{10}; " +
                    "StrmMount={11}/{12} arg={13}; Thumbnail={14}/{15} nativeArgs={16}; " +
                    "ChineseSearch={17}/{18} target={19}; MultiVersion={20}/{21}; MovieDbLoaded={22}",
                    pluginVersion,
                    emby.ServerVersion?.ToString() ?? "unknown",
                    emby.Band,
                    emby.DetectionSource ?? "unknown",
                    mediaInfo?.TargetFound == true,
                    mediaInfo?.Patched == true || mediaInfo?.ReflectionStaticMediaSourceAvailable == true,
                    mediaInfo?.RuntimeStaticMediaSourceParameterCount ?? 0,
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
