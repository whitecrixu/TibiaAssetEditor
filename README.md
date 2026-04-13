# Tibia Asset Editor By WhiteCrixu V1.0

Cross-platform editor for the MMORPG **Tibia** asset files (`appearances.dat`,
sprite sheets, `items.xml`, `items.otb`).

Linux/macOS/Windows port of the original Windows WPF editor. Built on
**.NET 9.0 + Avalonia 11**, using SkiaSharp for image work and Magick.NET for
GIF/APNG handling.

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

- **`MainWindow`** — preset/version selection, routes into the editor stack.
- **`DatEditor`** — primary appearance editor for modern (protobuf) clients.
- **`LegacyDatEditor`** — pre-protobuf clients with hand-rolled `.dat`
  structures.
- **`OTBEditor`** — server-side `items.otb` editor (binary tree).
- **`SprEditor`** — sprite sheet viewer / batch tools.
- **`ItemsXmlEditor`** — OpenTibia `items.xml` editor.
- **`Sprite.cs`** — sprite decode/encode + SkiaSharp bitmap conversion.
- **`LZMA.cs`** — sprite sheet (BMP-like) compression.
- **`OBD/ObdDecoder.cs`** — `.obd` legacy bundle import/export.
- **`AssetsPublisher.cs`** — content-addressed assets publishing with
  `assets.json` + sha256 rewrite.
- **`TemplateLibrary.cs`** — reusable appearance templates.
- **`ItemsXml.cs`** — items.xml load/save with typed `ItemEntry` model.

See `CHANGELOG.md` for the full feature list.

## License

Private repository.
