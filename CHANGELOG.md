# Changelog

All notable changes to **Tibia MultiTool Editor By WhiteCrixu** (formerly *Tibia Asset Editor*).

---

## Unreleased

- Proficiencies: the warning about perks the server cannot load now names
  both Crystal Server and Canary (both read `Value` from every perk and know
  perk types 0–31; Canary does not start with such a file).
- Proficiencies: new *Engine fix* button removes those perks (type 32 / no
  `Value`); nothing is written until you save.

---

## V2.6.3 — Proficiencies type 32, release-signed Android app

- Proficiencies: perk type 32 (the missile proc of the Moonsilver weapons in
  newer game data) is named and its Element, Missile, Multiplier and
  Probability can be edited. Changing a perk to type 32 drops its Value, as in
  the game data.
- Proficiencies: opening a file with perks Crystal Server cannot load (type 32
  or a perk without Value) shows a warning with the affected ids.
- Android: the APK is signed with a permanent release key. **Uninstall the
  previous Android version once** before installing this one (it was signed
  with a temporary key); later updates install over it.
- Tests for the proficiencies file (byte-identical round trip, per-type fields).

---

## V2.6.2 — Proficiencies fixes

- Each perk type shows the field it really uses: *Element* (`ElementId`) for
  the element perks 9 / 13 / 31, *DmgType* (`DamageType`) for Special Magic
  Level (4), *Bestiary* + *Name* (`BestiaryId`, `BestiaryName`) for Bestiary
  Damage (6). The presets write the same fields.
- An empty picker shows "(not set)" instead of -2147483648.
- An unedited `proficiencies.json` saves back byte-identical: perks without a
  `Value` (type 32) no longer get `"Value": 0.0`, whole numbers stay `1`
  instead of `1.0`, and unknown keys keep their place.

---

## V2.6.1 — About window

- The About window shows the full version (e.g. 2.6.1), the supported
  platforms, credits and a link to the GitHub releases.
- README: download table for Windows, Linux and Android, Android app overview.

---

## V2.6 — One version for Windows, Linux and Android

- Windows, Linux and Android now share one version number and are published in
  a single release (desktop archives and the Android APK side by side).
- Android: the update check follows the shared releases (`vX.Y.Z` with an APK).
  The Android app is still in testing.
- Android: dropped a log line that pointed to a server-side script.

---

## V2.5.1 — Project Wolf Server Tools

- The server tools are now called **Project Wolf Server Tools** (main screen
  tile and window title; consistency reports are named `consistency-<date>.tsv`).
- The warning about editing the frozen `client/` tree appears whenever a live
  `client-mehah` folder sits next to it, whatever the project folder is called.

---

## V2.5 — Every legacy client recognised

- The dat signatures of the official clients 7.10 – 10.99 and 13.10 are built
  in (`KnownClients.cs`). The loaded client is named on the main screen (e.g.
  *Tibia 8.60 v1*) and its dat is read with the right structure at once.
  Custom clients that keep their base version's signature are recognised the
  same way; unknown signatures still go through automatic structure / flag
  detection.
- Signature list from Object Builder's `versions.xml` (MIT) plus 10.79 / 10.80
  from ClientConverter. The Android app uses the same list.

---

## V2.4 — Clearer interface

- **Icons show on Linux**: the toolbars use emoji icons and most Linux systems
  have no emoji font, so they appeared as empty boxes. Noto Emoji (SIL OFL) is
  now bundled as a fallback font.
- **Main screen** redesigned: header with version and update check, one
  *Client* card (preset, folder, file format, detected version, counts as
  tiles, a full-width *Load Assets* button), equal tool tiles.
- **Legacy 8.60 editor** redesigned: toolbar with labelled buttons (Compile,
  Import, Search, OTB), a header showing the selected object with its picture
  and shape and a *Save object* button, flags grouped into cards (Ground &
  layering, Use & content, Movement & blocking, Display, Info & market),
  Texture tab in cards, labelled list actions.
- **OTB editor** redesigned the same way (Open / Save / Save as, grouped form).
- Secondary text, tile backgrounds and button hover colours referenced colour
  names the theme does not define, so they never applied; fixed everywhere.
- Fixed: thumbnails in the long legacy and OTB lists loaded for the wrong rows
  once scrolled far (row height did not match what the loader assumed).
