# StrmAssistant Custom Full-Feature Requirements

This document is the acceptance baseline for the custom open-source fork.

The goal is **behavioral feature coverage implemented independently from public documentation and public Emby APIs**. It does not patch, modify, or bypass the closed-source PRO licensing mechanism.

## Status legend

- `[BASE]` already present in the community source and must be preserved
- `[EXTEND]` community implementation exists but needs the documented enhanced behavior
- `[NEW]` missing from the community source and must be independently implemented
- `[VERIFY]` implementation exists/compiles but still requires real Emby runtime verification

A feature is not considered complete until it has:

1. configuration/UI where applicable;
2. backend behavior;
3. logging and failure handling;
4. Emby 4.8 / 4.9 / 4.10 compatibility checks where applicable;
5. real-server runtime verification.

---

## 1. General

- [ ] `[BASE/EXTEND]` Install / update / uninstall workflow
- [ ] `[BASE/EXTEND]` Catch-up Mode
- [ ] `[EXTEND]` Chinese search enhancement
  - adaptive tokenizer behavior
  - Emby 4.9 database-connection-pool compatibility
  - simplified/traditional mixed search
  - search-term conversion support
  - optional recommendation suppression
- [ ] `[EXTEND]` Proxy Server
  - scraper whitelist mode
  - global proxy mode
  - custom local discovery address
- [ ] `[BASE]` Known-issue compatibility handling
- [ ] `[NEW]` Continuous compatibility support for newer Emby releases

## 2. Media Info Extract

- [ ] `[BASE]` Concurrent / pseudo-multithread library import
- [ ] `[EXTEND]` Image Capture
  - image ratio post-processing (16:9 landscape / 2:3 portrait)
  - custom ffmpeg support
  - ISO screenshot/chapter-image support
- [ ] `[BASE/EXTEND]` Video Thumbnail
- [ ] `[BASE]` Exclusive Extract
- [ ] `[EXTEND]` MediaInfo Persist
  - video persistence
  - music persistence including album, artist and embedded artwork restoration
- [ ] `[EXTEND]` External Subtitle Scan
  - subtitle scan
  - external audio-track scan on supported Emby 4.9+
- [ ] `[NEW]` ISO MediaInfo Extract
- [ ] `[NEW]` Distributed Extract
  - rffmpeg integration
  - worker routing
  - path compatibility and diagnostics
- [ ] `[NEW]` extraction blacklist by tag / keyword for MediaInfo, fingerprint and BIF

## 3. Metadata Enhancement

- [ ] `[BASE]` Custom fallback metadata languages
- [ ] `[BASE/EXTEND]` Chinese Actor
- [ ] `[BASE/EXTEND]` Original Poster
- [ ] `[BASE]` Pinyin initial-letter sorting
- [ ] `[BASE]` TMDB Episode Group scraping
- [ ] `[BASE]` Local Episode Group scraping
- [ ] `[BASE]` Episode Refresh
- [ ] `[BASE]` Alternative TMDB configuration
- [ ] `[NEW]` Local TMDB metadata source
- [ ] `[NEW]` Douban-assisted scraping
- [ ] `[NEW]` distinguish Simplified/Traditional Chinese posters and logos
- [ ] `[NEW]` default TMDB metadata/image scraper selection for newly created libraries
- [ ] `[EXTEND]` Traditional-Chinese-first fallback with conversion behavior

## 4. Intro / Credits Enhancement

- [ ] `[BASE/EXTEND]` Native intro-detection enhancement
- [ ] `[BASE]` Playback-behavior intro detection
- [ ] `[NEW]` per-library fingerprint-detection duration
- [ ] `[NEW]` parallel fingerprint processing
- [ ] `[NEW]` Unified IntroDb scraping

## 5. Multi-Version Enhancement

- [ ] `[BASE/EXTEND]` Auto merge multi-version media
- [ ] `[NEW]` independent playback progress across versions in different libraries
- [ ] `[NEW]` independent favorite state across versions in different libraries
- [ ] `[NEW]` configurable display naming for versions
- [ ] `[NEW]` quality sorting by resolution / bitrate
- [ ] `[NEW]` configurable same-folder movie merge limit (remove fixed limit of 8)

