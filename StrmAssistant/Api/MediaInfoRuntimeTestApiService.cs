using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;
using StrmAssistant.MediaEnhance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Api
{
    [Route("/StrmAssistant/MediaInfo/{Id}/RuntimeTest", "GET",
        Summary = "Read-only MediaInfo reliability runtime test status for one Emby item")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetMediaInfoRuntimeTest : IReturn<MediaInfoRuntimeTestResponse>
    {
        public string Id { get; set; }
    }

    [Route("/StrmAssistant/MediaInfo/{Id}/RuntimeTest", "POST",
        Summary = "Run explicitly confirmed non-destructive MediaInfo runtime verification for one item")]
    [Authenticated(Roles = "Admin")]
    public sealed class ExecuteMediaInfoRuntimeTest : IReturn<MediaInfoRuntimeTestResponse>
    {
        public string Id { get; set; }
        public bool ConfirmRoundTrip { get; set; }
        public bool ConfirmShadowCapture { get; set; }
        public bool ConfirmRecovery { get; set; }
    }

    public sealed class MediaInfoRuntimeTestResponse
    {
        public bool Success { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string ItemPath { get; set; }
        public MediaInfoIntegrityAssessment Before { get; set; }
        public MediaInfoIntegrityAssessment After { get; set; }
        public bool RoundTripRequested { get; set; }
        public bool RoundTripAttempted { get; set; }
        public bool RoundTripVerified { get; set; }
        public bool ShadowCaptureRequested { get; set; }
        public bool ShadowCaptureAttempted { get; set; }
        public bool ShadowCaptureVerified { get; set; }
        public bool RecoveryRequested { get; set; }
        public bool RecoveryAttempted { get; set; }
        public bool RecoveryVerified { get; set; }
        public string StreamSignatureBefore { get; set; }
        public string StreamSignatureAfter { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    public sealed class MediaInfoRuntimeTestApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;

        public MediaInfoRuntimeTestApiService(ILibraryManager libraryManager, IItemRepository itemRepository)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
        }

        public object Get(GetMediaInfoRuntimeTest request)
        {
            var item = Resolve(request?.Id);
            if (item == null) return Error(request?.Id, "Item was not found or id is invalid.");
            var assessment = MediaInfoIntegrityService.Assess(item);
            return new MediaInfoRuntimeTestResponse
            {
                Success = true,
                ItemId = item.InternalId.ToString(),
                ItemName = item.Name,
                ItemPath = item.Path,
                Before = assessment,
                After = assessment,
                StreamSignatureBefore = BuildStreamSignature(item),
                StreamSignatureAfter = BuildStreamSignature(item),
                Warnings = BuildReadOnlyWarnings(assessment)
            };
        }

        public async Task<object> Post(ExecuteMediaInfoRuntimeTest request)
        {
            var item = Resolve(request?.Id);
            if (item == null) return Error(request?.Id, "Item was not found or id is invalid.");

            var response = new MediaInfoRuntimeTestResponse
            {
                ItemId = item.InternalId.ToString(),
                ItemName = item.Name,
                ItemPath = item.Path,
                RoundTripRequested = request?.ConfirmRoundTrip == true,
                ShadowCaptureRequested = request?.ConfirmShadowCapture == true,
                RecoveryRequested = request?.ConfirmRecovery == true
            };
            response.Before = MediaInfoIntegrityService.Assess(item);
            response.StreamSignatureBefore = BuildStreamSignature(item);

            if (!response.RoundTripRequested && !response.ShadowCaptureRequested && !response.RecoveryRequested)
            {
                response.After = response.Before;
                response.StreamSignatureAfter = response.StreamSignatureBefore;
                response.Warnings.Add("No runtime action was confirmed. Set an explicit Confirm* flag after reviewing the GET response.");
                response.Success = true;
                return response;
            }

            if (response.RoundTripRequested)
                ExecuteRoundTrip(item, response);

            item = Resolve(request.Id) ?? item;
            if (response.ShadowCaptureRequested)
                ExecuteShadowCapture(item, response);

            item = Resolve(request.Id) ?? item;
            if (response.RecoveryRequested)
                await ExecuteRecoveryAsync(item, response).ConfigureAwait(false);

            var after = Resolve(request.Id) ?? item;
            response.After = MediaInfoIntegrityService.Assess(after);
            response.StreamSignatureAfter = BuildStreamSignature(after);

            response.Success = response.Errors.Count == 0 &&
                               (!response.RoundTripRequested || response.RoundTripVerified) &&
                               (!response.ShadowCaptureRequested || response.ShadowCaptureVerified) &&
                               (!response.RecoveryRequested || response.RecoveryVerified);
            return response;
        }

        private void ExecuteRoundTrip(BaseItem item, MediaInfoRuntimeTestResponse response)
        {
            if (response.Before?.CoreMediaInfoComplete != true)
            {
                response.Errors.Add("Round-trip write verification requires an item whose core MediaInfo is already complete; no write was attempted.");
                return;
            }

            try
            {
                var streams = item.GetMediaStreams()?.Where(stream => stream != null).ToList() ?? new List<MediaStream>();
                var runtime = item.RunTimeTicks;
                var size = item.Size;
                var container = item.Container;
                var bitrate = item.TotalBitrate;
                var width = item.Width;
                var height = item.Height;
                var beforeSignature = BuildStreamSignature(streams);

                response.RoundTripAttempted = true;
                _itemRepository.SaveMediaStreams(item.InternalId, streams, CancellationToken.None);
                _libraryManager.UpdateItems(new List<BaseItem> { item }, null,
                    ItemUpdateType.MetadataImport, false, false, null, CancellationToken.None);

                var fresh = _libraryManager.GetItemById(item.InternalId);
                if (fresh == null)
                {
                    response.Errors.Add("Round-trip write completed but the item could not be reloaded from ILibraryManager.");
                    return;
                }

                var afterSignature = BuildStreamSignature(fresh);
                response.RoundTripVerified = MediaInfoIntegrityService.IsCoreMediaInfoComplete(fresh) &&
                                             runtime == fresh.RunTimeTicks &&
                                             size == fresh.Size &&
                                             string.Equals(container ?? string.Empty, fresh.Container ?? string.Empty,
                                                 StringComparison.OrdinalIgnoreCase) &&
                                             bitrate == fresh.TotalBitrate &&
                                             width == fresh.Width && height == fresh.Height &&
                                             string.Equals(beforeSignature, afterSignature, StringComparison.Ordinal);
                if (!response.RoundTripVerified)
                    response.Errors.Add("SaveMediaStreams/UpdateItems returned, but re-read values did not match the pre-write MediaInfo snapshot.");
            }
            catch (Exception ex)
            {
                response.Errors.Add("Round-trip repository verification failed: " + ex.GetBaseException().Message);
            }
        }

        private static void ExecuteShadowCapture(BaseItem item, MediaInfoRuntimeTestResponse response)
        {
            if (!MediaInfoReliabilityShadowStore.AppliesTo(item))
            {
                response.Errors.Add("Shadow capture verification applies only to STRM/shortcut items.");
                return;
            }
            if (!MediaInfoIntegrityService.IsCoreMediaInfoComplete(item))
            {
                response.Errors.Add("Shadow capture requires complete core MediaInfo; no shadow write was attempted.");
                return;
            }

            try
            {
                response.ShadowCaptureAttempted = true;
                var captured = MediaInfoReliabilityShadowStore.Capture(item, true);
                response.ShadowCaptureVerified = captured && MediaInfoReliabilityShadowStore.Exists(item);
                if (!response.ShadowCaptureVerified)
                    response.Errors.Add("Shadow Capture returned without a subsequently validated shadow snapshot.");
            }
            catch (Exception ex)
            {
                response.Errors.Add("Shadow capture verification failed: " + ex.GetBaseException().Message);
            }
        }

        private static async Task ExecuteRecoveryAsync(BaseItem item, MediaInfoRuntimeTestResponse response)
        {
            var before = MediaInfoIntegrityService.Assess(item);
            if (before.CoreMediaInfoComplete)
            {
                response.Warnings.Add("Recovery was requested, but core MediaInfo is already complete. No destructive clearing is performed by RuntimeTest; use a naturally incomplete/recoverable test item to verify the restore path.");
                response.RecoveryVerified = true;
                return;
            }
            if (!before.Recoverable)
            {
                response.Errors.Add("Recovery was requested, but no validated persistence/shadow source exists. RuntimeTest will not invoke a remote probe or synthesize MediaInfo.");
                return;
            }

            try
            {
                response.RecoveryAttempted = true;
                var recovered = await MediaInfoIntegrityMonitor.RecoverAsync(item,
                    "Admin RuntimeTest", CancellationToken.None).ConfigureAwait(false);
                var fresh = Plugin.Instance?.ApplicationHost?.Resolve<ILibraryManager>()?.GetItemById(item.InternalId) ?? item;
                response.RecoveryVerified = recovered && MediaInfoIntegrityService.IsCoreMediaInfoComplete(fresh);
                if (!response.RecoveryVerified)
                    response.Errors.Add("Local recovery returned without restoring complete core MediaInfo on re-read.");
            }
            catch (Exception ex)
            {
                response.Errors.Add("Local recovery runtime verification failed: " + ex.GetBaseException().Message);
            }
        }

        private BaseItem Resolve(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !long.TryParse(id, out var internalId)) return null;
            try { return _libraryManager.GetItemById(internalId); }
            catch { return null; }
        }

        private static List<string> BuildReadOnlyWarnings(MediaInfoIntegrityAssessment assessment)
        {
            var result = new List<string>();
            if (assessment == null) return result;
            if (assessment.CoreMediaInfoComplete)
                result.Add("Core MediaInfo is complete. POST ConfirmRoundTrip=true can verify real repository round-trip without changing semantic values.");
            if (assessment.IsShortcut && assessment.CoreMediaInfoComplete)
                result.Add("POST ConfirmShadowCapture=true can force and verify a schema-v3 shadow write for this STRM.");
            if (!assessment.CoreMediaInfoComplete && assessment.Recoverable)
                result.Add("This item is naturally incomplete but locally recoverable. POST ConfirmRecovery=true can verify the real restore path without any remote media probe.");
            if (!assessment.CoreMediaInfoComplete && !assessment.Recoverable)
                result.Add("This item is incomplete and has no validated local recovery source. RuntimeTest intentionally will not probe the remote media.");
            return result;
        }

        private static string BuildStreamSignature(BaseItem item)
        {
            try { return BuildStreamSignature(item?.GetMediaStreams()?.Where(stream => stream != null)); }
            catch { return string.Empty; }
        }

        private static string BuildStreamSignature(IEnumerable<MediaStream> streams)
        {
            return string.Join("|", (streams ?? Enumerable.Empty<MediaStream>())
                .Where(stream => stream != null)
                .OrderBy(stream => stream.Index)
                .ThenBy(stream => stream.Type)
                .Select(stream => string.Join(":", new[]
                {
                    stream.Index.ToString(),
                    stream.Type.ToString(),
                    stream.IsExternal ? "E" : "I",
                    stream.Codec ?? string.Empty,
                    stream.Width?.ToString() ?? string.Empty,
                    stream.Height?.ToString() ?? string.Empty,
                    stream.Channels?.ToString() ?? string.Empty,
                    stream.Path ?? string.Empty
                })));
        }

        private static MediaInfoRuntimeTestResponse Error(string id, string error)
        {
            return new MediaInfoRuntimeTestResponse
            {
                Success = false,
                ItemId = id,
                Errors = new List<string> { error }
            };
        }
    }
}