- Update window: release notes start at the top; disabled main buttons stay
  readable.

---

## V2.3 — Updates from GitHub

- **The editor checks GitHub for a new release** at startup (in the
  background) and shows what changed, with *Update now*, *Open on GitHub*,
  *Skip this version* and *Later*.
- **Update now** downloads the package for your system, checks it against the
  SHA-256 GitHub publishes for the release file, swaps the program files and
  restarts. The previous files are kept as `*.old` until the next start, and
  a failed update puts them back.
- *Check for updates* button and a *Check at startup* switch on the main
  screen; the installed version is shown next to them.
- Self-update works for the released single-file builds (Linux x64, Windows
  x64) when the program folder is writable; otherwise the window links to the
  GitHub release.

---

## V2.2 — Audit release

Full audit of the editor and fixes for everything it found, plus new tools for
TFS 1.x / 8.60 servers. Round trips stay byte-identical; tests now guard them.

### Fixed — affects every user
- **Startup crash when launched outside the program folder** (desktop shortcut,
  menu entry): bundled files are found next to the executable.
- Settings, Lua scripts and the crash log now live in the per-user config folder
  (`~/.config/TibiaMultiToolEditor`, `%APPDATA%\TibiaMultiToolEditor`); old
  `Settings.json` / `lua_scripts.json` are migrated on first start.
- The Lua window no longer crashes in the single-file release builds.
- A failed settings save no longer resets every preset to defaults; saves are atomic.
- Unexpected errors are logged and shown instead of closing the app
  (`crash.log` in the config folder).
- DataGrid views (Logger, items.xml attributes, Lua results) had no theme and
  rendered empty.
- Lua scripts run in a soft sandbox (no file system / OS access).
- Magick.NET updated (security advisories); package versions pinned to versions
  that exist; Avalonia.Diagnostics only in Debug builds.

### Legacy 8.60 editor (Tibia.dat / Tibia.spr)
- **Texture tab saves**: size, layers, patterns, frames (ids kept at their slot).
- **Slot editor**: assign sprites to slots (double-click / drag), clear slots,
  edit animation mode, loop count and per-frame durations; frame-group selector
  for outfits.
- **Import image**: splits an image into 32×32 tiles, adds them as sprites and
  fills the slots. Replace no longer squashes large images.
- **Import from another client / OBD** works: source read with its own flags,
  next free id, sprites copied and remapped. New sprites appear in the list.
- Preview follows direction, frame group, layer and frame; Play/Stop uses the
  frame durations; thumbnails refresh after edits.
- Compile locks the editor, compiles from a snapshot and shows what changed
  against the original (`-bak`). Closing with uncompiled edits asks first.
- Fixes: crash when switching category after scrolling, Search "Top Effect"
  used the wrong flag, Paste Flags no longer copies trade-as/show-as ids,
  faster sprite reads (one shared file handle), ~130 MB less memory on a
  500k-sprite client.

### Modern editor (appearances + sprite sheets)
- Sprite Editor wrote `catalog-content.json` in camelCase (client showed no
  graphics). One `CatalogWriter` now writes it: lowercase keys, atomic,
  one-time `-orig` backup.
- Flags no longer lost on save: Monk saved as Druid, Market name/vocation,
  market categories 25–27, Lens help ids, Shift A/B, hooks/clothes/default
  action, values clamped by number boxes; an appearance without flags no
  longer inherits the previous one's.
- Undo/redo restores into the right category.
- Deleting sprites/sheets always asks, lists appearances that still use them,
  and backs up sheets first. Publish writes everything new before removing the
  old dat. Compile never writes a blank sheet over an unreadable one.
- OBD import of files that reuse a sprite works; image export truncates files,
  exports the right frame count and reports failures.
- Sprites of unknown size are rejected before packing; sheet id allocation
  respects gaps; Repack is honest (analysis only).
- LZMA uses a dictionary sized to the input (~25× less memory per sheet);
  delete / dedup / remap loops no longer quadratic.

### Server tools
- **Monster Editor edits TFS revscript monsters** (`mType:` calls) by patching
  only the changed calls and loot blocks; unchanged files are never rewritten,
  first save keeps `-bak`. Generated files are marked.
- **OTB editor**: Save (overwrite with one-time `-bak`), created items appear
  in the list, items without a sprite hash can be written.
