# Full-Feature Development Status

This is the authoritative **development** status for `agent/custom-rebuild`.

It deliberately separates three different claims:

- **DEV DONE** — source implementation exists and is wired into the plugin.
- **CI GREEN** — the implementation has passed JavaScript validation (where applicable), C# build, ILRepack and artifact generation against the current compatibility matrix: Emby Core `4.8.0.80`, `4.9.1.90`, `4.10.0.1-beta`.
- **RUNTIME PENDING** — real Emby behavior still needs the final disposable-server test requested by the user. Runtime reflection targets, native libraries, rffmpeg/shared paths, external services and Emby Web DOM are intentionally not claimed as verified by CI.

A feature may therefore be **DEV DONE + CI GREEN + RUNTIME PENDING** at the same time.

## General / compatibility

| Feature | Development | Runtime |
|---|---|---|
| Community install/update/uninstall behavior preserved | DEV DONE | RUNTIME PENDING |
| Catch-up Mode / concurrent queues | DEV DONE | RUNTIME PENDING |
| Simplified/Traditional query expansion | DEV DONE / CI GREEN | RUNTIME PENDING |
| Pinyin SortName / A-Z bridge | DEV DONE / CI GREEN | RUNTIME PENDING |
| Advanced Chinese FTS connection loader | DEV DONE / CI GREEN | RUNTIME PENDING |
| Guarded advanced Chinese FTS Apply/Restore | DEV DONE / CI GREEN | **HIGH-RISK RUNTIME PENDING** |
| Proxy whitelist/global routing | DEV DONE / CI GREEN | RUNTIME PENDING |
| Runtime capability degradation across Emby generations | DEV DONE / CI GREEN | RUNTIME PENDING |
| Weekly compatibility watch | DEV DONE | CI/runtime depends on future Core packages |

Advanced FTS Apply is never automatic. It requires an active tokenizer smoke test, observed successful runtime SQLite extension loading, `Confirm=true`, `AcknowledgeImmediateRestart=true`, a consistent sqlite3 backup with `PRAGMA integrity_check=ok`, and a single-transaction rebuild of only `fts_search8/fts_search9`. Restore rebuilds the FTS table back to `unicode61`; it does not overwrite a live `library.db` with a backup file.

## Media information / extraction

| Feature | Development | Runtime |
|---|---|---|
| Concurrent/pseudo-multithread extraction | DEV DONE | RUNTIME PENDING |
| Shared extraction blacklist (tag/keyword) | DEV DONE / CI GREEN | RUNTIME PENDING |
| Image Capture enhancements | DEV DONE / CI GREEN | RUNTIME PENDING |
| Source / centered 16:9 / centered 2:3 capture | DEV DONE / CI GREEN | RUNTIME PENDING |
| Custom ffmpeg capture | DEV DONE / CI GREEN | RUNTIME PENDING |
| Video thumbnail/chapter JPEG pre-generation | DEV DONE / CI GREEN | RUNTIME PENDING |
| Native BIF/index finalization retained | DEV DONE / CI GREEN | RUNTIME PENDING |
| Exclusive extract | BASE PRESERVED | RUNTIME PENDING |
| MediaInfo persistence (video + music) | DEV DONE / CI GREEN | RUNTIME PENDING |
| External subtitle scan | BASE/EXTENDED | RUNTIME PENDING |
| External audio-track scan on Emby 4.9.1.80+ | DEV DONE / CI GREEN | RUNTIME PENDING |
| ISO/Blu-ray/BDMV probe | DEV DONE / CI GREEN | RUNTIME PENDING |
| Optical writeback Plan/Confirm/rollback | DEV DONE / CI GREEN | RUNTIME PENDING |
| BDMV BDInfo/MPLS enrichment | DEV DONE / CI GREEN | RUNTIME PENDING |
| rffmpeg/custom ffprobe/ffmpeg Health | DEV DONE / CI GREEN | RUNTIME PENDING |
| Per-item distributed Probe | DEV DONE / CI GREEN | RUNTIME PENDING |
| Distributed MediaInfo routing + native fallback | DEV DONE / CI GREEN | RUNTIME PENDING |
| Distributed image/chapter work | DEV DONE / CI GREEN | RUNTIME PENDING |
| Distributed fingerprint ffmpeg-path proxy | DEV DONE / CI GREEN | RUNTIME PENDING |
| Shared-root MediaInfo sync | DEV DONE / CI GREEN | RUNTIME PENDING |
| Portable cross-host MediaInfo Sync Key/path mapping | DEV DONE / CI GREEN | RUNTIME PENDING |

## Metadata enhancement

