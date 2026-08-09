# Phase 2 — Media enhancement status

This document tracks implementation and validation of the media-enhancement portion of the full-feature roadmap.

Compile/package success is **not** treated as runtime verification. Features that touch ffprobe, rffmpeg, mounted paths, Blu-ray parsing or Emby database state remain gated until tested on a disposable real server.

## Implemented in source

### Unified extraction blacklist

- [x] Configuration switch (default off).
- [x] Tag blacklist.
- [x] Keyword blacklist against title/original title/path.
- [x] MediaInfo pre-extract/catch-up filtering.
- [x] Final MediaInfo orchestration guard before provider refresh/ffprobe work.
- [x] Intro fingerprint queue/task/direct-call filtering.
- [x] Video thumbnail/BIF task/direct-call filtering.

### Music MediaInfo persistence

- [x] Explicit configuration switch.
- [x] Audio item add/remove lifecycle.
- [x] Catch-up/scheduled MediaInfo orchestration can persist Audio when enabled.
- [x] Reuses existing MediaInfo JSON serialization of audio streams.
- [x] Reuses existing embedded primary-image Base64 save/restore support.
- [x] Honors Restore mode cleanup behavior.
- [x] Honors extraction blacklist.

### External audio-track scanning

- [x] Runtime gate: Emby Server >= 4.9.1.80.
- [x] Resolve `Emby.Providers.MediaInfo.AudioTrackResolver` dynamically.
- [x] Resolve the public `BaseTrackResolver.GetExternalTracks` behavior dynamically so the plugin remains build-compatible with 4.8.
- [x] Detect changes to external audio files alongside external subtitles.
- [x] Probe discovered audio streams and persist them through `IItemRepository.SaveMediaStreams`.
- [x] Preserve subtitle-only behavior on Emby 4.8.
- [x] Optional setting; unsupported servers automatically ignore it.

### ISO / BDMV optical-media pipeline

- [x] Optical-media type detection with runtime reflection for server-internal `VideoType` / `IsoType` details.
- [x] BDMV directory detection fallback by path/layout.
- [x] ISO/IMG detection fallback by extension.
- [x] Dedicated configurable ffprobe executable and timeout.
- [x] `bluray:` ffprobe input for Blu-ray ISO/BDMV.
- [x] Read-only ffprobe health endpoint with `bluray` protocol detection.
- [x] Read-only per-item optical probe endpoint returning streams, chapters, duration and format information.
- [x] Admin-only write-back plan endpoint.
- [x] Admin-only Apply endpoint requiring both the write-back option and `Confirm=true` per item.
- [x] Write-back of embedded streams, chapters, runtime, bitrate and dimensions.
- [x] Preserve current external streams during write-back.
- [x] Preserve existing intro/credits marker chapters while replacing ordinary chapters.
- [x] Best-effort rollback if MediaStream/chapter/item update fails.
- [x] Runtime-optional BDMV BDInfo/MPLS enrichment.
- [x] Multi-M2TS BDMV playlists use Emby Blu-ray examiner output for stream language/layout, runtime and chapters when available.
- [x] ffprobe video width/height/bitrate/frame/color information fills BDInfo gaps.
- [x] Emby 4.8 automatically degrades to ffprobe-only when the Blu-ray examiner interface is absent.
- [ ] DVD ISO integration.
- [ ] Real Blu-ray ISO runtime verification.
- [ ] Real multi-M2TS BDMV runtime verification.
- [ ] ISO/BDMV image capture and BIF/chapter-image generation.

### Distributed MediaInfo extraction / rffmpeg

