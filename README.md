# Tibia MultiTool Editor By WhiteCrixu

Cross-platform suite for **Tibia / Canary** server content — `appearances.dat`,
sprite sheets, `items.xml`, `items.otb`, **monsters** (`.lua`) and
**proficiencies** (`proficiencies.json`). Formerly *Tibia Asset Editor*.

Linux / macOS / Windows port of the original Windows WPF editor, rebuilt on
**.NET 9.0 + Avalonia 11** with **Semi.Avalonia** theming, SkiaSharp for image
work and Magick.NET for GIF/APNG handling.

## What's new in v2.4

Clearer interface: redesigned main screen, legacy 8.60 editor and OTB editor,
icons that show on Linux (bundled Noto Emoji), working secondary colours in
both themes. See [`CHANGELOG.md`](CHANGELOG.md).

## What's new in v2.3

The editor updates itself from GitHub releases: it checks at startup, shows the
release notes and can download, verify (SHA-256) and install the new version,
then restart. See [`CHANGELOG.md`](CHANGELOG.md).

## What's new in v2.2

Audit release — see [`CHANGELOG.md`](CHANGELOG.md) for the full list.

- Starts from anywhere (desktop shortcuts); settings in `~/.config/TibiaMultiToolEditor`.
- Legacy 8.60 editor: texture/slot editing, image import split into tiles,
  import from other clients and OBD, animated preview, compile change summary.
- Modern editor: catalog written with lowercase keys again, many flag
  round-trip fixes, safe sprite deletion and publish.
- Monster Editor edits TFS revscript (`mType:`) monsters in place.
- New **Project Wolf Server Tools**: consistency report (dat / otb / items.xml / monsters),
  things hashes, diff and export for approval.
- Tests: `dotnet test tests/AssetsEditor.Tests`.

## What's new in v2.1

Compatibility update for **TFS 1.x / protocol 8.60** servers with OTClient
8.60 clients (extended sprites, frame durations, frame groups):

- Legacy DAT/SPR **Compile** works; untouched `Tibia.dat` / `Tibia.spr` /
  `items.otb` are written back byte-identical.
- Fixes for data lost on save: Market, Lens help, animation loop counts,
  non-ASCII market names, empty sprites, and id shifts when deleting objects.
- `items.xml` is patched in place (ranges, comments, `decayTo="0"`, nested
  `field` attributes and encoding are kept).
- Monster Editor treats TFS revscript (`mType:`) monsters as read-only.

See [`CHANGELOG.md`](CHANGELOG.md).

## What's new in v2.0

Major release. The app is no longer just a DAT/sprite editor — it's a full
dashboard for Canary / OpenTibia server content, with two brand-new editors,
a hub-style MainWindow, and a modern UI.

- **Monster Editor** — recursive `.lua` folder loader, 6-tab typed editor
  (Basic / Outfit / Flags / Bestiary / Bosstiary / Raw Blocks), live Lua
  preview, auto-fill Bestiary & Bosstiary from folder path + experience + HP,
  outfit thumbnails from the loaded DAT. No `monsters.xml` needed — modern
  Canary scans the folder.
- **Proficiencies Editor** — full `proficiencies.json` editor (180+ entries,
  27k+ lines) with typed perks, smart field visibility per Type, value hints,
  ~35 preset perks, 11 weapon-category templates, and an L1…L7 level pill
  bar. DatEditor's Proficiency ID flag gets a `…` picker wired to it.
- **Hub MainWindow** — no longer auto-hides after Load Assets; stays as the
  dashboard with a 6-card Tools grid (DAT / Sprite / Monster / Proficiencies
  / Items XML / OTB). Sprites count visible alongside Items/Outfits/Effects/
  Missiles. Load Assets button shows live **%**.
