# Phase 1 — Experience / Notification / Deep Delete Status

Branch: `agent/custom-rebuild`

This document tracks implementation status separately from the full acceptance checklist. `Implemented` means code exists and compatibility CI compiles/packages it. `Runtime verify` means it still requires a disposable real-Emby test before it is considered complete.

## Implemented

### Notification enhancement

- Registers Strm Assistant notification types in Emby's notification settings:
  - `favorites.update`
  - `introskip.update`
  - `deep.delete`
  - `metadata.update`
  - `image.update`
  - `collection.items.added`
  - `collection.items.removed`
- Adds Experience Enhance configuration switches for each notification channel.
- `metadata.update` has a 19-field snapshot/diff engine and default tracked fields:
  - `Name,Overview,OriginalTitle,Tags,Genres`
- Metadata notifications are restricted to `ItemUpdateType.MetadataEdit` and only emitted when tracked values differ.
- Collection added/removed notifications use the public `ICollectionManager` events.
- Image update fallback uses `ItemUpdateType.ImageUpdate`.

### Deep Delete

- Admin-only plugin-owned preview endpoint:
  - `GET /StrmAssistant/DeepDelete/{Id}/Plan`
- Admin-only explicit execution endpoint:
  - `DELETE /StrmAssistant/DeepDelete/{Id}?Confirm=true`
- Does **not** subscribe to `ILibraryManager.ItemRemoved`, so a normal library scan cannot trigger deep deletion.
- Defaults:
  - feature disabled;
  - Dry Run enabled;
  - target-media deletion disabled;
  - allowed roots empty.
- Local `.strm` target resolution.
- Symbolic-link target resolution on modern Emby/.NET runtimes through runtime `LinkTarget` reflection while retaining a `netstandard2.1` binary.
- Rejects non-file remote STRM targets such as HTTP/HTTPS.
- Allowed-root boundary enforcement.
- Related-file cleanup for same-basename metadata/images/subtitles.
- Optional recursive empty-directory cleanup, stopping before the configured allowed root itself.
- Partial failure handling preserves the Emby item when target deletion reports an error.
- `deep.delete` notification is sent only to the authenticated user after successful explicit execution, and Mount Paths contain resolved media targets rather than sidecar files.

### Shortcut / UI integration

- Adds an admin-only `Deep Delete` shortcut for deletable media items.
- The UI requests the plan first, displays planned/blocked paths, then requires a second confirmation before execution.
- Existing copy-library, version-delete, lock/unlock, clear-intro and DataExplorer integration are preserved.

### Collections library experience

- `HideCollectionsLibrary` uses the same `UserConfiguration.MyMediaExcludes` mechanism used by Emby's user-view filtering.
- Applies to all users and periodically reconciles newly created users / recreated BoxSets libraries.
- Tracks only exclusions added by the plugin in a separate state file; disabling the feature removes only plugin-managed exclusions.
- Existing copy-library API/shortcut is preserved.
- Collection-library removal now goes through an admin-only plugin route, verifies the selected folder is `BoxSets`, enables hide mode, conditionally marks the legacy collections migration complete when that runtime property exists, then removes the virtual folder using the current `ILibraryManager.RemoveVirtualFolder(long, bool)` API.

## Compatibility validation

Latest C# compatibility validation before the JavaScript syntax gate was added:

- Emby Core `4.8.0.80` — build/package success
- Emby Core `4.9.1.90` — build/package success
- Emby Core `4.10.0.1-beta` — build/package success

GitHub Actions run: `31254120722`.

The build workflow now also runs `node --check` against the embedded JavaScript files so subsequent Phase 1 builds gate both C# and shortcut-menu JavaScript syntax.

## Runtime verification still required

- Confirm all seven custom notification types appear in the real Emby notification UI.
- Confirm `metadata.update` behavior across a restart and the first manual edit after startup.
- Capture the exact image type (Primary / Backdrop / Logo / etc.) rather than the current `Unknown` server-event fallback.
- Test `.strm` and symlink deep deletion only in a disposable library with Dry Run first.
- Test Windows/Linux path semantics and Docker-mounted paths.
- Confirm the embedded web shortcut loads on the exact Emby 4.9 Web UI build used by the server.
- Confirm hide/unhide collections for multiple users and ensure pre-existing `MyMediaExcludes` entries are untouched.
- Confirm remove-collections behavior does not unexpectedly recreate the top-level BoxSets library after restart.

Phase 1 is therefore **implemented/CI-validated, but not yet runtime-accepted**.
