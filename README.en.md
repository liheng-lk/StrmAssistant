# StrmAssistant Custom

![logo](StrmAssistant/Properties/thumb.png "logo")

## [[简体中文]](README.md)

> [!IMPORTANT]
> This repository is a **fork / independently modified version** of [`sjtuross/StrmAssistant`](https://github.com/sjtuross/StrmAssistant). It is **not an official upstream release**.
>
> This fork keeps the core upstream functionality while adding independent work for newer Emby compatibility, STRM MediaInfo reliability, online intro/credits data, deep-delete safety, Webhook automation, and runtime diagnostics. Issues introduced by this fork should be reported here rather than attributed to the upstream project.

## Upstream and Credits

- Upstream: [`sjtuross/StrmAssistant`](https://github.com/sjtuross/StrmAssistant)
- Upstream Wiki: [`sjtuross/StrmAssistant/wiki`](https://github.com/sjtuross/StrmAssistant/wiki)
- This fork: [`liheng-lk/StrmAssistant`](https://github.com/liheng-lk/StrmAssistant)
- License: [`GNU GPL-3.0`](LICENSE)

Thanks to the original authors and all contributors. This fork continues development under GPL-3.0 and preserves the upstream project's license and contribution history.

> The upstream Wiki documents upstream behavior. For features added by this fork, use this repository's README, source code, commit history, and verified runtime results as the primary reference.

## Project Scope

StrmAssistant Custom is a community Emby plugin focused on large STRM libraries, remote media, media automation, and newer Emby versions.

The project is not intended to bypass Emby licensing, DRM, or paid features, and it does not distribute modified Emby server binaries. Some enhancements use Emby plugin APIs, the notification system, and runtime patching for compatibility and extension.

## Main Features

### Maintained upstream capabilities

1. Improve initial playback startup time
2. Image capture and chapter/thumbnail preview enhancements
3. Intro and credits detection enhancements
4. Automatic multi-version merging
5. MediaInfo extraction and persistence
6. Independent external subtitle scanning
7. TMDB fallback language and metadata enhancements
8. Original-language posters
9. Chinese search and Pinyin sorting
10. TMDB episode-group and other inherited upstream features

### Key enhancements in this fork

- **Emby 4.8 / 4.9 / 4.10 build compatibility** with a multi-version CI matrix.
- **Tabbed GenericUI settings** for General, MediaInfo, Metadata, Intro/Credits, Experience, and About sections.
- **Online intro/credits providers** with IntroDB.app and TheIntroDB.org plus Preview / Plan / Apply and diagnostics endpoints.
- **STRM MediaInfo reliability** with persistence backups, shadow cache, recovery queue, integrity checks, fleet health, and runtime tests to reduce disappearing MediaInfo and repeated probing during playback startup.
- **Safer deep delete** with Plan, Dry Run, Allowed Roots, verification, transaction recovery, and cascade-delete guards.
- **Provider-agnostic `deep.delete` Webhook semantics**: the original STRM target is captured before deletion and emitted through Emby's NotificationManager. External Webhook automation can decide how to handle 115, Quark, Aliyun Drive, CDN links, signed URLs, or any other storage backend.
- **Optional OpenList / WebDAV direct deletion** with post-delete verification. These providers are not required for generic deep-delete/Webhook workflows.
- **Reliability diagnostics** including ReliabilityAudit, MediaInfo RuntimeTest, DeepDelete Plan/Probe, and related status endpoints.
- **Behavioral CI gate**: core logic, filesystem behavior, and HTTP transactions must pass contract tests before an artifact is produced.

## Deep Delete and Webhook

This fork treats deep delete as two parallel modes.

### 1. Generic Webhook / external automation

This mode is storage-provider agnostic:

```text
User executes Deep Delete
    ↓
Plugin reads the original .strm target before deletion
    ↓
Emby Notification Event: deep.delete
    ↓
Emby Webhook / notification provider
    ↓
External automation service
    ↓
External service performs the actual remote-storage operation
```

The `deep.delete` notification keeps the legacy-compatible description format:

```text
Item Name:
<media name>

Item Path:
<local STRM path>

Mount Paths:
<original target/direct URL from the STRM file>
<optional provider-mapped path>
```

The plugin does not need to identify which storage provider owns the STRM target before sending the Webhook event.

### 2. Direct OpenList / WebDAV deletion

When an administrator explicitly configures a remote provider, path mappings, and allowed roots, the plugin can directly delete remote objects and verify that they are gone. This is a destructive feature: always inspect Plan / Probe first and validate with disposable test media.

## Test Status and Evidence Levels

This repository does **not** treat an `Info` log line, a successful Harmony patch, or a successful build as proof that a feature works end to end.

Evidence levels are:

- **CONTRACT PASS**: the core algorithm, filesystem side effect, or HTTP transaction passed automated behavioral tests.
- **RUNTIME PASS**: the final side effect and re-read result were verified on a real Emby runtime.
- **DESTRUCTIVE PASS**: a destructive path was verified end to end using explicitly disposable test media.

> A green CI run means the automated gate passed. It does not mean every Emby patch level, client, storage provider, and external Webhook environment has completed runtime/destructive validation.

## Compatibility

The current CI matrix primarily targets:

- Emby Core 4.8.0.80
- Emby Core 4.9.1.90
- Emby Core 4.10.0.1-beta

Exact Emby Server patch releases can still differ at runtime. Back up configuration and databases and validate in a test environment before production deployment.

## Installation

1. Download the appropriate `StrmAssistantCustom.dll` from this fork's GitHub Actions artifacts or Releases.
2. Back up the existing plugin DLL and Emby configuration/database.
3. Stop Emby Server.
4. Place `StrmAssistantCustom.dll` in the Emby plugins directory, replacing an older custom-fork build when upgrading.
5. Start Emby Server.
6. Verify the plugin version and settings, run read-only diagnostics first, and only then enable destructive features such as deep delete.

> Avoid loading multiple StrmAssistant-derived DLLs that patch the same Emby methods, as duplicate Harmony patches can cause unpredictable behavior.

## Development and Verification Principles

- Development PRs remain Draft until runtime validation is complete.
- Destructive filesystem/remote operations should provide Preview / Plan / Dry Run / Verification whenever practical.
- MediaInfo, Webhook, and database-write features should be proven by re-reading the resulting state rather than by log messages alone.
- New behavior should receive repeatable contract tests where possible, with real Emby runtime tests for behavior that cannot be proven in isolation.

## License

This repository remains licensed under the **GNU General Public License v3.0 (GPL-3.0)**. See [`LICENSE`](LICENSE).

GPL-3.0 permits use, study, modification, and redistribution subject to its terms. This README does not add a conflicting "non-commercial use only" restriction. Distribution of modified binaries must comply with the corresponding GPL-3.0 source-code and license-notice obligations.

## Disclaimer

1. This project and this fork are not affiliated with, authorized by, or endorsed by Emby LLC.
2. Users are responsible for complying with applicable licenses, service terms, and law for Emby, media content, remote storage, and automation services.
3. Deep delete, remote deletion, Webhook automation, and database migration can cause data loss. Back up first and validate using disposable test data.
4. Problems introduced by this fork should not automatically be attributed to the upstream `sjtuross/StrmAssistant` project. Include the fork version, Emby version, and relevant diagnostics when reporting issues.
5. The software is provided under the GPL-3.0 no-warranty terms; developers and contributors are not liable beyond what applicable law requires.
