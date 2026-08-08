# Emby compatibility strategy

This fork keeps Emby API compatibility explicit instead of hard-coding a single server-core package version.

## Build targets

The CI matrix currently compiles and packages against:

- `MediaBrowser.Server.Core` 4.8.0.80
- `MediaBrowser.Server.Core` 4.9.1.90
- `MediaBrowser.Server.Core` 4.10.0.1-beta

The package version can also be overridden locally:

```powershell
dotnet msbuild StrmAssistant/StrmAssistant.csproj `
  -restore `
  -t:PackagePlugin `
  -p:Configuration=Release `
  -p:EmbyServerCoreVersion=4.9.1.90
```

## Compatibility policy

A successful compile is a compatibility gate, not a guarantee that every runtime path behaves identically on every Emby release. When Emby changes plugin APIs, compatibility fixes should be isolated behind adapter code rather than scattered throughout feature modules.

Future work should keep version-sensitive calls in a dedicated compatibility layer and add smoke tests for plugin startup, scheduled tasks, media events, metadata operations, and web configuration pages.

## Packaging

`PackagePlugin` produces `StrmAssistantCustom.dll` in the configured `PluginOutputDir`. This replaces the original machine-specific post-build path that wrote directly into a local Emby installation.