| Feature | Development | Runtime |
|---|---|---|
| Custom TMDB fallback languages | DEV DONE / CI GREEN | RUNTIME PENDING |
| Chinese Actor refresh / duplicate handling | BASE/EXTENDED | RUNTIME PENDING |
| Original-language poster priority | DEV DONE / CI GREEN | RUNTIME PENDING |
| Simplified/Traditional poster + Logo priority | DEV DONE / CI GREEN | RUNTIME PENDING |
| Traditional fallback -> Simplified metadata conversion | DEV DONE / CI GREEN | RUNTIME PENDING |
| Alternative TMDB API/key/image base | DEV DONE / CI GREEN | RUNTIME PENDING |
| TMDB Episode Group | DEV DONE / CI GREEN | RUNTIME PENDING |
| Local `episodegroup.json` | DEV DONE / CI GREEN | RUNTIME PENDING |
| Episode Group Preview/diagnostics | DEV DONE / CI GREEN | RUNTIME PENDING |
| Episode refresh | BASE PRESERVED/EXTENDED | RUNTIME PENDING |
| Local TMDB JSON metadata source | DEV DONE / CI GREEN | RUNTIME PENDING |
| New-library default TMDB provider policy | DEV DONE / CI GREEN | RUNTIME PENDING |
| Douban Assist configurable JSON bridge | DEV DONE / CI GREEN | RUNTIME PENDING / requires user-supplied compatible service |

Douban Assist intentionally does not scrape unstable Douban HTML. It consumes a configurable JSON bridge keyed by TMDB/IMDb identity and fails back to MovieDb data.

## Intro / credits

| Feature | Development | Runtime |
|---|---|---|
| Native fingerprint/intro workflow preserved and extended | DEV DONE | RUNTIME PENDING |
| Existing playback behavior integration preserved | BASE PRESERVED | RUNTIME PENDING |
| Parallel fingerprint queue | DEV DONE | RUNTIME PENDING |
| Per-library fingerprint duration override | DEV DONE / CI GREEN | RUNTIME PENDING |
| Fingerprint/Chromaprint Health | DEV DONE / CI GREEN | RUNTIME PENDING |
| IntroDB.app built-in provider | DEV DONE / CI GREEN | RUNTIME PENDING / external service |
| TheIntroDB.org built-in provider | DEV DONE / CI GREEN | RUNTIME PENDING / external service |
| Custom IntroDb JSON provider | DEV DONE / CI GREEN | RUNTIME PENDING |
| Provider priority + TTL cache | DEV DONE / CI GREEN | RUNTIME PENDING |
| Multi-provider intro/credits merge | DEV DONE / CI GREEN | RUNTIME PENDING |
| IntroDb Preview / Plan / Confirm Apply | DEV DONE / CI GREEN | RUNTIME PENDING |
| Confidence-gated auto-apply | DEV DONE / CI GREEN | RUNTIME PENDING |

Auto-apply is off by default. Existing markers are preserved unless overwrite is explicitly enabled.

## Multi-version

| Feature | Development | Runtime |
|---|---|---|
| Auto merge multi-version media | BASE/EXTENDED | RUNTIME PENDING |
| Configurable/unlimited same-group merge count | DEV DONE / CI GREEN | RUNTIME PENDING |
| Version display-name formatting | DEV DONE / CI GREEN | RUNTIME PENDING |
| Resolution/bitrate quality sorting | DEV DONE / CI GREEN | RUNTIME PENDING |
| Cross-library version UserData identity diagnostics | DEV DONE / CI GREEN | RUNTIME PENDING |
| Cross-library version progress/favorite isolation runtime mod | DEV DONE / CI GREEN | RUNTIME PENDING |

## Notification enhancement

| Feature | Development | Runtime |
|---|---|---|
| `favorites.update` | BASE PRESERVED | RUNTIME PENDING |
| `introskip.update` | BASE PRESERVED/EXTENDED | RUNTIME PENDING |
| `deep.delete` | DEV DONE / CI GREEN | RUNTIME PENDING |
| `metadata.update` tracked-field change monitor | DEV DONE / CI GREEN | RUNTIME PENDING |
| `image.update` | DEV DONE / CI GREEN | RUNTIME PENDING |
| `collection.items.added` / `removed` | DEV DONE / CI GREEN | RUNTIME PENDING |
| Native NewLibraryContent Episode `SxxExx` description | DEV DONE / CI GREEN | RUNTIME PENDING |
| Catch-up image-work notification delay | DEV DONE / CI GREEN | RUNTIME PENDING |
| Removed Episode activity notification `SxxExx` via exact ItemId cache | DEV DONE / CI GREEN | RUNTIME PENDING |
| Clear inherited notification settings after explicit clone-user CreateUser path | DEV DONE / CI GREEN | RUNTIME PENDING |

Clone-user cleanup does not subscribe indiscriminately to all new users. It runtime-discovers the explicit clone overload and resets only user configuration stores whose key/type unambiguously contains `notification` or `notifier`. If no such store is discovered it safely no-ops and reports capability state.

## Deep Delete

