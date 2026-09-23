# Discover UI rebuild - handoff

This document records what was built in the `feature/netflix-ui` branch, why the
pieces are shaped the way they are, and how to verify or extend the work.

Everything here is additive. The classic table view, the change set, the install
pipeline and the metadata layers in `Core/` are untouched.

The current concept reference is kept in `Concept Art/index.html` as a design
reference only. The shipped surface is `GUI/Main/ModernShell.cs`: a borderless,
owner-drawn WinForms shell that renders the same stage, title bar, rail, shelves,
queue, collections, settings and overlays without embedding HTML or a WebView.

## What changed

| Area | Before | After |
|------|--------|-------|
| Browsing | A single dense `DataGridView` | A native card-based "Discover" surface, shown by default, with compact gallery, list and table densities |
| Artwork | None | On-demand cover art from SpaceDock, GitHub README screenshots and GitHub social cards, with deterministic generated fallback |
| Ranking | Column-click sorting in the grid | A "Rank by" control with five rankings, persisted between runs |
| Filtering | Structured grid filters | Concept-art pills for All / Installed / Updates / New / Compatible plus presentation categories |
| Change-set visibility | Checkbox in a row | Status badges and an action button on every card |
| Version history | A "Versions" column | A dedicated **Changelog** tab in the detail pane, with optional GitHub release notes |
| Window chrome | Classic raised WinForms chrome | Borderless native stage, title bar, rail and status bar with soft surfaces |
| Status bar | Left-aligned text | Centred, calmer status line |

## New files

All under `GUI/Discover/` unless noted.

### `ModernShell.cs` (under `GUI/Main/`)

The native application frame that replaces the legacy menu-bar/split-pane
framing when the GUI starts. It owns only presentation state and delegates
catalogue selection, changeset edits, refresh, settings and launch actions to
the existing `Main` and `ManageMods` owners. Its command palette includes view
navigation, density changes, queue/apply/discard actions, theme/notes and the
first catalogue matches.

### `SoftTheme.cs`

The single source of truth for the visual language: light and dark palettes, the
accent colors, rounded-rectangle path helpers, soft shadow painting, colour mixing
and a small set of cached fonts (so paint code never allocates a `Font`).

`IsDark` is read once from `Util.DarkMode`. Fonts are app-lifetime statics - do
**not** wrap them in `using`, that disposes a shared instance and breaks later
paints.

### `ModCard.cs`

One owner-drawn mod tile. Paints artwork, a top scrim, status pills, title, author,
download count, abstract, a version pill and an action button.

* `Status` is driven by the change set (`ModCardStatus`), never by the card itself.
* The artwork band is clipped to the cover rectangle **and** the rounded card path;
  without the extra clip an aspect-filled portrait banner spills down over the text.
* The abstract is limited to whole lines (`maxLines` of 1 or 2 depending on the space
  measured for it) so text is never cut mid-line.

### `ModCarouselRow.cs`

A horizontally scrolling shelf: title, item count, page arrows, optional "See all"
and a `FlowLayoutPanel` of cards.

* The wheel scrolls the shelf sideways **only while there is more to see**, then
  marks the event unhandled so the page scrolls vertically. This avoids trapping the
  user inside a row.
* `LayoutChildren` computes its own height (header + card + margins + scrollbar) and
  then positions children; setting `Height` re-enters once and settles.

### `ModDiscoverView.cs`

The browsing surface. Structure:

* **Header** - title, subtitle, soft search field, filter pills (All / Installed /
  Updates / New / Compatible), `Rank by`, category and density controls, a
  native Play KSP action, and a stats line
  (`N mods available · N installed · N updates`).
* **Shelves** - `Continue with your setup` (installed), `Updates available`,
  `New and noteworthy`, the ranked shelf (title follows the selected ranking) and
  `Browse everything`. Empty shelves are skipped; rows are capped at 30 cards, with
  "See all" handing the user back to the table view.
* **Flat catalogue** - `List` and `Table` are owner-drawn WinForms rows backed by
  the same `GUIMod` objects and change-set status resolver. `Table` adds a native
  metadata header; neither mode creates an HTML control or WebView.
