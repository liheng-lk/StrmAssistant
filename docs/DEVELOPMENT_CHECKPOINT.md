# Development checkpoint

This checkpoint intentionally separates source implementation, compile/package validation and real-server runtime validation.

## Current source batch

The current `agent/custom-rebuild` branch includes the earlier Phase 1/2 work plus the following post-Phase-2 source modules:

- isolated Harmony runtime-mod host while keeping the plugin target on `netstandard2.1`;
- selective HTTP proxy routing and read-only proxy-route diagnostics;
- DTO-only people display filtering;
- non-destructive Simplified/Traditional Chinese FTS search-term expansion;
- runtime Pinyin sort-name enhancement;
- MovieDb fallback language-chain extension;
- alternate MovieDb API/image endpoint and API-key rewriting;
- original-language remote-poster priority;
- exact Simplified/Traditional Chinese Primary/Logo image-language priority;
- optional Traditional-to-Simplified MovieDb result conversion;
- TMDB/Local Episode Group infrastructure, provider mapping and read-only preview;
- runtime-only multi-version media-source naming/quality ordering;
- unified capability diagnostics;
- STRM MediaInfo persistence/shadow reliability protection;
- verified OpenList/WebDAV remote Deep Delete and native Emby delete bridging.

## Latest compile/package validation

The latest audited reliability candidate is GitHub Actions run **#566 / 31441060879**.

- Emby Core `4.8.0.80` — JavaScript validation, C# build, ILRepack, artifact upload ✅
- Emby Core `4.9.1.90` — JavaScript validation, C# build, ILRepack, artifact upload ✅
- Emby Core `4.10.0.1-beta` — JavaScript validation, C# build, ILRepack, artifact upload ✅

A successful matrix means only that JavaScript syntax validation, C# compilation, ILRepack packaging and artifact upload succeeded for the tested SDK targets. It does **not** prove runtime-private Emby/MovieDb/OpenList behavior on a real server.

## STRM MediaInfo reliability audit

The current implementation is designed around a single invariant: a known-good STRM must not require a new remote probe merely because Emby's repository streams were cleared during a later refresh.

Implemented safeguards:

1. **Playback/static media-source pre-read recovery**
   - Runtime playback/static MediaSourceManager methods are discovered and patched.
   - If core A/V MediaInfo is incomplete, validated local recovery is attempted before media-source resolution.
   - Persisted MediaInfo JSON/.bak is preferred where configured; plugin-local STRM shadow is the fallback.
   - The pre-read guard itself never ffprobes or opens the remote media.

2. **External subtitle/audio stream write guard**
   - External-track reconciliation cannot become the authority for internal video/audio streams.
   - If an internal A/V baseline is missing, local recovery is attempted first.
   - If recovery still cannot establish internal A/V, the external-track repository write is blocked.
   - A post-write verifier repairs a refresh race that removes internal A/V between the guard and repository write.

3. **Validated persistence backup**
   - A valid `-mediainfo.json` is copied to `.bak` before overwrite.
   - Invalid/partial replacement snapshots do not destroy the previous valid backup.
   - Restore retries from `.bak` when the primary snapshot is unusable.
   - Non-explicit scan/item-removal events preserve recovery snapshots.

4. **Plugin-local STRM reliability shadow, schema v3**
   - Independent from the user-facing MediaInfo persistence mode and media-directory write permissions.
   - Stores playback-critical internal streams plus runtime/container/bitrate/dimension fields; external subtitle/audio paths are excluded.
   - HTTP(S) target identity hashes authority + decoded normalized path and intentionally ignores query/fragment values, so rotating signed URL tokens do not invalidate the same media.
   - A real host/port/path change rejects the old shadow.
   - Restore merges shadow internal streams with the item's current external streams.

5. **Automatic schema-v3 seed migration**
   - Waits for startup/scan activity to settle.
   - Seeds already-complete STRM items from Emby's local repository; no remote probe is performed.
   - `seed-v3.done` is written only when every currently complete STRM has a valid v3 shadow.
   - Partial failures leave no marker, so a future startup retries.

