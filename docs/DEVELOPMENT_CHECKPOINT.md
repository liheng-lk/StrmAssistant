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
- unified capability diagnostics.

## Validation boundary

A successful GitHub Actions matrix means only that JavaScript syntax validation, C# compilation, ILRepack packaging and artifact upload succeeded for the tested SDK targets. It does **not** prove that runtime-private Emby or MovieDb method signatures still match on a real server.

The compatibility matrix remains:

- Emby Core 4.8.0.80
- Emby Core 4.9.1.90
- Emby Core 4.10.0.1-beta

The current batch must be revalidated as one unit before more high-coupling runtime patches are added.

## Runtime testing remains deferred

The user requested that development continue first and that full-feature runtime results will be provided after the development pass is complete. Therefore no current source-complete feature is marked runtime-verified unless an earlier explicit real-server test established it.