* **Empty state** - shown when the search or filter matches nothing.

Artwork loading is on demand: a 350 ms timer plus scroll/resize hooks find the cards
that intersect the viewport, queue them, and run at most three downloads at a time.
Decoded images are kept per mod for the session and **can be disposed** in
`Dispose`; cards own their own clone.

`DiscoverSortMode` (defined here) is `Name`, `Downloads`, `NewestRelease`,
  `SmallestDownload`, `Author`. Download counts come from the repository download
  statistics that `GUIMod.DownloadCount` already exposes. `DiscoverDensity` is
  persisted in `GUIConfiguration.DiscoverDensity` as compact gallery (`0`), list
  (`1`) or table (`2`).

### `ModArtGenerator.cs`

Deterministic generated cover art for mods with no image: a gradient from a hue
derived from a hand-written FNV-1a hash of the identifier (never
`string.GetHashCode()`, which is randomized per process), a diagonal light streak,
large translucent initials and a bottom band that keeps overlaid text readable.

### `ModVisualMetadataService.cs`

Resolves artwork on demand and caches it in `%LOCALAPPDATA%\CKAN\artcache`:

1. **SpaceDock** - `https://spacedock.info/api/mod/{id}` (`background` field).
2. **GitHub README screenshot** - `raw.githubusercontent.com/{owner}/{repo}/{master|main}/README.md`,
   then the first Markdown or HTML image that is not a badge, CI status or donation
   link. Raw content is CDN backed, so this deliberately avoids the GitHub API rate
   limit; a session cap (`MaxReadmeLookups`) keeps a long browsing session polite.
3. **GitHub OpenGraph card** - `https://opengraph.githubassets.com/1/{owner}/{repo}`.
4. **Generated fallback** - `ModArtGenerator`.

`GetInfoAsync` and `GetCoverAsync` never throw and never return null; failures are
logged and degrade to the next source. Negative results are cached (`.info.none`) so
a mod without artwork is only ever looked up once. Delete
`%LOCALAPPDATA%\CKAN\artcache` to force a refetch.

### `ModChangelogService.cs` / `ModChangelogEntry.cs`

`GetLocalHistory` builds the version list from
`IRegistryQuerier.AvailableByIdentifier`, newest first, flagging the installed,
latest and queued versions. `GetGithubReleasesAsync` pulls
`https://api.github.com/repos/{owner}/{repo}/releases` on demand, and
`GetFullChangelogAsync` merges the two, preferring local metadata for versions the
registry already knows about.

### `Changelog.cs`

The detail-pane tab: a header with a "Fetch release notes" button and the source
line, then one card per version showing version, date, status pills and notes.
Notes link to the release page when a URL is known.

### `SegmentedControl.cs`

An Apple-style segmented switch (soft track, raised thumb, hover state) used for the
Discover / List toggle in the toolbar.

### `SoftToolStripRenderer.cs`

A flat professional renderer with a soft colour table and a single hairline rule.
Applied to `MainMenu`, the `ManageMods` toolbar and `statusStrip1`.

## Modified files

| File | Change |
|------|--------|
| `GUI/Controls/ManageMods.cs` | Owns the Discover view, the view switch, the toolbar restyle and the bridging methods `SelectModForDetails`, `ToggleModInstalled`, `ShowDiscoverView`, `RefreshDiscoverView`, `ApplyDiscoverSettings`. |
| `GUI/Controls/ModInfo/ModInfo.cs` | Adds the Changelog tab in code (so the localized designer layout is untouched) and routes it from `LoadTab`. |
| `GUI/Main/Main.cs` | Restyles the legacy controls for fallback use and installs the borderless native `ModernShell` as the visible application surface. |
| `GUI/Model/GUIConfiguration.cs` | Persists `DiscoverView` (bool, default `true`), `DiscoverSort` (int), and `DiscoverDensity` (int). |
| `GUI/Properties/Resources.resx` | All new user-visible strings. No display text is hard-coded in the new code. |