6. **Explicit maintenance consistency**
   - Administrator-confirmed MediaInfo clear invalidates shadow/backup recovery data when persisted data is explicitly deleted.
   - Explicit Deep Delete clears the STRM shadow only inside the explicit destructive context.
   - Background refresh/scan removal does not clear the last-known-good recovery state.

7. **Manual-only repair for already-broken STRM items**
   - Scheduled task: `修复缺失的 STRM 媒体信息`.
   - It has no default triggers.
   - Local persistence/shadow recovery is attempted first.
   - Only items with no working local recovery source are allowed one explicit Emby MediaInfo rebuild that may access the media source.
   - Concurrency is capped at 2.

8. **Read-only reliability inventory**
   - `GET /StrmAssistant/ReliabilityAudit?IncludeInventory=true` reports total STRM count, complete core MediaInfo count, valid v3 shadows, recoverable incomplete items and the count/sample IDs that genuinely remain at playback-probe risk.
   - `GET /StrmAssistant/Reliability/{Id}?ProbeRemote=true` correlates one item's MediaInfo/shadow/hook and remote-delete health.

## Remote/cloud Deep Delete audit

Remote deletion is fail-closed. A local STRM/Emby deletion is not considered a substitute for deleting the cloud object.

1. **Providers and safety boundary**
   - OpenList API and WebDAV are supported.
   - Explicit `AllowedRemoteRoots` are required for destructive calls.
   - Manual source-prefix mappings remain authoritative.
   - Same-origin OpenList `/d/<mount-path>` STRM targets can auto-map when no manual mapping matches; cross-authority/reverse-proxy aliases still require explicit mapping.

2. **Verified transaction**
   - Pre-delete provider probe must confirm the target exists, or confirm it is already missing under the configured idempotency policy.
   - Then the provider delete/remove request is sent.
   - A second probe must verify the target is missing.
   - Only verified remote success permits local STRM/Emby deletion to continue.
   - Mapping/auth/network/probe failures preserve the local item.

3. **Native Emby delete bridge**
   - Hooks only explicit single-item `/Items/{Id}` delete routes discovered from runtime Route metadata.
   - It intentionally does not hook generic `ItemRemoved`/`ILibraryManager.DeleteItem` events that scans/background work can raise.
   - The authenticated user is independently checked for administrator/global/folder content-deletion permission before a remote destructive call.
   - Deep Delete Dry Run blocks native local deletion for a remote target.

4. **Remote transaction monitor**
   - Detects the irreversible partial state where the cloud object was removed but the subsequent native Emby/local delete failed or the item remained.
   - Such cases are surfaced in reliability diagnostics and logged as critical.

5. **OpenList associated sidecars, opt-in**
   - Disabled by default.
   - Uses the actual OpenList directory listing before selecting candidates.
   - Only strict same-stem metadata/subtitle/image extensions are eligible; unrelated files and other video versions are not inferred.
   - Candidate count and directory-list size are safety-limited.
   - Sidecars are deleted only after the main remote object is already verified missing.
   - The directory is listed again; any remaining candidate marks the remote transaction failed and blocks local STRM deletion so a retry can finish cleanup.
   - Read-only preview: `GET /StrmAssistant/DeepDelete/{Id}/RemoteSidecars`.

6. **Non-destructive provider probe**
   - `GET /StrmAssistant/DeepDelete/{Id}/RemoteProbe` resolves target/mapping/allowed-root and checks provider visibility without issuing DELETE.

## Runtime testing remains deferred

The user requested that development/audit continue first and that full-feature runtime results will be provided afterwards. Therefore no source-complete feature is marked runtime-verified unless an earlier explicit real-server test established it.

The remaining reliability gates are real-environment tests rather than unresolved source implementation:

- restart + schema-v3 seed against an existing STRM library;
- rotating signed OpenList `/d/` URL query tokens across refresh/restart without a new playback probe;
- external subtitle/audio reconciliation racing with an Emby metadata refresh;
- OpenList pre-probe → remove → post-probe with the user's real token and mount path;
- native Emby delete button → verified remote delete → local STRM/item delete;
- opt-in OpenList sidecar preview/delete/verification;
- failure injection: bad token, unavailable provider, wrong mapping, and post-remote local-delete failure.