- [x] Configuration for custom/distributed ffprobe and ffmpeg executables.
- [x] Optional real rffmpeg executable path for `rffmpeg status`.
- [x] Health endpoint for ffprobe/ffmpeg/rffmpeg.
- [x] Detect `bluray` and `smb` protocols.
- [x] Detect chromaprint, Vulkan and libplacebo build flags.
- [x] Optional active Vulkan + libplacebo ffmpeg smoke test.
- [x] Detect common rffmpeg wrapper/backend log signatures.
- [x] Read-only per-item distributed Probe endpoint.
- [x] Probe confirms whether the configured wrapper/worker can open the exact media path and returns a stream/chapter summary.
- [x] STRM Probe requires explicit target resolution so `.strm` text is never mistaken for media.
- [x] Optional distributed MediaInfo routing in the normal extraction orchestration.
- [x] Routing is default-off.
- [x] Routing only handles actual MediaInfo misses; items queued only for image capture continue through native Emby behavior.
- [x] ISO/BDMV stays on the dedicated optical-media pipeline.
- [x] STRM distributed routing is default-off because mounted paths may not exist on workers.
- [x] Configurable distributed extraction timeout.
- [x] Configurable fallback to native Emby extraction after remote failure.
- [x] Successful distributed extraction persists media streams/chapters/basic media fields.
- [x] Existing external streams and intro/credits markers are preserved.
- [x] Best-effort rollback on write-back failure.
- [x] Existing QueueManager concurrency still limits distributed MediaInfo jobs.
- [x] External subtitle/audio rescan and MediaInfo JSON persistence continue after a successful distributed extract.
- [ ] Real rffmpeg worker routing test with a shared media path.
- [ ] Real multi-worker load-balancing test.
- [ ] Shared-path/permission diagnostics beyond an actual ffprobe Probe request.
- [ ] Route image capture/BIF/fingerprint work through distributed ffmpeg.
- [ ] Central-server MediaInfo synchronization endpoint/workflow.

## Remaining Phase 2 work

- [ ] ISO/BDMV image capture.
- [ ] Custom/distributed ffmpeg image capture and BIF generation.
- [ ] Distributed fingerprint processing through compatible ffmpeg/chromaprint workers.
- [ ] Central-server MediaInfo synchronization workflow.
- [ ] Additional music album/artist persistence behavior if runtime testing shows parent metadata is not restored by the current Audio lifecycle.

## Compile/package validation

The following compatibility milestones are green:

- Run `31255226989` / #52 — blacklist, music persistence and external-audio scanning.
- Run `31291116402` / #59 — ISO/BDMV read-only probe after cross-version reflection fixes.
- Run `31291229048` / #62 — guarded optical write-back.
- Run `31291440676` / #66 — runtime-optional BDMV BDInfo/MPLS enrichment.
- Run `31291604737` / #69 — distributed tool health API.
- Run `31291822556` / #72 — optional distributed MediaInfo routing.
- Run `31291950394` / #74 — read-only per-item distributed path Probe API.

Latest run #74 passed all matrix targets:

- [x] Emby Core 4.8.0.80 compile/package/artifact.
- [x] Emby Core 4.9.1.90 compile/package/artifact.
- [x] Emby Core 4.10.0.1-beta compile/package/artifact.

## Runtime validation still required

- [ ] Real Emby 4.8 startup/configuration smoke test.
- [ ] Real Emby 4.9.x startup/configuration smoke test.
- [ ] Real Emby 4.10 test-build startup/configuration smoke test.
- [ ] External audio resolver initialization and add/remove/rescan test.
- [ ] Music JSON save/restore with embedded artwork.
- [ ] Blacklist tag/keyword smoke test for MediaInfo, fingerprint and BIF.
- [ ] Optical ffprobe Health on the target host.
- [ ] Real Blu-ray ISO Probe, Plan and Apply test on disposable media.
- [ ] Real multi-file BDMV BDInfo/MPLS Probe, Plan and Apply test.
- [ ] rffmpeg Health + status test.
- [ ] Distributed Probe against a path visible with the same spelling on a worker.
- [ ] Distributed routing success and remote-failure/native-fallback test.
- [ ] STRM distributed routing only after target-path parity is confirmed.
