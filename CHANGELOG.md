# Changelog

All notable changes to **Tibia Asset Editor By WhiteCrixu V1.0**.

## V1.0 — Initial cross-platform release

Linux/cross-platform port of the Windows WPF Assets Editor for the MMORPG Tibia.
Ported to **.NET 9.0 + Avalonia 11**, using SkiaSharp + Magick.NET + protobuf.

---

### Branding & packaging

- Renamed product to **Tibia Asset Editor By WhiteCrixu V1.0** (window titles,
  About dialog, csproj `AssemblyName`/`Product`/`FileVersion`).
- Single-file self-contained binaries for **Windows x64** and **Linux x64**
  via `dotnet publish /p:PublishSingleFile=true`.

---

### Pass 0 — Windows → Linux port (DAT/Appearances editor parity)

Brought the Linux port up to feature parity with the Windows source.

- **Appearance flags UI** — extended from 27 to **62 flags** with full Load/Save:
  Hook + type, Shift X/Y, Height/Elevation, Light brightness/color, Automap,
  LensHelp, Clothes slot, Default action, full Market (category/trade/show/level
  + 7 vocations), TopEffect, DecoKit, Corpse, PlayerCorpse, ShowOffSocket,
  Reportable, ReverseAddon E/W/N/S, WearOut, ClockExpire, Expire, ExpireStop,
  Cyclopedia + ID, Ammo, NPC, SkillWheel Gem (quality/vocation), DualWielding,
  Imbueable + slots, Proficiency + ID, Transparency + level, ChangedToExpire,
  UpgradeClass + amount, WeaponType (9 options), MinimumLevel, Vocation
  Restriction (8 options), Name, Description, full proto dump.
- **Flag Parameters panel** — 20+ NumericUpDown / ComboBox controls for
  parameterized flags.
- **Utils.cs** — ported missing helpers: `ColorizeOutfit`, `ColorizePixel`,
  `OnAppearanceCloned`, `Outfit` class, `GetLastIdOrZero<IList<T>>`.
- **OTB reader fix** — replaced unreliable `BinaryReader.PeekChar()` (which
  fails on Linux when binary data looks like multi-byte UTF-8) with a direct
  `Position < Length` check in `OTBReader.Read()`. Items now load correctly.

---

### items.xml integration

- **`ItemsXml.cs`** — full OpenTibia `items.xml` loader/saver. Parses both
  `<item id="...">` and ranged `<item fromid="..." toid="...">`.
- **`ItemsXmlEditor.axaml(.cs)`** — dedicated editor window:
  - Item list with **40×40 thumbnails** loaded from `appearance.dat` sprites
    (lazy via `ContainerPrepared`/`ContainerClearing`).
  - Header card with 96×96 preview + ID + name + type hint.
  - **5 tabs**: Basic / Combat / Properties / Flags / Custom.
  - **~40 typed fields** covering: Article, Name, Plural, Description, Type,
    Weight, SlotType, WeaponType, Attack, Defense, ExtraDef, Armor, Range,
    BreakChance, ShootType, MaxHitChance, Required Level/Mag, Charges,
    Duration, DecayTo, RotateTo, ContainerSize, Speed, MaxTextLen,
    WriteOnceItemId, CorpseType, FluidSource, Effect, Wrapable, Unwrapable,
    skill bonuses (Sword/Axe/Club/Distance/Shield/Fishing), MagicPoints,
    and 10 boolean flags (stopDuration, showCount, showAttributes, readable,
    writeable, allowDistRead, replaceable, blockprojectile, moveable,
    pickupable).
  - **Custom attributes grid** — preserves unknown XML keys on roundtrip.
- **DatEditor integration**:
  - `File > Load items.xml` — populates names in the appearance list.
  - `File > Edit items.xml` — opens the dedicated editor.
  - Appearance list now shows the items.xml name under the ID for items.
  - Search box filters by ID **or name**.

---

### Pass 1 — Workflow & productivity

- **Recent files menu** — `File > Recent appearance.dat` and
  `File > Recent items.xml`, last 10 files persisted in `Settings.json`.
- **Auto-save / crash recovery** — `DispatcherTimer` writes
  `~/.local/share/TibiaAssetEditor/autosave.dat` every N minutes (configurable),
  with a `.meta` sidecar. Restore offered on startup.
- **Quick jump (Ctrl+G)** — popup ID input, jumps across all categories.
- **Bookmarks (Ctrl+B)** — `Settings.Bookmarks` list, view in
  `Edit > Show Bookmarks`.
- **Settings dialog** — `Edit > Settings`: dark mode, autosave on/off + interval,
  default preview FPS, keyboard-shortcut cheatsheet.

---

### Pass 2 — Sprite toolkit

- **Sprite Deduplicator** (`Tools > Deduplicate Sprites`) — SHA256 over every
  sprite, picks the lowest ID as canonical, remaps appearance references,
  removes duplicates from `SprLists` and `AllSprList`. Reports scanned /
  unique / duplicates / refs remapped.
- **Sprite Reference Graph** (`Tools > Sprite References`) — given a sprite ID
  (smart pickup from `AppSprListView` / `SprListView` / dialog), lists every
  appearance that references it.
- **Flip / Rotate Sprites** (`Tools > Flip / Rotate Sprites`) — 4 operations
  (H, V, 90° CW, 90° CCW) applied to every unique sprite of the selected
  appearance via `SKCanvas` transforms.