- **Modern UI** — [`Semi.Avalonia`](https://github.com/irihitech/Semi.Avalonia)
  theme, custom window chrome with `TransparencyLevelHint="Mica,AcrylicBlur,Blur"`,
  FontAwesome vector icons on toolbars, Comfortable/Cozy/Compact density
  toggle, shared design tokens in `Resources/Theme.axaml`.
- **Renamed** from Tibia Asset Editor → **Tibia MultiTool Editor**
  (`AssemblyName` / `Product` / `FileVersion` = 2.0.0). Namespace kept as
  `Assets_Editor` to avoid breaking protobuf bindings and user settings.

See the full entry and fix list in [`CHANGELOG.md`](CHANGELOG.md) or the
[v2.0 GitHub release](https://github.com/whitecrixu/TibiaAssetEditor/releases/tag/v2.0).

## Features

- **Appearance editor** — full flag support (62 flags), sprite slot editing,
  frame group management, multi-direction preview, animated preview with
  outfit colorizer (head/body/legs/feet).
- **items.xml editor** — typed forms for ~40 OpenTibia attributes, per-item
  thumbnails from `appearance.dat`, custom-attribute preservation.
- **Sprite editor** — sheet viewer with grid overlay, drag-and-drop tile
  import, batch downscale, range export.
- **OTB editor** — server-side item database (read/write).
- **Lua scripting** via MoonSharp + AvaloniaEdit syntax highlighting.

### Workflow tools

- Recent files menu, autosave + crash recovery, quick jump (Ctrl+G),
  bookmarks (Ctrl+B), undo/redo (Ctrl+Z / Ctrl+Y), revert (Ctrl+R),
  multi-select on the appearance list, Ctrl+scroll preview zoom.
- Tooltip preview on hover.
- Settings dialog.

### Sprite toolkit

- **Deduplicator** — SHA256-based duplicate elimination across the whole
  asset folder.
- **Reference graph** — find every appearance using a given sprite ID.
- **Flip / rotate** for all sprites of an appearance.
- **Outline / shadow generators** — pixel-edge effects in one click.
- **GIF / APNG import** — split frames, build animated appearance.
- **Resize sprites** between sheet sizes (32 ↔ 64) with remap.

### Power features

- **Template library** — save an appearance's flags + structure as a
  reusable template, apply to new items.
- **items.xml ↔ DAT sync checker** — find orphan entries on either side,
  auto-fix.
- **Validator** — broken sprites, duplicate IDs, semantic flag warnings
  (Container without Take, Light without Brightness, etc).
- **Visual flag diff** — side-by-side comparison of two appearances.
- **Compare DAT** — diff two `appearances.dat` files.
- **Statistics dashboard** — counts, ID ranges, sheet/sprite breakdown.
- **Batch flag setter, batch import / export PNGs, copy sprite IDs to
  clipboard** as CSV.
- **Compile to assets** — overwrites the live Tibia client `appearances-*.dat`
  + `catalog-content.json` (lowercase property names — the Tibia parser is
  case-sensitive).
- **Publish to client** — full integrity-chain rewrite via `AssetsPublisher`.

## Build

```bash
# Build (debug)
~/.dotnet/dotnet build AssetsEditor.csproj

# Run
~/.dotnet/dotnet run --project AssetsEditor.csproj

# Publish single-file binary for Windows x64
~/.dotnet/dotnet publish AssetsEditor.csproj -c Release -r win-x64 \
    --self-contained true /p:PublishSingleFile=true \
    /p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64

# Publish single-file binary for Linux x64
~/.dotnet/dotnet publish AssetsEditor.csproj -c Release -r linux-x64 \
    --self-contained true /p:PublishSingleFile=true \
    /p:IncludeNativeLibrariesForSelfExtract=true -o publish/linux-x64
```

Requires .NET 9.0 SDK. The project builds with **0 errors**; warnings come
from a known vulnerability in `Magick.NET-Q8-AnyCPU` 14.9.1 (used for GIF
import/export only).

## Architecture

- **`MainWindow`** — hub dashboard: preset/version selection, Load Assets,
  6-card Tools grid that opens each editor.
- **`DatEditor`** — primary appearance editor for modern (protobuf) clients.
- **`LegacyDatEditor`** — pre-protobuf clients with hand-rolled `.dat`
  structures.
- **`OTBEditor`** — server-side `items.otb` editor (binary tree).
- **`SprEditor`** — sprite sheet viewer / batch tools.
- **`ItemsXmlEditor`** — OpenTibia `items.xml` editor.
- **`MonsterEditor`** + **`MonsterData.cs`** — Canary monster `.lua` folder
  loader, typed editor, Lua generator, auto-fill Bestiary/Bosstiary.
- **`ProficienciesEditor`** + **`Proficiencies.cs`** — Canary
  `proficiencies.json` editor with typed perks, presets and templates.
- **`Sprite.cs`** — sprite decode/encode + SkiaSharp bitmap conversion.
- **`LZMA.cs`** — sprite sheet (BMP-like) compression.
- **`OBD/ObdDecoder.cs`** — `.obd` legacy bundle import/export.
- **`AssetsPublisher.cs`** — content-addressed assets publishing with
  `assets.json` + sha256 rewrite.
- **`TemplateLibrary.cs`** — reusable appearance templates.
- **`ItemsXml.cs`** — items.xml load/save with typed `ItemEntry` model.
- **`Resources/Theme.axaml`** — design tokens (spacing, radii, typography)
  + shared control classes (`card`, `appbar`, `statusbar`, `hero`,
  `spriteSlot`, `primary`, `tool`, `action`, `icon`).

See `CHANGELOG.md` for the full feature list.

## License

Private repository.
