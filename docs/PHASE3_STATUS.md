# Phase 3 — Runtime Mods, Metadata and Multi-Version Status

This document tracks the post-Phase-2 feature expansion in the community GPL fork.

**Status vocabulary**

- `source complete` — implementation exists on `agent/custom-rebuild`.
- `matrix green` — JavaScript validation, C# build, ILRepack packaging and artifact upload passed for the tested Emby Core matrix.
- `runtime pending` — a real disposable Emby server still needs to validate runtime-private method binding and behavior.

Compile/package success is never treated as runtime verification.

## Runtime compatibility / Harmony host

- [x] Minimal isolated Harmony host; no third-party PRO code or license behavior is used.
- [x] Plugin target remains `netstandard2.1`.
- [x] `Lib.Harmony 2.3.6` runtime implementation is merged into the single plugin DLL during packaging.
- [x] Harmony packaging baseline passed Emby Core 4.8.0.80 / 4.9.1.90 / 4.10.0.1-beta in run #115.
- [x] Each high-coupling mod has its own Harmony ID/state and can degrade independently.
- [ ] Real-server startup/unload/reload verification with the merged Harmony runtime.

## General — proxy and Chinese search

### Selective proxy

- [x] Default-off proxy enhancement.
- [x] Global public-network mode.
- [x] Domain whitelist mode.
- [x] RFC1918 / loopback / link-local bypass.
- [x] Additional bypass hosts and local discovery address.
- [x] HTTP/HTTPS proxy URLs with optional URL credentials.
- [x] `HttpClientHandler` direct configuration.
- [x] Other handler types use runtime `Proxy` / `UseProxy` property discovery instead of a hard `SocketsHttpHandler` compile dependency.
- [x] Read-only `/StrmAssistant/Diagnostics/ProxyRoute` route evaluation; it makes no outbound request.
- [ ] Real MovieDb/TVDB request verification through a configured proxy.

### Chinese search compatible layer

- [x] Default-off search enhancement.
- [x] Patch only `SqliteItemRepository.CreateSearchTerm(string)` when available.
- [x] Preserve the original FTS term.
- [x] Add Simplified and Traditional Chinese variants with a thread-local recursion guard.
- [x] No `library.db` schema migration.
- [x] No native tokenizer requirement.
- [x] Read-only `/StrmAssistant/Diagnostics/ChineseSearch` variant preview.
- [x] Matrix green in run #119.
- [ ] Real library search smoke test on 4.8/4.9/4.10.
- [ ] Native/adaptive CJK tokenizer layer and safe FTS rebuild/migration.

## Experience — people display filter

- [x] Default-off DTO-only filtering.
- [x] Hide people without primary image.
- [x] Actors/guest-stars-only mode.
- [x] Hide non-CJK person names.
- [x] Does not delete Person items or metadata.
- [ ] Real detail-page DTO/UI verification.

## Metadata — Pinyin sorting

- [x] `TinyPinyin 1.1.0` bundled into the single plugin DLL.
- [x] Runtime-only `BaseItem.CreateSortName(ReadOnlySpan<char>)` enhancement.
- [x] Locked SortName is preserved.
- [x] No bulk database rewrite.
- [x] Optional A–Z/# prefix cleanup when compatible TagService methods are available.
- [x] Core compile/package path validated in run #124.
- [ ] Real browse/alphabet-index verification.

## Metadata — MovieDb fallback languages

- [x] Default-off.
- [x] Extends MovieDb's own metadata language chain instead of replacing the provider.
- [x] Configurable fallback order.
- [x] Can limit expansion to Chinese-first libraries.
- [x] Generic `zh` image-language widening.
- [x] Runtime target status exposed through `MovieDbFallbackModState`.
- [ ] Real MovieDb provider binding and missing-field fallback verification.

## Metadata — Chinese actor baseline

The community base already contained a functional person-refresh pipeline; it was not actually missing.

- [x] Scheduled `RefreshPersonTask`.
- [x] TMDB Person ID based refresh.
- [x] Concurrent MovieDb person metadata requests.
- [x] Traditional-to-Simplified conversion and person-name cleanup through existing `MetadataApi.ProcessPersonInfo`.
- [x] Duplicate Person cleanup.
- [x] Person image/metadata refresh.
- [ ] Real scheduled-task regression test after the new runtime mods are installed.

## Metadata — alternate MovieDb configuration

- [x] Default-off.
- [x] Optional compatible TMDB API base URL.
- [x] Optional compatible TMDB image base URL.
- [x] Optional 32-character hexadecimal v3 API key override.
- [x] Only default TMDB URLs are rewritten; other providers remain untouched.
- [x] API request, ProviderManager remote image save and RemoteImageService download targets are discovered independently.
- [ ] Real alternate endpoint/proxy verification.

## Metadata — Original Poster priority