## Integration contract

These invariants keep the new UI honest:

* The Discover view never mutates the data model. It raises events, `ManageMods`
  acts. Adding a card action means adding an event, not a `GUIMod` write.
* Install/queue changes go through `GUIMod.SelectedMod` - the same property the
  grid checkbox writes - so conflicts, recommendations, the dry run and the change
  set all keep working. `guiModule_PropertyChanged` pushes the new state back into
  the row's checkbox cells for us.
* Card selection prefers the grid row (so the shared context menu and navigation
  history keep working) and falls back to `OnSelectedModuleChanged` when the
  current filters hide that mod.
* `ManageMods` is constructed before `Main` loads `GUIConfiguration`, so the
  constructor must not read config. Persisted Discover settings are applied by
  `ApplyDiscoverSettings()` on the first refresh.

## Verifying the work

Both targets must build:

```powershell
dotnet build GUI\CKAN-GUI.csproj -f net481
dotnet build GUI\CKAN-GUI.csproj -f net10.0-windows
```

Then launch against a real instance:

```powershell
_build\out\CKAN-GUI\Debug\bin\net481\CKAN-GUI.exe
```

Manual checklist:

1. The app opens in Discover with shelves and artwork inside the borderless
   native shell; the legacy menu bar and split-pane frame are not visible.
2. Scroll a shelf with the wheel - it moves sideways until the end, then the page
   scrolls.
3. Change `Rank by` to `Most downloaded` - shelf order and the ranked shelf title
   change, and the choice survives a restart.
4. Press a card's action button - the badge flips to a queued state and the change
   set in the table view matches.
5. Select a card, open the **Changelog** tab, press "Fetch release notes" for a mod
   with a GitHub repository.
6. Switch through compact, list and table density - the native rows remain interactive and the classic grid is still one click away.
7. Press `N` for the design-notes panel, `Ctrl K` for the native command palette,
   and press the native Play KSP action.
8. Launch again - the view, ranking and density you left are restored.

### Screenshot harness

The UI has no automated tests. During development this PowerShell pattern was used;
it launches the app, optionally resizes the window, captures it and kills it.

```powershell
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int t,bool p);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int Left, Top, Right, Bottom; }
}
"@
$p = Start-Process "_build\out\CKAN-GUI\Debug\bin\net481\CKAN-GUI.exe" -PassThru
Start-Sleep -Seconds 30
$h = $p.MainWindowHandle
[void][W]::MoveWindow($h, 20, 20, 1560, 980, $true)
Start-Sleep -Seconds 5
$r = New-Object W+R
[void][W]::GetWindowRect($h, [ref]$r)
$bmp = New-Object System.Drawing.Bitmap(($r.Right-$r.Left), ($r.Bottom-$r.Top))
[System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($r.Left,$r.Top,0,0,$bmp.Size)
$bmp.Save("$env:TEMP\ckan.png", [System.Drawing.Imaging.ImageFormat]::Png)
Stop-Process -Id $p.Id -Force
```

Caveats learned the hard way:

* Move/resize the window with `MoveWindow`, **not** `ShowWindow(SW_MAXIMIZE)`; the
  `SplitContainer` has `FixedPanel = Panel2` and a maximize-then-resize leaves the
  detail pane squeezed to a few pixels.
* A window restored partly off-screen (a stale `WindowLoc` in `GUIConfig.json`)
  makes screen captures show gaps; fix `WindowLoc`/`WindowSize` first.
* `CKAN-GUI` is a WinForms app: `System.Windows.Automation` sees top-level menus but
  not every `TabControl` tab, so click by coordinate rather than by name for tabs.

## Active repair handoff — PR #1

Work is on `feature/netflix-ui`; do not merge or push to `master`.
The user authorised fixes, regression tests, handoff updates and checkpoint pushes
on the existing PR. Starting commit: `504633b884cfb9d4fca48c5d054e4d2f965036f6`.

### In progress

