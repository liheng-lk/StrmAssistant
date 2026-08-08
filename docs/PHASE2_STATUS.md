# Phase 2 — Media enhancement status

This document tracks implementation and validation of the media-enhancement portion of the full-feature roadmap.

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

## Still to implement in Phase 2

- [ ] ISO MediaInfo extraction/mount workflow.
- [ ] ISO image-capture support.
- [ ] Distributed extraction / rffmpeg configuration and health checks.
- [ ] Custom ffmpeg executable support where needed by image capture.
- [ ] Additional music album/artist persistence behavior if runtime testing shows parent metadata is not restored by the current Audio lifecycle.

## Validation gates

- [ ] Emby Core 4.8.0.80 compile/package after current Phase 2 changes.
- [ ] Emby Core 4.9.1.90 compile/package after current Phase 2 changes.
- [ ] Emby Core 4.10.0.1-beta compile/package after current Phase 2 changes.
- [ ] Real Emby 4.9.5 runtime: external audio resolver initialization.
- [ ] Real movie/episode external-audio add/remove/rescan test.
- [ ] Music JSON save/restore test with embedded artwork.
- [ ] Blacklist tag/keyword smoke test for MediaInfo, fingerprint and BIF.

Compile success is not a substitute for the runtime tests above.