- [x] Default-off.
- [x] Remote image query is widened only when an original language can be inferred.
- [x] Keeps every original remote-image result.
- [x] Stable sort: inferred original language first, preferred image language second, remaining results keep their relative order.
- [x] Optional Backdrop handling.
- [x] BoxSet language inference reuses collection children.
- [ ] Real MovieDb remote image ordering verification.

## Metadata — exact Simplified/Traditional Chinese poster/logo priority

- [x] Standalone persistent runtime settings; default off.
- [x] Preferred exact language (default `zh-CN`).
- [x] Ordered fallback languages (default `zh,zh-HK,zh-TW`).
- [x] Primary and Logo can be enabled independently.
- [x] No remote image is removed.
- [x] If Original Poster is enabled for a clearly non-Chinese work, the original-language rule wins.
- [x] Admin GET/POST settings API.
- [ ] Matrix compile/package confirmation on latest HEAD.
- [ ] Real remote-image ordering verification.

## Metadata — Traditional fallback conversion

- [x] Standalone persistent runtime settings; default off.
- [x] MovieDb Movie/Series/Season/Episode/Person provider discovery by runtime type name and return type.
- [x] Optional Traditional-to-Simplified conversion for Name, Overview and Tagline.
- [x] Optional Person name conversion.
- [x] Default guard limits conversion to `zh-CN`, `zh-SG` and `zh-Hans` metadata requests.
- [x] `OriginalTitle` is never converted.
- [ ] Matrix compile/package confirmation on latest HEAD.
- [ ] Real fallback-result verification.

## Metadata — TMDB / Local Episode Group

### Infrastructure

- [x] Series external ID provider `TmdbEg`.
- [x] TMDB episode-group response model.
- [x] Compact local `episodegroup.json` model.
- [x] Online TMDB episode-group fetch with cache.
- [x] Direct HTTP JSON episode-group URL support.
- [x] Optional online-to-local compact save.
- [x] Local `episodegroup.json` read path.
- [x] Alternate MovieDb API URL/key can be reused by the episode-group fetcher.

### MovieDb provider mapping

- [x] Season metadata mapping and group-name result.
- [x] Episode metadata maps display group order to original TMDB season/episode before provider execution and restores display order afterwards.
- [x] Season image mapping.
- [x] Episode image mapping.
- [x] Local Episode Group is preferred when enabled and a current Series context is available.
- [x] Each provider target degrades independently.
- [x] 4.8 and 4.10 build/package were green on run #135 at the last recorded checkpoint; 4.9 still needed final run confirmation.
- [x] Read-only `/StrmAssistant/EpisodeGroup/{Id}/Preview` mapping preview.
- [ ] Latest-head matrix confirmation including Preview API.
- [ ] Real TMDB Episode Group refresh test.
- [ ] Real Local Episode Group refresh test.
- [ ] Validate provider refresh concurrency/AsyncLocal Series context under concurrent library refreshes.

## Multi-Version enhancement

### Existing base merge behavior

- [x] Scheduled/post-scan merge task already exists in the community source.
- [x] Movie grouping by provider identity.
- [x] Series grouping.
- [x] Library/global movie scope.
- [x] Uses Emby's existing `ILibraryManager.MergeItems`.
- [x] No literal plugin-side fixed eight-version cap was found during source audit.

### Runtime display enhancement

- [x] Standalone persistent settings; default off.
- [x] Runtime-only `Video.GetMediaSources` post-processing.
- [x] Optional source display name from quality/container/file name.
- [x] Duplicate display-name disambiguation.
- [x] Optional stable highest-resolution/bitrate-first ordering.
- [x] No file rename and no media database write.
- [x] Admin GET/POST settings API.
- [ ] Matrix compile/package confirmation on latest HEAD.
- [ ] Real client version-selector verification.

### Still pending

- [ ] Independent playback progress/favorite state for globally merged versions across different libraries. This requires a verified Emby UserData-key contract before any runtime patch is enabled.

## Unified diagnostics

- [x] `/StrmAssistant/Diagnostics/Capabilities` exists and reports core Phase-2 capabilities.
- [x] Runtime Mod, Pinyin, MovieDb fallback, alternate MovieDb, Original Poster and Episode Group states added on the current branch.
- [x] Active tests use synthetic media only.
- [ ] Extend the report with the standalone Chinese image language, Chinese metadata conversion and multi-version display stores after the latest matrix is green.

## Deferred high-risk / external-source work

These are intentionally not marked complete simply to inflate feature count:

- [ ] Native SQLite CJK tokenizer / FTS migration layer.
- [ ] Cross-library merged-version independent UserData keys.
- [ ] Unified IntroDb external source integration.
- [ ] Douban-assisted metadata source (requires a stable, explicitly defined source/API contract).
- [ ] Automatic default metadata/image provider mutation for newly created libraries.
- [ ] Direct cross-server database writes (not planned; shared portable JSON remains the safe design).

All runtime-private patches in this phase are implemented independently in the GPL community fork and are unrelated to any third-party PRO licensing mechanism.