| Feature | Development | Runtime |
|---|---|---|
| Explicit user-owned Plan/Confirm route | DEV DONE / CI GREEN | RUNTIME PENDING |
| Admin shortcut preview/confirm | DEV DONE / CI GREEN | RUNTIME PENDING |
| STRM/symlink local-target resolution | DEV DONE / CI GREEN | RUNTIME PENDING |
| Allowed-root protection | DEV DONE / CI GREEN | RUNTIME PENDING |
| HTTP/HTTPS target refusal | DEV DONE / CI GREEN | RUNTIME PENDING |
| Media target + sidecar deletion | DEV DONE / CI GREEN | RUNTIME PENDING |
| Empty-directory cleanup | DEV DONE / CI GREEN | RUNTIME PENDING |
| Partial failure handling / preserve Emby item on file-delete failure | DEV DONE / CI GREEN | RUNTIME PENDING |
| Delete Emby item only after local operation succeeds | DEV DONE / CI GREEN | RUNTIME PENDING |
| Deleting-user-only `deep.delete` notification | DEV DONE / CI GREEN | RUNTIME PENDING |

The implementation intentionally does not bind local file deletion to passive `ItemRemoved` scan events.

## UI / library / experience

| Feature | Development | Runtime |
|---|---|---|
| Hide collections virtual library without deleting BoxSets | DEV DONE / CI GREEN | RUNTIME PENDING |
| Copy library | BASE/EXTENDED | RUNTIME PENDING |
| People DTO filter | DEV DONE / CI GREEN | RUNTIME PENDING |
| Missing-episode display enhancement | DEV DONE / CI GREEN | RUNTIME PENDING |
| Unmatched Episode display-title beautification | DEV DONE / CI GREEN | RUNTIME PENDING |
| Multipart-title beautification | DEV DONE / CI GREEN | RUNTIME PENDING |
| Forced user preferences | DEV DONE / CI GREEN | RUNTIME PENDING |
| Total Episode count in DTO | DEV DONE / CI GREEN | RUNTIME PENDING |
| Home/library item counts | DEV DONE / CI GREEN | RUNTIME PENDING |
| Natural/reverse display sorting | DEV DONE / CI GREEN | RUNTIME PENDING |
| Prior partially-played Episode backfill after playback completion | DEV DONE / CI GREEN | RUNTIME PENDING |
| Series detail Collections = direct Series membership UNION Season membership | DEV DONE / CI GREEN | RUNTIME PENDING / Emby Web DOM placement |
| Log content newest-first runtime route patch | DEV DONE / CI GREEN | RUNTIME PENDING / runtime route discovery |

The Series collection UI uses a plugin-owned read-only API and independent embedded Web module; no BoxSet membership is modified.

## Shortcut / maintenance

| Feature | Development | Runtime |
|---|---|---|
| Existing shortcut menu | BASE/EXTENDED | RUNTIME PENDING |
| Deep Delete shortcut | DEV DONE / CI GREEN | RUNTIME PENDING |
| Duplicate/split Person maintenance | DEV DONE / CI GREEN | RUNTIME PENDING |
| Clear chapter images / BIF using Emby-resolved thumbnail paths | DEV DONE / CI GREEN | RUNTIME PENDING |
| Clear MediaInfo / persisted JSON | DEV DONE / CI GREEN | RUNTIME PENDING |

## Diagnostics available for the final test

- `/StrmAssistant/Diagnostics/Capabilities`
- `/StrmAssistant/Diagnostics/Experience`
- `/StrmAssistant/Diagnostics/FeatureStores`
- `/StrmAssistant/Fingerprint/Health`
- `/StrmAssistant/DistributedExtract/Health`
- `/StrmAssistant/Search/AdvancedChinese/Health`
- `/StrmAssistant/Search/AdvancedChinese/Plan`
- `/StrmAssistant/Search/AdvancedChinese/Apply` (**guarded write operation**)
- `/StrmAssistant/Search/AdvancedChinese/Restore` (**guarded write operation**)
- `/StrmAssistant/IntroDb`
- `/StrmAssistant/IntroDb/{Id}/Preview`
- `/StrmAssistant/IntroDb/{Id}/Plan`
- per-feature Plan/Preview APIs for destructive or high-risk operations

## Compatibility gate

The current compile/package matrix remains:

- Emby Core `4.8.0.80`
- Emby Core `4.9.1.90`
- Emby Core `4.10.0.1-beta`

Later Emby server builds must be verified at runtime when their corresponding public Server.Core package is not available. Reflection/capability bridges are used specifically so unsupported runtime methods degrade per-feature instead of preventing the plugin from loading.

## Final gate still intentionally open

The PR remains Draft until the user performs the planned real-server full-function test. Runtime verification must include at minimum startup/config pages, main metadata/MediaInfo flows, distributed/optical paths where available, notifications, Deep Delete on disposable media, IntroDB providers, multi-version UserData behavior, Web UI injections and the guarded Chinese FTS migration on a disposable library database.