- **items.xml editor**: Add New proposes an id that exists in items.otb.

### New: Project Wolf Server Tools (legacy clients)
- **Consistency report**: dat ↔ items.otb (stack order, use flags; same results
  as `items_otb.py check --dat`), items.xml ↔ otb, monsters (corpse, lookType,
  loot) and outfits.xml ↔ dat, with filters and TSV export.
- **Things guard**: SHA-256 of Tibia.dat/.spr/.otfi, warning for the frozen
  client tree, comparison with an update-channel manifest.
- **Things diff**: new / art-changed / flag-changed ids against the original
  dat, JSON/TSV export and the follow-up checklist.
- **Export for approval**: staging folder in the server upload layout plus the
  client files with `SHA256SUMS` and the release order.

### Development
- `tests/AssetsEditor.Tests`: round-trip tests for legacy dat/spr, OTB,
  items.xml and revscript monsters (`dotnet test`).

---

## V2.1 — 8.60 / TFS 1.x compatibility update

Full audit against a TFS 1.5 (protocol 8.60) server with an OTClient (Mehah)
8.60 client: extended sprites, frame durations, frame groups, ~55k items,
~511k sprites. Unchanged `Tibia.dat`, `Tibia.spr` and `items.otb` now come
back **byte-identical** after a save, and `items.xml` comes back semantically
identical.

### Legacy DAT/SPR editor
- **Compile works** (it was a stub). Writes the `.dat` and, only when sprites
  changed, the `.spr`. The first compile keeps the originals as `*-bak`, and
  files are written through a temp file and then renamed into place.
- Fast, lossless `.spr` compile: unchanged sprites are copied raw and only
  edited ones are re-encoded (511k sprites in ~1.5 s).
- Empty sprites (spr address 0) no longer read garbage from the file header.
- Saving an object no longer drops **Market** (the category list was 6 of 27,
  and trade-as / show-as / vocation / level were discarded) or **Lens help**
  (ids 1100+), and keeps clothes slots 11–12.
- Animation loop count is written as-is. Ping-pong and counted animations were
  turned into infinite ones; the same bug was fixed in OBD export.
- Market names are written as ISO-8859-1. Non-ASCII names corrupted the dat.
- Flags are written in ascending id order, like the original files.
- Ids are positional. Deleting an object in the middle now clears it instead
  of shifting every later id, and a dat with id gaps is refused.
- Paste Flags replaces the flags instead of merging them.
- Wider field ranges (light, text length, shift, elevation, ground speed),
  so loading and saving no longer clamps real values.
- A known 8.60 signature selects the right dat structure. When the first load
  attempt fails, the `.otfi` flags are tried with every structure before
  guessing, and a read must end exactly at EOF.

### items.xml
- Saving patches the document in place: only entries you changed are
  touched. `decayTo="0"`, `showcount="0"`, nested `field` damage,
  `fromid/toid` ranges, comments and the `iso-8859-1` encoding are kept.
  Before, a save changed the behaviour of ~1,000 items.
- The first definition of a duplicate id wins, as on the server. Renaming an
  item to an id that already exists is refused, and a `-bak` is kept.
- Form fields no longer clamp values (containerSize 220, charges 14400, ...).

### items.otb
- Byte-identical round trip: the client version label is read from the file
  (it was hard-coded to 12.90), and unknown item groups are preserved.
- Saves go through a temp file, and errors are shown in the UI.
- *Create missing items* keeps server id = client id on 1:1 mapped OTBs.
- Warning before saving Podium items (group 15), which TFS 1.x rejects.

### Monster Editor
- TFS revscript monsters (`mType:health(...)` style) are recognised: their
  basic stats are shown, and they are read-only. Before, *Save All*
  overwrote them with empty monsters (health 0, no loot).

---

## V2.0 — MultiTool release

Major expansion: the app is no longer just a DAT/sprite editor — it's a suite
for Canary / OpenTibia server content. Renamed to **Tibia MultiTool Editor**.
Brand new Monster Editor, Proficiencies Editor, and a redesigned hub-style
MainWindow that hosts every tool. Modernised UI across every window.

### New editors