- Independently verify the review's bulk-update staging bug in `ManageMods`.
- Review artwork request scheduling, duplicates and cancellation.
- Review catalogue keyboard activation and reveal-animation resource ownership.
- Remove or correct misleading settings without weakening download integrity.
- Build both GUI frameworks and run feasible automated tests.

### Environment and verification boundaries

This repair session runs on Linux ARM64, not a Windows desktop. A local .NET
10 SDK has been installed at `/home/opc/workspace/.tools/dotnet`. Build and
non-visual regression results will be recorded below; none prove the actual
Windows Forms interface works visually. The Windows/KSP manual checklist above
remains required. Never substitute the HTML concept art for native UI evidence.

### Checkpoints

- First checkpoint pushed: `91a34c03`; both remote branch and PR head verified.
- Catalogue Enter/Space now opens details without staging installs/removals,
  including unavailable/autodetected rows. PaneReveal disposes ImageAttributes
  on normal drawing and both matrix/drawing exception paths.
- Artwork review was partly stale: offscreen identifiers and cached duplicates
  already worked. Fixed hidden-card requests, disposed queued cards retaining
  in-flight markers, and initiating-card disposal losing artwork for duplicates.
- Removed unsupported verification/artwork switches and bindings without
  changing integrity checks, caching or legacy stored configuration values.
- Native Refresh now calls UpdateRepo, matching classic Refresh; previously it
  only rebuilt the list from the unchanged local registry.
- Parent independently reran: artwork harness 9 tests; catalogue/pane harness
  86 assertions; settings 3 source-contract tests and 1 runtime harness (14
  assertions); bulk actions 15 assertions. All passed. Runtime harnesses compile
  actual source methods against doubles, NOT a native Windows UI.
- Follow-up investigation: gallery ModCard keyboard routing and signed tick
  rollover in PaneReveal. Do not confuse these with already verified fixes.

- Initial investigation: working tree clean; PR #1 open; remote matches starting
  commit. Review findings are leads, not independently verified facts yet.
- Bulk-update regression reproduced: the normal installed selection was toggled
  to null (removal), not the latest compatible version. QueueAllUpdates now
  explicitly selects the latest compatible version, preserving queued changes.
- Collection staging regression reproduced: a repeated member toggled its
  installation back off. Collection staging now selects, never toggles, and
  preserves existing version choices.
- `DOTNET=/path/to/dotnet python3 qa/check_manage_mod_actions.py`: 15 passed.
  The two reported failures were observed before fixes. The harness executes
  production C# method bodies with model doubles; it does not test WinForms
  events or real registry/install integration.
- Both GUI target builds succeeded after this batch (zero errors; existing
  warnings remain). Baseline `dotnet test Tests/Tests.csproj -f net10.0
  -p:EnableWindowsTargeting=true` passed 1,319 tests. Dependency vulnerability
  warnings include System.Drawing.Common 4.7.0 and cryptography/build packages;
  dependency remediation needs a separate compatibility-reviewed follow-up.

## Known limitations and follow-ups

* **One screenshot per mod.** The README parser takes the first usable image. A
  gallery in the detail pane would need a `ModVisualInfo.Screenshots` list plus a
  fetch of the whole README once per mod.
* **CurseForge artwork is not used.** `resources.curseforge` exists in metadata, but
  the CurseForge API requires a key, so those mods fall through to GitHub or
  generated art.
* **Cover art is not shown in list/table density.** The compact gallery remains
  the art-forward mode; flat rows use generated initials so they stay fast and
  legible with large catalogues.
* **Shelf order is fixed.** Shelves are declared in `RebuildRows`; a user-arrangeable
  order would need a config entry listing shelf keys.
* **No filtering by game version or install size yet.** The filter pills are the
  natural place to add them.
* **The generated art is procedural.** It is intentionally abstract; swapping in a
  bundled set of KSP-themed gradients would be a pure asset change in
  `ModArtGenerator`.
* **The changelog's GitHub path is API-limited.** Unauthenticated GitHub allows 60
  requests per hour per IP, which is why release notes are only fetched when the
  user presses the button.