---

### Pass 3 — Selection & UX

- **Multi-select on `ObjListView`** — `SelectionMode="Multiple"` with classic
  click-to-select, Ctrl+click extends, Shift+click range.
  Batch Set Flag prefers selection over filter when ≥1 items are selected.
- **Revert (Ctrl+R)** — snapshot taken on selection, restored to last-loaded
  state without affecting other appearances.
- **Tooltip preview on hover** — floating tooltip with 128×128 image + ID +
  name on every appearance list item.
- **Zoom in sprite preview (Ctrl+scroll)** — `ScaleTransform` on the preview
  image, range 0.25× – 16×, status bar shows the current zoom.

---

### Pass 4 — items.xml power features

- **`Tools > Sync items.xml ↔ DAT`** — finds entries in XML without DAT and
  vice-versa, reports counts + ID lists, optional auto-fix that creates stub
  XML entries for orphaned DAT appearances.
- **Template Library** (`TemplateLibrary.cs`):
  - `Edit > Save as Template` — clones an appearance with `Id` + sprite IDs
    stripped, saved to `~/.local/share/TibiaAssetEditor/templates/<name>.aec`.
  - `Edit > Apply Template` — dynamic submenu, applies a template while
    preserving the target's `Id` and existing sprite IDs.
  - `Edit > Manage Templates` — list view with on-disk path.
- **Flag consistency checker** — `Tools > Validate` extended with semantic
  warnings: Container without Take, Stackable+Container conflict,
  Writeable with `MaxTextLength=0`, Light with `Brightness=0`, Hook without
  Hangable, Market without Category, Cyclopedia with type 0, Clothes without
  slot, Ammo without Stackable, Impassable+Take contradiction, etc.

---

### Pass 5 — Visual effects & power workflow

- **Outline Generator** (`Tools > Add Outline`) — pixel edge detection, fills
  every transparent pixel adjacent to an opaque one with black.
- **Shadow Generator** (`Tools > Add Shadow`) — copies the alpha mask, draws
  it offset by `(+2,+2)` at ~40% alpha behind the original sprite.
- **GIF / APNG Import** (`Import > Import from GIF/APNG`) — uses Magick.NET to
  split frames, scales to target slot size, builds new sprite sheets, wires up
  `SpriteAnimation` with `Infinite` loop and per-frame phases.
- **Visual Flag Diff** (`Edit > Compare Flags`) — prompts for ID B, generates
  side-by-side flag diff (~35 booleans + 13 sub-message presence + name +
  description), shows only the differences.
- **Copy Sprite IDs** (`Edit > Copy Sprite IDs`) — multi-select aware,
  generates CSV `id,type,frame_group,sprite_ids` to clipboard via
  `TopLevel.GetTopLevel(this)?.Clipboard`.

---

### Earlier features (already in the Linux port at session start, retained)

- DAT/Appearances editing with sprite sheets (LZMA-compressed BMPs).
- OBD / AEC import/export (single-appearance and multi).
- Compile to assets (overwrites in-place with all-lowercase
  `catalog-content.json`).
- Publish to client (rewrites assets.json + sha256).
- Sprite editor with sheet viewer, batch downscale, range export,
  drag-and-drop tile import, **grid overlay toggle**, **per-tile info hint**.
- Lua scripting via MoonSharp + AvaloniaEdit.
- Search window, OTB editor, JSON editor, Log view.
- Outfit colorizer in preview (head/body/legs/feet, "Colorize" toggle,
  "Random" button) using `Utils.ColorizeOutfit` + Tibia HSI palette.
- Animated preview with FPS slider (1–30) and frame counter.
- Outfit template generator (2 frame groups, 4 directions, 3 addons,
  2 layers = 48 sprite slots).
- Compare DAT, Statistics dashboard, Validate, Repack Sheets.
- Batch Import / Batch Export, Resize Sprites (32↔64), Batch Set Flag.
- Quick Outfit Preview (cycles all 4 directions with walking animation).

---

### Bug fixes

- **OTB reader** — `OTBReader.Read()` `PeekChar() != -1` replaced with
  `Position < Length`, otherwise binary data containing UTF-8-looking
  sequences would terminate the loop early on Linux.
- **`SelectionMode` toggle bug** — `ObjListView` had `Multiple,Toggle` which
  toggled selection on every single click. Removed `Toggle` so single click
  selects normally; Ctrl+click still extends.
- **Flag Parameters & Sprite Info column widths** — NumericUpDown spinner +
  value were clipped at width 90; widened to 160.
- **SprEditor Search Sprite Sheet & Export Range** — NumericUpDowns widened
  similarly, window resized to 900×680.

---

### Build artifacts

- `publish/win-x64/Tibia Asset Editor.exe` (131 MB) — single-file PE32+ x86-64
  GUI executable, self-contained .NET 9 runtime, native libs embedded
  (`IncludeNativeLibrariesForSelfExtract=true`).
- `publish/linux-x64/Tibia Asset Editor` (149 MB) — single-file ELF 64-bit
  PIE, self-contained, depends only on standard glibc / libstdc++ / libgcc.
- `publish/TibiaAssetEditor-Windows-x64.zip` (58 MB).
- `publish/TibiaAssetEditor-Linux-x64.tar.gz` (65 MB).