- **Monster Editor** (`MonsterEditor.axaml` + `MonsterData.cs`)
  - Loads an entire Canary `monster/` folder recursively (`.lua` files).
  - Typed editing across 6 tabs: Basic / Outfit / Flags / Bestiary /
    Bosstiary / Raw Blocks.
  - Raw blocks (loot / attacks / defenses / elements / immunities / voices /
    light / events / changeTarget / strategiesTarget / summons) preserved
    verbatim, no data loss on save.
  - **Preview Lua** tab with live-regenerated output matching Canary style.
  - **Auto-fill Bestiary / Bosstiary** — infers class from folder path
    (`reptiles/` → Reptile → `BESTY_RACE_REPTILE`), stars from experience,
    kill counts from stars, boss rarity from HP.
  - Canary-style folder-scan registration — no `monsters.xml` needed.
  - Outfit thumbnails rendered from the loaded DAT.

- **Proficiencies Editor** (`ProficienciesEditor.axaml` + `Proficiencies.cs`)
  - Full canary `proficiencies.json` editor (180+ proficiencies, 27k+ lines).
  - Preserves field order and unknown keys (clean diffs).
  - Typed perks with **smart field visibility** — each Type controls which
    optional fields show (SkillId / AugmentType / SpellId / BestiaryId /
    DamageType / Range).
  - Value hints per type (`"0.05 = 5%"`, `"flat, e.g. 1"`, …).
  - **Preset perk library** — ~35 one-click perks grouped by category
    (Skill +1 / Crit / Leech+Gain / Augment / Bestiary-Element / Range / Other).
  - **New-from-template** — 11 weapon skeletons (1H/2H Sword/Axe/Club,
    Bow/Crossbow/Fist, Wand/Rod) with 7 bootstrap levels.
  - **Level pill bar** — L1…L7 horizontal buttons with add/dup/del/move.
  - DatEditor integration: Proficiency ID flag has a `…` picker that opens
    a name-based list drawn from the loaded JSON.

### Hub-style MainWindow

- MainWindow stays open after Load Assets as the app dashboard.
- **Tools** grid with six cards: DAT / Sprite / Monster / Proficiencies /
  Items XML / OTB. DAT and Sprite unlock after load; the rest work standalone.
- Stats card now shows **Sprites:** count alongside Items / Outfits / Effects
  / Missiles, refreshed after load finishes.
- Load Assets button shows live **%** next to its label.

### Modern UI

- **Theme** — switched from default `FluentTheme` to `Semi.Avalonia`:
  rounded corners, better focus rings, stronger dark palette.
- **Design tokens** in `Resources/Theme.axaml` — shared spacing scale,
  radii, typography sizes, and named surfaces (`card`, `appbar`, `statusbar`,
  `hero`, `spriteSlot`), plus button classes (`primary`, `tool`, `action`,
  `icon`).
- **Custom window chrome** with `ExtendClientAreaToDecorations` and
  `TransparencyLevelHint="Mica,AcrylicBlur,Blur"` — Mica on Win11, acrylic
  fallback elsewhere.
- **Icon font** — `Projektanker.Icons.Avalonia.FontAwesome`; toolbar buttons
  in the main editors now use vector glyphs instead of emoji.
- **Density toggle** in Settings (Comfortable / Cozy / Compact) — live-
  overrides spacing tokens, persisted in `Settings.json`.

### Rename

- **Tibia Asset Editor** → **Tibia MultiTool Editor** across every window
  title, About dialog, `AssemblyName`, `Product`, `FileVersion` (`2.0.0`).
- Namespace `Assets_Editor` kept as-is to avoid breaking protobuf bindings
  and user `Settings.json` files.

### Fixes / polish

- **Flicker on virtualised lists** — cleared Semi's default brush
  transitions on `ListBoxItem` / `Image` / `Border.spriteSlot` /
  `ProgressBar` so sprite tiles stop shimmering during load and scroll.
- **Primary-button hover flicker** — moved hover selector from
  `:pointerover /template/ ContentPresenter` to `:pointerover` on the
  Button itself, eliminating the dual-setter race with Semi transitions.
- **Load Assets button preservation** — no longer replaces `Button.Content`
  (which blew away the inner ProgressBar); updates only a named label so
  the progress widget survives multiple loads.
- **Recent-file menus** — added `RecentProficiencies` and
  `RecentMonsterFolders` to `Settings.json`.

---

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
