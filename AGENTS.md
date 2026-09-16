# AGENTS.md

Guidance for AI agents and contributors working in this repository.

## What this repo is

This is a fork of [KSP-CKAN/CKAN](https://github.com/KSP-CKAN/CKAN), the Comprehensive
Kerbal Archive Network mod manager for Kerbal Space Program. The fork adds a
Netflix-style browsing experience on top of the existing WinForms client.

Upstream is the canonical source for metadata logic. Keep changes to `Core/` minimal
and additive; the Discover work lives in `GUI/Discover/`.

## Repository layout

| Path        | Contents |
|-------------|----------|
| `Core/`     | The CKAN engine: registry, metadata, downloads, install logic. Cross platform. |
| `GUI/`      | The Windows Forms client. |
| `GUI/Discover/` | The Discover (`Netflix-style`) browsing experience. See below. |
| `ConsoleUI/`| The `ckan` command line client. |
| `Cmdline/`  | Command line helper actions (`ckan add`, `ckan install`, ...). |
| `Netkan/`   | The NetKAN metadata bot. |
| `Tests/`    | NUnit test suite. |
| `doc/`      | Extended documentation and handoff notes. |
| `Screenshots/` | Images used by documentation. |

## Build

The GUI targets `net481` and `net10.0-windows` (`GUI/CKAN-GUI.csproj`). A .NET SDK
is required to build even the `net481` target; a local SDK install works fine:

```powershell
# One-time local SDK install (no admin needed)
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1
./dotnet-install.ps1 -Channel 10.0 -InstallDir $env:LOCALAPPDATA\Temp\kilo\dotnet

$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Temp\kilo\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"

# Build the GUI for one target, or both
dotnet build GUI\CKAN-GUI.csproj -f net481
dotnet build GUI\CKAN-GUI.csproj -f net10.0-windows
```

The solution-wide build is driven by `build.ps1` / `build.sh` and the
`build/` targets used by CI.

### Build rules to respect

`GUI/CKAN-GUI.csproj` sets `TreatWarningsAsErrors=true`. Two rules bite often:

* **`WFO1000`** - every public settable property on a `Control`/`Component` needs
  `[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]`
  unless it really should be serialized by the WinForms designer.
* **`CA1416`** - types from `System.Drawing` and `System.Windows.Forms` are
  annotated as Windows-only. Any class that touches them needs the repo's
  standard guard:

  ```csharp
  #if NET5_0_OR_GREATER
  using System.Runtime.Versioning;
  #endif

      #if NET5_0_OR_GREATER
      [SupportedOSPlatform("windows")]
      #endif
      public sealed class MyControl : Control { ... }
  ```

Also: no C# 10+ syntax (`record`, `init`, global usings) because the `net481`
target is compiled with C# 9 and has no `IsExternalInit`.

## The Discover experience

All new UI lives in `GUI/Discover/`:

| File | Responsibility |
|------|----------------|
| `SoftTheme.cs` | Single source of truth for the soft palette, rounded-rect drawing, cached fonts and DPI scaling. Read `IsDark` once from `Util.DarkMode`. |
| `ModCard.cs` | One mod tile. Owner-drawn; exposes `Status` (change-set badge), `IsSelected`, `Cover`, and the `Activated` / `ActionClicked` / `ContextRequested` events. |
| `ModCarouselRow.cs` | One horizontally scrolling shelf with a title, counts, nav arrows and an optional "See all". Wheel scrolls sideways while there is more to see, then hands the event back so the page scrolls vertically. |
| `ModDiscoverView.cs` | The whole browsing surface: header (search, filter pills, ranking combo, stats), shelves, empty state, and the on-demand artwork pipeline. Defines `DiscoverSortMode`. |
| `ModArtGenerator.cs` | Deterministic generated cover art (stable FNV-1a hash - never `string.GetHashCode()`) for mods with no real image. |
| `ModVisualMetadataService.cs` | On-demand artwork and short descriptions, cached on disk. |
| `ModChangelogService.cs` / `ModChangelogEntry.cs` | Version history from the local registry, optionally enriched with GitHub releases. |
| `Changelog.cs` | The `ModInfo` "Changelog" tab. |
| `SegmentedControl.cs` | The Discover / List switch. |
| `SoftToolStripRenderer.cs` | Flat, hairline renderer for the menu bar, toolbar and status bar. |

### How Discover integrates with the classic UI

`ManageMods` owns both views. The important invariants:

* The Discover view is **read-only** with respect to the CKAN data model. It raises
  events; `ManageMods` performs the change. Never mutate `GUIMod` from the view.
* Toggling install from a card goes through the same property the grid's checkbox
  uses (`GUIMod.SelectedMod`), so the change set, conflict detection and the dry run
  all stay shared. See `ManageMods.ToggleModInstalled`.
* Selecting a card drives the grid's current cell when the mod is in the grid, and
  falls back to `OnSelectedModuleChanged` when a filter hides it. See
  `ManageMods.SelectModForDetails`.

### Artwork pipeline (on demand)

`ModVisualMetadataService` resolves a cover in this order and caches the outcome in
`%LOCALAPPDATA%\CKAN\artcache`:

1. **SpaceDock** `background` banner, from `https://spacedock.info/api/mod/{id}`.
2. **GitHub README screenshot** - the first non-badge raster image in `README.md`
   (`master` then `main`, via `raw.githubusercontent.com`, which does not consume
   the GitHub API rate limit). Badges, CI status and donation buttons are filtered out.
3. **GitHub OpenGraph card** - `https://opengraph.githubassets.com/1/{owner}/{repo}`.
4. **Generated fallback** - `ModArtGenerator`.

Artwork is only fetched for cards that scroll into view, at most three at a time,
and results (including "nothing available" markers) are cached to disk.

## Local development notes

* **Auto-update nag:** a fork build is versioned below the upstream release, so
  CKAN offers to "update" itself back to upstream and would replace this UI. Set
  `"CheckForUpdatesOnLaunch": false` in `<KSP>\CKAN\GUIConfig.json` when running
  a fork build.
* The GUI config for an instance lives at `<KSP install>\CKAN\GUIConfig.json`.
  `DiscoverView`, `DiscoverSort`, `WindowSize`, `WindowLoc` and `PanelPosition`
  are read from there. Anything the Discover view persists is written on a normal
  close, so kill the process only if you don't care about the settings.
* `Main` constructs `ManageMods` *before* it loads the GUI configuration, so
  `ManageMods.guiConfig` is null in its constructor. Persisted Discover settings
  are applied later by `ManageMods.ApplyDiscoverSettings`. Do not read config in
  the constructor.

## Testing

* Build both target frameworks before pushing.
* `Tests/` holds the NUnit suite for `Core`; run it with the test target used by CI.
* The GUI has no automated UI tests. Verify visually by launching
  `_build/out/CKAN-GUI/Debug/bin/<tfm>/CKAN-GUI.exe` against a real KSP instance.
  `doc/DISCOVER-UI-HANDOFF.md` documents a PowerShell screenshot/automation harness
  that was used during development.

## Style

* 4-space indent, braces on their own line, `using` directives outside the namespace.
* Match the existing file's patterns before introducing new ones.
* Prefer additive change over refactoring existing code, especially in `Core/`.
* New user-visible strings go in `GUI/Properties/Resources.resx` and are referenced
  through `Properties.Resources.*`; never hard-code display text.