## 6. Notification Enhancement

- [ ] `[BASE]` `favorites.update`
- [ ] `[BASE]` `introskip.update`
- [ ] `[EXTEND]` season/episode information in grouped TV add/remove notifications
- [ ] `[NEW]` `deep.delete`
  - sent only for explicit user delete intent
  - sent only after successful local STRM/symlink deletion
  - sent only to the deleting user
  - description includes `Mount Paths`, one media target per line
- [ ] `[NEW]` `metadata.update`
  - only manual / REST API metadata changes
  - only when tracked values actually change
  - configurable tracked fields
  - supported fields: Name, Overview, OriginalTitle, Tagline, OfficialRating, CustomRating, CriticRating, CommunityRating, IndexNumber, ParentIndexNumber, PremiereDate, ProductionYear, EndDate, RunTimeTicks, Tags, Genres, Studios, ProductionLocations, ProviderIds
- [ ] `[NEW]` `image.update`
  - movies, collections, series, seasons and episodes
  - all supported image types
- [ ] `[NEW]` `collection.items.added`
- [ ] `[NEW]` `collection.items.removed`
- [ ] `[NEW]` delayed library-added notification when catch-up image work is pending
- [ ] `[NEW]` clear copied notification configuration for cloned users

## 7. Deep Delete

- [ ] `[NEW]` trigger only on explicit user delete operations
- [ ] `[NEW]` resolve local STRM target / symlink target
- [ ] `[NEW]` delete target media file
- [ ] `[NEW]` delete related files by target-media basename (nfo/json/images/subtitles/etc.)
- [ ] `[NEW]` delete empty target directory
- [ ] `[NEW]` recursively delete empty parent directories until a non-empty directory is reached
- [ ] `[NEW]` do not perform local deep delete for HTTP STRM targets
- [ ] `[NEW]` detailed operation log and partial-failure handling
- [ ] `[NEW]` integrate with `deep.delete` notification

## 8. Library / UI / Experience Enhancement

- [ ] `[BASE/EXTEND]` Hide collection libraries in user UI without deleting them
- [ ] `[BASE/EXTEND]` Copy library
  - Web UI action
  - `POST /Library/VirtualFolders/Copy` compatible API
- [ ] `[EXTEND]` UI enhancements
  - hide people by preference (no image / actors only / non-Chinese names)
  - auto-complete unmatched episode display titles
  - improve multipart-title display
  - TMDB-based missing episode support respecting scraper priority / episode groups
- [ ] `[NEW]` forced user preferences
  - forced library sorting
  - forced season/episode display style
- [ ] `[NEW]` total episode count instead of unwatched count
- [ ] `[NEW]` main-screen library item-count badges
- [ ] `[NEW]` preference-based item display order
  - natural title sorting
  - reverse season/episode display
  - collection date sorting based on contained item dates
- [ ] `[NEW]` collection membership on series page considers season collection membership
- [ ] `[NEW]` newest-first log display
- [ ] `[NEW]` after current episode finishes, optionally mark prior partially-played episodes as played

## 9. Shortcut Menu

- [ ] `[BASE/EXTEND]` existing shortcut menu behavior
- [ ] `[NEW]` remove selected duplicate/split person entries
- [ ] `[NEW]` clear chapter images and BIF
- [ ] `[NEW]` clear media information

## 10. Compatibility / Release Gate

Every release must pass at minimum:

- [x] compile/package against Emby Core 4.8.0.80
- [x] compile/package against Emby Core 4.9.1.90
- [x] compile/package against Emby Core 4.10.0.1-beta
- [ ] startup test on real Emby 4.8
- [ ] startup test on real Emby 4.9
- [ ] startup test on real Emby 4.10 test build
- [ ] smoke-test all configuration pages
- [ ] smoke-test MediaInfo/STRM/subtitle/multi-version/intro flows
- [ ] smoke-test notification/deep-delete flows in a disposable test library

## Public behavior references

Primary public documentation: https://github.com/sjtuross/StrmAssistant/wiki

Important pages include `PRO版的功能`, `通知系统增强 (Notification)`, `深度删除 (Deep Delete)`, `分布式媒体信息提取 (Distributed Extract)`, `复制媒体库`, and `UI 功能`.
