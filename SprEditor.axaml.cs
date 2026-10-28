using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using Newtonsoft.Json;
using SkiaSharp;
using static Assets_Editor.MainWindow;

namespace Assets_Editor
{
    public partial class SprEditor : Window
    {
        private DatEditor _editor;

        public SprEditor(DatEditor editor)
        {
            InitializeComponent();
            _editor = editor;

            // File drops (from file manager) still go through DragDrop.
            DragDrop.SetAllowDrop(SheetWrap, true);
            SheetWrap.AddHandler(DragDrop.DragOverEvent, SheetWrap_DragOver);
            SheetWrap.AddHandler(DragDrop.DropEvent, SheetWrap_Drop);

            // Internal tile move uses raw pointer events — DragDrop.DoDragDrop is flaky
            // on Linux/X11, and swapping inside a single control doesn't need the OS clipboard.
            SheetWrap.PointerPressed  += SheetWrap_PointerPressed;
            SheetWrap.PointerReleased += SheetWrap_PointerReleased;

            // Make SheetWrap focusable so it can receive keyboard events.
            SheetWrap.Focusable = true;

            // Right-click on SheetsList → context menu with Delete/Edit
            SheetsList.PointerReleased += SheetsList_PointerReleased;

            // Tunnel so the key handler fires before any child control can swallow it.
            AddHandler(KeyDownEvent, SprEditor_KeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }

        private async void SprEditor_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            // Ctrl+A — select all filled tiles
            if (e.Key == Avalonia.Input.Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                ClearSelection();
                for (int i = 0; i < SheetWrap.Children.Count; i++)
                {
                    if (_tilePngs.ContainsKey(i))
                    {
                        _selectedTiles.Add(i);
                        UpdateTileSelectionVisual(i);
                    }
                }
                SprStatusText.Text = $"Selected {_selectedTiles.Count} sprite(s).";
                e.Handled = true;
                return;
            }

            // Escape — clear selection
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                ClearSelection();
                SprStatusText.Text = "Selection cleared.";
                e.Handled = true;
                return;
            }

            if (e.Key != Avalonia.Input.Key.Delete) return;

            // Delete selected tiles (multi) or focused tile (single)
            if (_selectedTiles.Count > 0)
            {
                var filled = _selectedTiles.Where(i => _tilePngs.ContainsKey(i)).ToList();
                if (filled.Count == 0) return;

                bool ok = await ConfirmHere(
                    "Delete sprites",
                    $"Clear {filled.Count} selected sprite(s)?" + TileUsageWarning(filled));
                if (!ok) return;

                foreach (int i in filled)
                    MarkTileDeleted(i);
                ClearSelection();
                SprStatusText.Text = $"Cleared {filled.Count} sprite(s). Click \U0001f4be to persist.";
                e.Handled = true;
                return;
            }

            // Single tile delete (focused)
            int idx = focusedIndex;
            if (idx < 0 || idx >= SheetWrap.Children.Count) return;
            if (!_tilePngs.ContainsKey(idx)) return;

            bool confirm = await ConfirmHere(
                "Delete sprite",
                $"Clear sprite from tile {idx}?" + TileUsageWarning(new[] { idx }));
            if (!confirm) return;

            MarkTileDeleted(idx);
            SprStatusText.Text = $"Cleared tile {idx}. Click \U0001f4be to persist.";
            e.Handled = true;
        }

        private void ShowTileContextMenu(int idx, PointerPressedEventArgs e)
        {
            var menu = new ContextMenu();
            bool hasTile = _tilePngs.ContainsKey(idx);
            bool hasSelection = _selectedTiles.Count > 0;
            int filledInSelection = hasSelection
                ? _selectedTiles.Count(i => _tilePngs.ContainsKey(i))
                : 0;

            // ── Delete single tile ──
            var deleteSingle = new MenuItem { Header = $"Usu\u0144 sprite (tile {idx})", IsEnabled = hasTile };
            deleteSingle.Click += async (_, _) =>
            {
                bool ok = await ConfirmHere("Delete sprite", $"Clear sprite from tile {idx}?" + TileUsageWarning(new[] { idx }));
                if (!ok) return;
                MarkTileDeleted(idx);
                SprStatusText.Text = $"Cleared tile {idx}. Click \U0001f4be to persist.";
            };
            menu.Items.Add(deleteSingle);

            // ── Delete selected tiles ──
            var deleteSelected = new MenuItem
            {
                Header = $"Usu\u0144 zaznaczone ({filledInSelection})",
                IsEnabled = filledInSelection > 0
            };
            deleteSelected.Click += async (_, _) =>
            {
                var filled = _selectedTiles.Where(i => _tilePngs.ContainsKey(i)).ToList();
                bool ok = await ConfirmHere("Delete sprites", $"Clear {filled.Count} selected sprite(s)?" + TileUsageWarning(filled));
                if (!ok) return;
                foreach (int i in filled)
                    MarkTileDeleted(i);
                ClearSelection();
                SprStatusText.Text = $"Cleared {filled.Count} sprite(s). Click \U0001f4be to persist.";
            };
            menu.Items.Add(deleteSelected);

            menu.Items.Add(new Separator());

            // ── Export single tile ──
            var exportSingle = new MenuItem { Header = $"Eksportuj sprite (tile {idx})", IsEnabled = hasTile };
            exportSingle.Click += async (_, _) => await ExportTileAsPngAsync(idx);
            menu.Items.Add(exportSingle);

            // ── Select all ──
            var selectAll = new MenuItem { Header = "Zaznacz wszystkie (Ctrl+A)" };
            selectAll.Click += (_, _) =>
            {
                ClearSelection();
                for (int i = 0; i < SheetWrap.Children.Count; i++)
                {
                    if (_tilePngs.ContainsKey(i))
                    {
                        _selectedTiles.Add(i);
                        UpdateTileSelectionVisual(i);
                    }
                }
                SprStatusText.Text = $"Selected {_selectedTiles.Count} sprite(s).";
            };
            menu.Items.Add(selectAll);

            // ── Clear selection ──
            if (hasSelection)
            {
                var clearSel = new MenuItem { Header = "Wyczy\u015b\u0107 zaznaczenie (Esc)" };
                clearSel.Click += (_, _) =>
                {
                    ClearSelection();
                    SprStatusText.Text = "Selection cleared.";
                };
                menu.Items.Add(clearSel);
            }

            // Open the context menu at the pointer position on the SheetWrap.
            menu.Open(SheetWrap);
        }

        private async void DeleteSelected_Click(object? sender, RoutedEventArgs e)
        {
            // If there's a selection, delete all selected filled tiles
            if (_selectedTiles.Count > 0)
            {
                var filled = _selectedTiles.Where(i => _tilePngs.ContainsKey(i)).ToList();
                if (filled.Count == 0)
                {
                    SprStatusText.Text = "No filled sprites in selection.";
                    return;
                }

                bool ok = await ConfirmHere(
                    "Delete sprites",
                    $"Clear {filled.Count} selected sprite(s)?" + TileUsageWarning(filled));
                if (!ok) return;

                foreach (int i in filled)
                    MarkTileDeleted(i);
                ClearSelection();
                SprStatusText.Text = $"Cleared {filled.Count} sprite(s). Click \U0001f4be to persist.";
                return;
            }

            // No selection — delete the focused (hovered) tile
            int idx = focusedIndex;
            if (idx < 0 || idx >= SheetWrap.Children.Count || !_tilePngs.ContainsKey(idx))
            {
                SprStatusText.Text = "Select or hover a sprite to delete.";
                return;
            }

            bool confirm = await ConfirmHere(
                "Delete sprite",
                $"Clear sprite from tile {idx}?" + TileUsageWarning(new[] { idx }));
            if (!confirm) return;

            MarkTileDeleted(idx);
            SprStatusText.Text = $"Cleared tile {idx}. Click \U0001f4be to persist.";
        }

        // Appearances using the sprites behind these tile indices of the current sheet,
        // formatted for a confirm dialog ("" when unused or no sheet is open).
        private string TileUsageWarning(System.Collections.Generic.IEnumerable<int> tileIdxs)
        {
            if (CurrentSheet == null) return "";
            var sheet = CurrentSheet;
            return DatEditor.SpriteUsageWarning(tileIdxs.Select(i => (uint)(sheet.FirstSpriteid + i)));
        }

        // Show confirm dialog parented to this SprEditor window (not MainWindow).
        private async System.Threading.Tasks.Task<bool> ConfirmHere(string title, string message)
        {
            var box = MessageBoxManager.GetMessageBoxStandard(title, message, ButtonEnum.YesNo, MsBox.Avalonia.Enums.Icon.Warning);
            var result = await box.ShowWindowDialogAsync(this);
            return result == ButtonResult.Yes;
        }

        private int SpriteWidth = 32;
        private int SpriteHeight = 32;
        private int SprType = 0;
        private int focusedIndex = 0;
        public static ObservableCollection<ShowList> CustomSheetsList = new ObservableCollection<ShowList>();
        private Catalog? CurrentSheet = null;

        // PNG-encoded bytes for each tile index in SheetWrap. This is the source of truth for
        // save — we never round-trip Avalonia.Bitmap.Save(Stream), which has quirks on Linux.
        private readonly System.Collections.Generic.Dictionary<int, byte[]> _tilePngs = new();

        // Multi-selection state: tracks which tile indices are currently selected.
        private readonly System.Collections.Generic.HashSet<int> _selectedTiles = new();
        private int _lastClickedTile = -1; // anchor for Shift+Click range selection

        // Tile indices the user has deleted since the last save. At save time these are
        // translated to global sprite IDs and removed from SprLists + AllSprList so the
        // sprite list view stops showing them. Re-filling a tile cancels its pending delete.
        private readonly System.Collections.Generic.HashSet<int> _pendingTileDeletions = new();

        // Helper: blanks the tile and records a pending deletion so the save flow can
        // also remove the corresponding sprite ID from the global sprite list.
        private void MarkTileDeleted(int idx)
        {
            SetTilePng(idx, null);
            if (CurrentSheet != null)
            {
                _pendingTileDeletions.Add(idx);
                MainWindow.Log($"[SprEditor] Marked tile {idx} for deletion (sheet {CurrentSheet.File}, spriteId={CurrentSheet.FirstSpriteid + idx}). Pending={_pendingTileDeletions.Count}");
            }
            else
            {
                MainWindow.Log($"[SprEditor] Tile {idx} cleared but NO CurrentSheet — delete will not be persisted.", "Warning");
            }
        }

        // ── Sheet grid in the viewer ──────────────────────────────────────────

        private void CreateNewSheetImg(int sprType, bool modify)
        {
            if (!modify)
                CurrentSheet = null;

            SprType = sprType;
            var sprLayout = DatEditor.GetSpriteLayout(sprType);
            SpriteWidth = sprLayout.SpriteSizeX;
            SpriteHeight = sprLayout.SpriteSizeY;

            SheetWrap.Children.Clear();
            _tilePngs.Clear();
            _pendingTileDeletions.Clear();

            int sprMaxW = DatEditor.SprSheetWidth;
            int sprMaxH = DatEditor.SprSheetHeight;

            int count = sprMaxW / SpriteWidth * sprMaxH / SpriteHeight;
            for (int i = 0; i < count; i++)
            {
                var border = new Border
                {
                    Width = SpriteWidth,
                    Height = SpriteHeight,
                    Margin = new Avalonia.Thickness(1, 1, 0, 0),
                    Tag = i,
                    Background = new SolidColorBrush(Color.FromArgb(255, 255, 0, 255))
                };

                var img = new Image
                {
                    Width = SpriteWidth,
                    Height = SpriteHeight,
                    Tag = i
                };

                border.Child = img;
                border.PointerEntered += Border_PointerEntered;
                border.PointerExited += Border_PointerExited;
                SheetWrap.Children.Add(border);
            }
            SheetWrap.Width = sprMaxW + 1 + sprMaxW / SpriteWidth;
            SheetWrap.Height = sprMaxH + 1 + sprMaxH / SpriteHeight;

            // If editing an existing sheet, fill tiles
            if (modify && CurrentSheet != null)
                LoadSheetTilesIntoViewer(CurrentSheet);
        }

        private void LoadSheetTilesIntoViewer(Catalog sheet)
        {
            string sprPath = Path.Combine(MainWindow._assetsPath, sheet.File);
            if (!File.Exists(sprPath)) return;

            try
            {
                using SKBitmap bmp = LZMA.DecompressFileLZMA(sprPath);
                var layout = DatEditor.GetSpriteLayout(sheet.SpriteType);
                int tWidth = layout.SpriteSizeX;
                int tHeight = layout.SpriteSizeY;
                int cols = layout.Cols;

                int tileCount = SheetWrap.Children.Count;
                for (int i = 0; i < tileCount; i++)
                {
                    int col = i % cols;
                    int row = i / cols;

                    var tile = new SKBitmap(tWidth, tHeight);
                    using (var canvas = new SKCanvas(tile))
                        canvas.DrawBitmap(bmp,
                            SKRect.Create(col * tWidth, row * tHeight, tWidth, tHeight),
                            SKRect.Create(0, 0, tWidth, tHeight));

                    using var img = SKImage.FromBitmap(tile);
                    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                    byte[] pngBytes = data.ToArray();
                    _tilePngs[i] = pngBytes;

                    if (SheetWrap.Children[i] is Border border && border.Child is Image image)
                    {
                        using var ms = new MemoryStream(pngBytes);
                        image.Source = new Avalonia.Media.Imaging.Bitmap(ms);
                        border.Background = Brushes.Transparent;
                    }
                    tile.Dispose();
                }
            }
            catch (Exception ex)
            {
                SprStatusText.Text = $"Error loading sheet: {ex.Message}";
            }
        }

        private void Border_PointerEntered(object? sender, PointerEventArgs e)
        {
            if (sender is Border border)
            {
                focusedIndex = (int)(border.Tag ?? 0);

                // Grab keyboard focus so Del/Ctrl+A/Esc work.
                SheetWrap.Focus();

                if (!_selectedTiles.Contains(focusedIndex))
                {
                    border.BorderBrush = new SolidColorBrush(Colors.Blue);
                    border.BorderThickness = new Avalonia.Thickness(2);
                }

                // Show tile info
                if (CurrentSheet != null)
                {
                    uint sprId = (uint)(CurrentSheet.FirstSpriteid + focusedIndex);
                    var layout = DatEditor.GetSpriteLayout(CurrentSheet.SpriteType);
                    int col = focusedIndex % layout.Cols;
                    int row = focusedIndex / layout.Cols;
                    TileInfoText.Text = $"Tile [{col},{row}]  SprID: {sprId}  Size: {layout.SpriteSizeX}x{layout.SpriteSizeY}";
                }
            }
        }

        private void Border_PointerExited(object? sender, PointerEventArgs e)
        {
            if (sender is Border border)
            {
                int idx = (int)(border.Tag ?? 0);
                if (_selectedTiles.Contains(idx))
                {
                    border.BorderBrush = new SolidColorBrush(Colors.Orange);
                    border.BorderThickness = new Avalonia.Thickness(2);
                }
                else
                {
                    bool showGrid = GridOverlayToggle.IsChecked == true;
                    border.BorderBrush = showGrid ? new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)) : null;
                    border.BorderThickness = showGrid ? new Avalonia.Thickness(1) : new Avalonia.Thickness(0);
                }
            }
            TileInfoText.Text = "";
        }

        private void UpdateTileSelectionVisual(int idx)
        {
            if (idx < 0 || idx >= SheetWrap.Children.Count) return;
            if (SheetWrap.Children[idx] is not Border border) return;

            if (_selectedTiles.Contains(idx))
            {
                border.BorderBrush = new SolidColorBrush(Colors.Orange);
                border.BorderThickness = new Avalonia.Thickness(2);
            }
            else
            {
                bool showGrid = GridOverlayToggle.IsChecked == true;
                border.BorderBrush = showGrid ? new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)) : null;
                border.BorderThickness = showGrid ? new Avalonia.Thickness(1) : new Avalonia.Thickness(0);
            }
        }

        private void ClearSelection()
        {
            var old = _selectedTiles.ToList();
            _selectedTiles.Clear();
            foreach (int idx in old)
                UpdateTileSelectionVisual(idx);
        }

        private void GridOverlayToggle_Changed(object? sender, RoutedEventArgs e)
        {
            bool show = GridOverlayToggle.IsChecked == true;
            foreach (var child in SheetWrap.Children)
            {
                if (child is Border border)
                {
                    border.BorderBrush = show ? new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)) : null;
                    border.BorderThickness = show ? new Avalonia.Thickness(1) : new Avalonia.Thickness(0);
                }
            }
        }

        // ── Toolbar: Save current viewer to assets ────────────────────────────

        private void ImportSheet_Click(object? sender, RoutedEventArgs e)
        {
            if (SheetWrap.Children.Count == 0)
            {
                SprStatusText.Text = "Create or edit a sheet first.";
                return;
            }

            try
            {
                // Reconstruct 384x384 bitmap from the tile grid in SheetWrap
                int w = DatEditor.SprSheetWidth;
                int h = DatEditor.SprSheetHeight;
                using var sheetBmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var canvas = new SKCanvas(sheetBmp);
                canvas.Clear(SKColors.Transparent);

                var layout = DatEditor.GetSpriteLayout(SprType);
                int tWidth = layout.SpriteSizeX;
                int tHeight = layout.SpriteSizeY;
                int cols = layout.Cols;

                for (int i = 0; i < SheetWrap.Children.Count; i++)
                {
                    if (!_tilePngs.TryGetValue(i, out var png) || png == null || png.Length == 0)
                        continue;
                    using var tileBmp = SKBitmap.Decode(png);
                    if (tileBmp == null) continue;

                    int col = i % cols;
                    int row = i / cols;
                    canvas.DrawBitmap(tileBmp, SKRect.Create(0, 0, tWidth, tHeight),
                                               SKRect.Create(col * tWidth, row * tHeight, tWidth, tHeight));
                }

                // Brand-new blank sheet — register a fresh catalog entry with the next
                // available sprite-ID range and update MainWindow.SprLists / AllSprList.
                if (CurrentSheet == null)
                {
                    int firstId = NextFreeSpriteId();

                    int tileCount = layout.Cols * layout.Rows;
                    int lastId = firstId + tileCount - 1;

                    // Tibia assets use content-addressed names: sprites-<sha256>.bmp.lzma
                    // where the hash is over the raw pixel buffer. Match that convention so
                    // the written file visually belongs with the rest of the sheet files.
                    byte[] pixels = sheetBmp.Bytes;
                    using var sha = System.Security.Cryptography.SHA256.Create();
                    string hash = Convert.ToHexString(sha.ComputeHash(pixels)).ToLowerInvariant();
                    string sheetFilename = $"sprites-{hash}.bmp.lzma";

                    var newSheet = new Catalog
                    {
                        Type = "sprite",
                        File = sheetFilename,
                        SpriteType = SprType,
                        FirstSpriteid = firstId,
                        LastSpriteid = lastId,
                    };

                    // Sheet file first, catalog second: the catalog must never point at a
                    // file that isn't on disk.
                    string newPath = Path.Combine(MainWindow._assetsPath, newSheet.File);
                    LZMA.ExportLzmaFile(sheetBmp, ref newPath);
                    MainWindow.catalog.Add(newSheet);
                    SaveCatalog();
                    CurrentSheet = newSheet;

                    RegisterNewSpriteIds(firstId, lastId);

                    AddToSheetsList(CurrentSheet);
                    SprStatusText.Text = $"New sheet registered: {sheetFilename} (IDs {firstId}–{lastId}).";
                }
                else
                {
                    string outPath = Path.Combine(MainWindow._assetsPath, CurrentSheet.File);
                    LZMA.ExportLzmaFile(sheetBmp, ref outPath);
                }

                // Invalidate cached SprLists entries so they reload next time
                for (int i = CurrentSheet.FirstSpriteid; i <= CurrentSheet.LastSpriteid; i++)
                {
                    if (MainWindow.SprLists.ContainsKey(i))
                        MainWindow.SprLists[i] = null!;
                }

                // Drop tiles the user has deleted since the last save from SprLists + AllSprList
                // so the sprite list view actually loses them instead of showing a blank slot.
                int removedFromList = 0;
                MainWindow.Log($"[SprEditor] ImportSheet_Click: pending deletions = {_pendingTileDeletions.Count}");
                if (_pendingTileDeletions.Count > 0)
                {
                    var idsToRemove = new System.Collections.Generic.List<uint>();
                    foreach (int tileIdx in _pendingTileDeletions)
                    {
                        int spriteId = CurrentSheet.FirstSpriteid + tileIdx;
                        if (spriteId < CurrentSheet.FirstSpriteid || spriteId > CurrentSheet.LastSpriteid) continue;
                        MainWindow.SprLists.TryRemove(spriteId, out _);
                        idsToRemove.Add((uint)spriteId);
                    }
                    MainWindow.Log($"[SprEditor] Removing {idsToRemove.Count} sprite IDs from AllSprList (AllSprList count before: {MainWindow.AllSprList.Count}): {string.Join(",", idsToRemove)}");
                    if (idsToRemove.Count > 0)
                        _editor.SafeRemoveFromAllSprList(idsToRemove);
                    MainWindow.Log($"[SprEditor] AllSprList count after: {MainWindow.AllSprList.Count}");
                    removedFromList = idsToRemove.Count;
                    _pendingTileDeletions.Clear();
                }

                SprStatusText.Text = removedFromList > 0
                    ? $"Saved: {CurrentSheet.File} (IDs {CurrentSheet.FirstSpriteid}–{CurrentSheet.LastSpriteid}). Removed {removedFromList} sprite(s) from list."
                    : $"Saved: {CurrentSheet.File} (IDs {CurrentSheet.FirstSpriteid}–{CurrentSheet.LastSpriteid}).";
            }
            catch (Exception ex)
            {
                SprStatusText.Text = $"Save failed: {ex.GetType().Name}: {ex.Message}";
            }
        }

        // ── Toolbar: Export current sheet as PNG ──────────────────────────────

        private async void ExportSheet_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentSheet == null)
            {
                SprStatusText.Text = "No sheet selected.";
                return;
            }

            string sprPath = Path.Combine(MainWindow._assetsPath, CurrentSheet.File);
            if (!File.Exists(sprPath))
            {
                SprStatusText.Text = "Sheet file not found on disk.";
                return;
            }

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Sheet as PNG",
                DefaultExtension = "png",
                SuggestedFileName = Path.GetFileNameWithoutExtension(CurrentSheet.File),
                FileTypeChoices = [new FilePickerFileType("PNG Image") { Patterns = ["*.png"] }]
            });

            if (file == null) return;

            try
            {
                using SKBitmap bmp = LZMA.DecompressFileLZMA(sprPath);
                using var img = SKImage.FromBitmap(bmp);
                using var stream = await file.OpenWriteAsync();
                img.Encode(SKEncodedImageFormat.Png, 100).SaveTo(stream);
                SprStatusText.Text = "Sheet exported.";
            }
            catch (Exception ex)
            {
                SprStatusText.Text = $"Export failed: {ex.Message}";
            }
        }

        // ── Import PNG as new sheet ───────────────────────────────────────────

        private async void ImportPngAsSheet_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import PNG as Sprite Sheet",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("PNG Image") { Patterns = ["*.png", "*.bmp"] }]
            });

            if (files.Count == 0) return;

            if (string.IsNullOrEmpty(MainWindow._assetsPath) || !Directory.Exists(MainWindow._assetsPath))
            {
                SprStatusText.Text = "Assets path not set. Load assets first.";
                return;
            }

            try
            {
                string srcPath = files[0].Path.LocalPath;
                using var srcBmp = SKBitmap.Decode(srcPath);
                if (srcBmp == null) { SprStatusText.Text = "Could not decode image."; return; }

                // Resize to 384x384 if needed
                SKBitmap sheetBmp;
                if (srcBmp.Width == DatEditor.SprSheetWidth && srcBmp.Height == DatEditor.SprSheetHeight)
                    sheetBmp = srcBmp.Copy();
                else
                    sheetBmp = srcBmp.Resize(new SKImageInfo(DatEditor.SprSheetWidth, DatEditor.SprSheetHeight,
                        SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);

                // Determine next available sprite ID (above catalog ranges and in-memory sprites)
                int firstId = NextFreeSpriteId();

                int sprType = 0; // 32x32
                var layout = DatEditor.GetSpriteLayout(sprType);
                int tileCount = layout.Cols * layout.Rows;
                int lastId = firstId + tileCount - 1;

                using var sha = System.Security.Cryptography.SHA256.Create();
                string hash = Convert.ToHexString(sha.ComputeHash(sheetBmp.Bytes)).ToLowerInvariant();
                string sheetFilename = $"sprites-{hash}.bmp.lzma";
                string outPath = Path.Combine(MainWindow._assetsPath, sheetFilename);
                LZMA.ExportLzmaFile(sheetBmp, ref outPath);
                sheetBmp.Dispose();

                // Register in catalog
                var newEntry = new Catalog
                {
                    Type = "sprite",
                    File = sheetFilename,
                    SpriteType = sprType,
                    FirstSpriteid = firstId,
                    LastSpriteid = lastId
                };
                MainWindow.catalog.Add(newEntry);

                // Save updated catalog-content.json
                SaveCatalog();

                // Update SprLists and AllSprList with exactly the new ids
                RegisterNewSpriteIds(firstId, lastId);

                SprStatusText.Text = $"Imported as {sheetFilename} (IDs {firstId}–{lastId})";

                // Open the new sheet in the viewer
                CurrentSheet = newEntry;
                CreateNewSheetImg(sprType, true);
                AddToSheetsList(newEntry);
            }
            catch (Exception ex)
            {
                SprStatusText.Text = $"Import failed: {ex.Message}";
            }
        }

        // First sprite id above every id in use — catalog ranges *and* sprites that only
        // live in memory (imported but not compiled into a sheet yet).
        private static int NextFreeSpriteId()
        {
            int max = 0;
            foreach (var c in MainWindow.catalog)
                if (c.Type == "sprite" && c.LastSpriteid != int.MinValue && c.LastSpriteid > max)
                    max = c.LastSpriteid;
            foreach (var k in MainWindow.SprLists.Keys)
                if (k > max) max = k;
            return max + 1;
        }

        // Mark [firstId, lastId] as lazily loadable from their sheet and add them to the
        // global sprite list (only ids not already listed).
        private static void RegisterNewSpriteIds(int firstId, int lastId)
        {
            var listed = new System.Collections.Generic.HashSet<uint>(MainWindow.AllSprList.Select(s => s.Id));
            for (int i = firstId; i <= lastId; i++)
            {
                MainWindow.SprLists[i] = null!;
                if (listed.Add((uint)i))
                    MainWindow.AllSprList.Add(new ShowList { Id = (uint)i });
            }
        }

        private void SaveCatalog()
        {
            // Tibia's catalog uses ALL-lowercase field names (type/file/spritetype/…);
            // CatalogWriter is the only place that serializes it.
            CatalogWriter.Write(MainWindow._assetsPath, MainWindow.catalog);
        }

        private void AddToSheetsList(Catalog cat)
        {
            string sprPath = Path.Combine(MainWindow._assetsPath, cat.File);
            if (!File.Exists(sprPath)) return;
            try
            {
                using SKBitmap sheetBmp = LZMA.DecompressFileLZMA(sprPath);
                using var ms = new MemoryStream();
                using var img = SKImage.FromBitmap(sheetBmp);
                img.Encode(SKEncodedImageFormat.Png, 100).SaveTo(ms);
                ms.Position = 0;
                CustomSheetsList.Add(new ShowList
                {
                    Id = (uint)cat.FirstSpriteid,
                    Image = new Avalonia.Media.Imaging.Bitmap(ms),
                    Name = cat.File
                });
                SheetsList.ItemsSource = CustomSheetsList;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SprEditor] Could not load sheet preview {cat.File}: {ex.Message}", "Warning");
            }
        }

        // ── Sheet list buttons ────────────────────────────────────────────────

        private async void CreateSheet_Click(object? sender, RoutedEventArgs e)
        {
            var picker = new SheetSizePicker();
            var ok = await picker.ShowDialog<bool>(this);
            if (!ok) return;

            int sprType = picker.SelectedSpriteType;
            var layout = DatEditor.GetSpriteLayout(sprType);
            CreateNewSheetImg(sprType, false);
            SprStatusText.Text = $"New blank {layout.SpriteSizeX}×{layout.SpriteSizeY} sheet created.";
        }

        private void EditSheet_Click(object? sender, RoutedEventArgs e)
        {
            if (SheetsList.SelectedItem is ShowList showList)
            {
                CurrentSheet = MainWindow.catalog.Find(x => x.File == showList.Name);
                if (CurrentSheet != null)
                {
                    CreateNewSheetImg(CurrentSheet.SpriteType, true);
                    SprStatusText.Text = $"Editing: {CurrentSheet.File}";
                }
            }
        }

        private async void DeleteSheet_Click(object? sender, RoutedEventArgs e)
        {
            // Try selected item from list first, then fall back to currently edited sheet.
            ShowList? showList = SheetsList.SelectedItem as ShowList;
            Catalog? cat = null;

            if (showList != null)
                cat = MainWindow.catalog.Find(x => x.File == showList.Name);

            // Fallback: if nothing selected in list, use the currently edited sheet.
            if (cat == null && CurrentSheet != null)
            {
                cat = CurrentSheet;
                showList = CustomSheetsList.FirstOrDefault(s => s.Name == cat.File);
            }

            if (cat == null)
            {
                SprStatusText.Text = "Select a sheet from the list or edit one first.";
                return;
            }

            var idsToRemove = new System.Collections.Generic.List<uint>();
            for (int id = cat.FirstSpriteid; id <= cat.LastSpriteid; id++)
                idsToRemove.Add((uint)id);

            bool ok = await ConfirmHere(
                "Delete sheet",
                $"Delete sheet {cat.File}?\nSprite IDs {cat.FirstSpriteid}–{cat.LastSpriteid} will be removed.\n\n" +
                "The sheet file is kept once as <sheet>-orig." +
                DatEditor.SpriteUsageWarning(idsToRemove));
            if (!ok) return;

            try
            {
                // 1. Catalog on disk first: if this fails nothing has been deleted yet.
                string sprPath = Path.Combine(MainWindow._assetsPath, cat.File);
                int catIndex = MainWindow.catalog.IndexOf(cat);
                MainWindow.catalog.Remove(cat);
                try { SaveCatalog(); }
                catch
                {
                    if (catIndex >= 0) MainWindow.catalog.Insert(catIndex, cat);
                    throw;
                }

                // 2. Delete the .bmp.lzma file from disk (after a one-time backup)
                if (File.Exists(sprPath))
                {
                    CatalogWriter.BackupOnce(sprPath);
                    File.Delete(sprPath);
                }

                // 3. Remove sprite IDs from SprLists and AllSprList
                foreach (var id in idsToRemove)
                    MainWindow.SprLists.TryRemove((int)id, out _);
                _editor.SafeRemoveFromAllSprList(idsToRemove);

                // 4. Remove from the UI sheets list
                if (showList != null)
                    CustomSheetsList.Remove(showList);

                // 5. Clear the viewer if this sheet was being edited
                if (CurrentSheet == cat)
                {
                    CurrentSheet = null;
                    SheetWrap.Children.Clear();
                    _tilePngs.Clear();
                    _selectedTiles.Clear();
                }

                SprStatusText.Text = $"Deleted sheet {cat.File} (IDs {cat.FirstSpriteid}–{cat.LastSpriteid}).";
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SprEditor] Delete sheet {cat.File} failed: {ex.GetType().Name}: {ex.Message}", "Error");
                SprStatusText.Text = $"Delete failed: {ex.Message}";
            }
        }

        private void SheetsList_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Right) return;

            var selected = SheetsList.SelectedItem as ShowList;
            if (selected == null) return;

            var menu = new ContextMenu();

            var editItem = new MenuItem { Header = "Edytuj sheet" };
            editItem.Click += (_, _) => EditSheet_Click(null, null!);
            menu.Items.Add(editItem);

            var deleteItem = new MenuItem { Header = "Usu\u0144 sheet" };
            deleteItem.Click += (_, _) => DeleteSheet_Click(null, null!);
            menu.Items.Add(deleteItem);

            menu.Open(SheetsList);
            e.Handled = true;
        }

        private void SearchSpr_Click(object? sender, RoutedEventArgs e)
        {
            int searchId = (int)(A_SearchSprId.Value ?? 1);
            foreach (var cat in MainWindow.catalog)
            {
                if (searchId < cat.FirstSpriteid || searchId > cat.LastSpriteid) continue;
                string sprPath = Path.Combine(MainWindow._assetsPath, cat.File);
                if (!File.Exists(sprPath)) continue;

                if (CustomSheetsList.Any(s => s.Id == (uint)cat.FirstSpriteid)) continue;
                AddToSheetsList(cat);
            }
            SheetsList.ItemsSource = CustomSheetsList;
        }

        // ── Called by DatEditor to jump to a specific sprite sheet ────────────

        public void OpenForSpriteId(int spriteId)
        {
            foreach (var cat in MainWindow.catalog)
            {
                if (spriteId < cat.FirstSpriteid || spriteId > cat.LastSpriteid) continue;
                string sprPath = Path.Combine(MainWindow._assetsPath, cat.File);
                if (!File.Exists(sprPath)) continue;

                if (!CustomSheetsList.Any(s => s.Id == (uint)cat.FirstSpriteid))
                    AddToSheetsList(cat);

                SheetsList.ItemsSource = CustomSheetsList;
                var entry = CustomSheetsList.FirstOrDefault(s => s.Id == (uint)cat.FirstSpriteid);
                if (entry != null) SheetsList.SelectedItem = entry;

                CurrentSheet = cat;
                CreateNewSheetImg(cat.SpriteType, true);
                return;
            }
        }

        // ── Multi-image import + drag/drop ────────────────────────────────────

        // Decode a PNG/BMP file at path and fit it into the current tile size without
        // stretching. Tibia draws sprites anchored at the bottom-right corner of the
        // tile they stand on, so source art smaller than the tile is padded with
        // transparency into the bottom-right — making e.g. a 32×32 creature rendered
        // in a 64×64 slot look identical to a native 32×32 creature. Oversized sources
        // are downscaled with preserved aspect ratio, then likewise bottom-right aligned.
        private byte[]? LoadTileImagePng(string path)
        {
            using var src = SKBitmap.Decode(path);
            if (src == null) return null;

            int srcW = src.Width, srcH = src.Height;
            int tileW = SpriteWidth, tileH = SpriteHeight;

            int dstW, dstH;
            SKBitmap? resized = null;
            SKBitmap drawSrc;

            if (srcW <= tileW && srcH <= tileH)
            {
                dstW = srcW; dstH = srcH;
                drawSrc = src;
            }
            else
            {
                double scale = Math.Min((double)tileW / srcW, (double)tileH / srcH);
                dstW = Math.Max(1, (int)Math.Round(srcW * scale));
                dstH = Math.Max(1, (int)Math.Round(srcH * scale));
                resized = src.Resize(
                    new SKImageInfo(dstW, dstH, SKColorType.Bgra8888, SKAlphaType.Premul),
                    SKFilterQuality.High);
                if (resized == null) return null;
                drawSrc = resized;
            }

            using var canvasBmp = new SKBitmap(tileW, tileH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(canvasBmp))
            {
                canvas.Clear(SKColors.Transparent);
                int x = tileW - dstW;
                int y = tileH - dstH;
                canvas.DrawBitmap(drawSrc, SKRect.Create(x, y, dstW, dstH));
            }
            resized?.Dispose();

            using var img = SKImage.FromBitmap(canvasBmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        private void SetTilePng(int idx, byte[]? png)
        {
            if (idx < 0 || idx >= SheetWrap.Children.Count) return;
            if (SheetWrap.Children[idx] is not Border border || border.Child is not Image image) return;

            if (png == null || png.Length == 0)
            {
                _tilePngs.Remove(idx);
                image.Source = null;
                border.Background = new SolidColorBrush(Color.FromArgb(255, 255, 0, 255));
                return;
            }

            _tilePngs[idx] = png;
            _pendingTileDeletions.Remove(idx);
            using var ms = new MemoryStream(png);
            image.Source = new Avalonia.Media.Imaging.Bitmap(ms);
            border.Background = Brushes.Transparent;
        }

        // Compute the tile index under a point in SheetWrap-local coordinates.
        private int HitTestTile(Avalonia.Point p)
        {
            var layout = DatEditor.GetSpriteLayout(SprType);
            int cellW = layout.SpriteSizeX + 1;
            int cellH = layout.SpriteSizeY + 1;
            int col = (int)(p.X / cellW);
            int row = (int)(p.Y / cellH);
            if (col < 0 || row < 0 || col >= layout.Cols || row >= layout.Rows) return -1;
            return row * layout.Cols + col;
        }

        private async void ImportManyIntoSheet_Click(object? sender, RoutedEventArgs e)
        {
            if (SheetWrap.Children.Count == 0)
            {
                SprStatusText.Text = "Create or edit a sheet first.";
                return;
            }

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import PNG sprites into sheet (fills from the first empty tile)",
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("PNG / BMP Image") { Patterns = ["*.png", "*.bmp"] }]
            });
            if (files.Count == 0) return;

            int startIdx = FirstEmptyTile();
            int placed = FillTilesFromPaths(startIdx, files.Select(f => f.Path.LocalPath));
            SprStatusText.Text = $"Imported {placed} sprite(s) starting at tile {startIdx}.";
        }

        // Returns the index of the first tile with no image, or 0 if all are empty.
        private int FirstEmptyTile()
        {
            for (int i = 0; i < SheetWrap.Children.Count; i++)
                if (!_tilePngs.ContainsKey(i)) return i;
            return 0;
        }

        private int FillTilesFromPaths(int startIdx, System.Collections.Generic.IEnumerable<string> paths)
        {
            int placed = 0;
            int idx = Math.Max(0, startIdx);
            foreach (var path in paths)
            {
                if (idx >= SheetWrap.Children.Count) break;
                var png = LoadTileImagePng(path);
                if (png == null) continue;
                SetTilePng(idx, png);
                idx++;
                placed++;
            }
            return placed;
        }

        // ── File drop onto the sheet grid ─────────────────────────────────────

        private void SheetWrap_DragOver(object? sender, DragEventArgs e)
        {
            e.DragEffects = e.Data.Contains(DataFormats.Files)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void SheetWrap_Drop(object? sender, DragEventArgs e)
        {
            if (!e.Data.Contains(DataFormats.Files)) return;
            var files = e.Data.GetFiles();
            if (files == null) return;
            var paths = files.Select(f => f.Path.LocalPath).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count == 0) return;

            // Always fill from the first empty tile — drop position is ignored per user request.
            int startIdx = FirstEmptyTile();
            int placed = FillTilesFromPaths(startIdx, paths);
            SprStatusText.Text = $"Dropped {placed} sprite(s) starting at tile {startIdx}.";
            e.Handled = true;
        }

        // ── Internal tile move: raw pointer events (no OS DragDrop) ───────────

        private int _moveSrcIdx = -1;

        private void SheetWrap_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var p = e.GetPosition(SheetWrap);
            int idx = HitTestTile(p);
            if (idx < 0) { _moveSrcIdx = -1; return; }

            // Right-click → context menu
            var props = e.GetCurrentPoint(SheetWrap).Properties;
            if (props.IsRightButtonPressed)
            {
                _moveSrcIdx = -1;
                ShowTileContextMenu(idx, e);
                e.Handled = true;
                return;
            }

            // Ctrl+Click: toggle selection on individual tile
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _moveSrcIdx = -1;
                if (_selectedTiles.Contains(idx))
                    _selectedTiles.Remove(idx);
                else
                    _selectedTiles.Add(idx);
                UpdateTileSelectionVisual(idx);
                _lastClickedTile = idx;
                SprStatusText.Text = $"Selected {_selectedTiles.Count} sprite(s).";
                e.Handled = true;
                return;
            }

            // Shift+Click: range selection from last clicked tile to this one
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _lastClickedTile >= 0)
            {
                _moveSrcIdx = -1;
                int from = Math.Min(_lastClickedTile, idx);
                int to = Math.Max(_lastClickedTile, idx);
                for (int i = from; i <= to; i++)
                {
                    _selectedTiles.Add(i);
                    UpdateTileSelectionVisual(i);
                }
                SprStatusText.Text = $"Selected {_selectedTiles.Count} sprite(s).";
                e.Handled = true;
                return;
            }

            // Plain click: clear selection, set anchor for future Shift+Click
            if (_selectedTiles.Count > 0)
                ClearSelection();
            _lastClickedTile = idx;

            if (SheetWrap.Children[idx] is not Border b || b.Child is not Image img || img.Source == null)
            {
                _moveSrcIdx = -1;
                return;
            }
            _moveSrcIdx = idx;
            e.Pointer.Capture(SheetWrap);
        }

        // ── Export sprite ID range as individual PNGs ────────────────────────

        private async void ExportSpriteRange_Click(object? sender, RoutedEventArgs e)
        {
            int from = (int)(A_ExportRangeFrom.Value ?? 0);
            int to   = (int)(A_ExportRangeTo.Value   ?? 0);
            if (from <= 0 || to <= 0 || to < from)
            {
                SprStatusText.Text = "Invalid range — set From ≤ To and both ≥ 1.";
                return;
            }

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"Export sprites {from}–{to} to folder",
                AllowMultiple = false,
            });
            if (folders.Count == 0) return;
            string dstDir = folders[0].Path.LocalPath;
            if (string.IsNullOrEmpty(dstDir) || !Directory.Exists(dstDir))
            {
                SprStatusText.Text = "Invalid destination folder.";
                return;
            }

            int exported = 0, missing = 0, failed = 0;
            for (int id = from; id <= to; id++)
            {
                try
                {
                    var stream = MainWindow.getSpriteStream(id);
                    if (stream == null) { missing++; continue; }

                    // getSpriteStream returns PNG-encoded bytes in a shared MemoryStream;
                    // copy the bytes out rather than disposing the stream.
                    stream.Seek(0, SeekOrigin.Begin);
                    byte[] png = stream.ToArray();
                    if (png.Length == 0) { missing++; continue; }

                    string outPath = Path.Combine(dstDir, $"sprite_{id}.png");
                    await File.WriteAllBytesAsync(outPath, png);
                    exported++;
                }
                catch (Exception ex)
                {
                    failed++;
                    MainWindow.Log($"[SprEditor] Export sprite {id} failed: {ex.Message}", "Error");
                }
            }

            SprStatusText.Text = $"Exported {exported} sprite(s) to {dstDir}. "
                               + $"Missing: {missing}. Failed: {failed}.";
        }

        // ── Delete sprite ID range ──────────────────────────────────────────

        private async void DeleteSpriteRange_Click(object? sender, RoutedEventArgs e)
        {
            int from = (int)(A_DeleteRangeFrom.Value ?? 0);
            int to   = (int)(A_DeleteRangeTo.Value   ?? 0);
            if (from <= 0 || to <= 0 || to < from)
            {
                SprStatusText.Text = "Invalid range — set From \u2264 To and both \u2265 1.";
                return;
            }

            var rangeIds = new System.Collections.Generic.List<uint>();
            for (int id = from; id <= to; id++) rangeIds.Add((uint)id);

            bool ok = await ConfirmHere(
                "Delete sprite range",
                $"Delete sprites {from}\u2013{to} ({to - from + 1} sprites)?\n\nThis clears them from their sheets and removes from SprLists. " +
                "Each changed sheet is kept once as <sheet>-orig." +
                DatEditor.SpriteUsageWarning(rangeIds));
            if (!ok) return;

            // Sheets whose id range overlaps [from, to] (one pass over the catalog).
            var affectedSheets = MainWindow.catalog
                .Where(c => c.Type == "sprite" && c.FirstSpriteid != int.MinValue
                            && c.FirstSpriteid <= to && c.LastSpriteid >= from)
                .ToList();

            // For each affected sheet: decompress, blank out the deleted tiles, re-save.
            // Ids of a sheet that fails stay in memory so memory and disk don't diverge.
            var keepIds = new System.Collections.Generic.HashSet<uint>();
            int failedSheets = 0;
            foreach (var cat in affectedSheets)
            {
                string sprPath = Path.Combine(MainWindow._assetsPath, cat.File);
                if (!File.Exists(sprPath)) continue;

                try
                {
                    using SKBitmap bmp = LZMA.DecompressFileLZMA(sprPath);
                    var layout = DatEditor.GetSpriteLayout(cat.SpriteType);
                    int tWidth = layout.SpriteSizeX;
                    int tHeight = layout.SpriteSizeY;
                    int cols = layout.Cols;

                    using (var canvas = new SKCanvas(bmp))
                    {
                        for (int id = Math.Max(from, cat.FirstSpriteid); id <= Math.Min(to, cat.LastSpriteid); id++)
                        {
                            int tileIdx = id - cat.FirstSpriteid;
                            int col = tileIdx % cols;
                            int row = tileIdx / cols;
                            canvas.Save();
                            canvas.ClipRect(SKRect.Create(col * tWidth, row * tHeight, tWidth, tHeight));
                            canvas.Clear(SKColors.Transparent);
                            canvas.Restore();
                        }
                    }

                    string outPath = sprPath;
                    LZMA.ExportLzmaFile(bmp, ref outPath);
                }
                catch (Exception ex)
                {
                    failedSheets++;
                    for (int id = Math.Max(from, cat.FirstSpriteid); id <= Math.Min(to, cat.LastSpriteid); id++)
                        keepIds.Add((uint)id);
                    MainWindow.Log($"[SprEditor] Delete range: rewriting sheet {cat.File} failed, its sprites were kept: {ex.Message}", "Error");
                }
            }

            var idsToRemove = rangeIds.Where(id => !keepIds.Contains(id)).ToList();
            foreach (var id in idsToRemove)
                MainWindow.SprLists.TryRemove((int)id, out _);
            int deleted = idsToRemove.Count;

            // Safely remove from AllSprList via DatEditor (disconnects binding first).
            _editor.SafeRemoveFromAllSprList(idsToRemove);

            // If the currently edited sheet is affected, reload the viewer.
            if (CurrentSheet != null && affectedSheets.Contains(CurrentSheet))
                CreateNewSheetImg(CurrentSheet.SpriteType, true);

            SprStatusText.Text = $"Deleted {deleted} sprite(s) in range {from}\u2013{to}. Affected {affectedSheets.Count} sheet(s)."
                               + (failedSheets > 0 ? $" {failedSheets} sheet(s) could not be rewritten — see log." : "");
        }

        // ── Batch PNG downscale (e.g. 64×64 → 32×32) ─────────────────────────

        private async void BatchDownscale_Click(object? sender, RoutedEventArgs e)
        {
            var srcFolders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select source folder with PNGs to downscale",
                AllowMultiple = false,
            });
            if (srcFolders.Count == 0) return;
            string srcDir = srcFolders[0].Path.LocalPath;
            if (string.IsNullOrEmpty(srcDir) || !Directory.Exists(srcDir))
            {
                SprStatusText.Text = "Invalid source folder.";
                return;
            }

            var dstFolders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select destination folder (will write downscaled PNGs here)",
                AllowMultiple = false,
            });
            if (dstFolders.Count == 0) return;
            string dstDir = dstFolders[0].Path.LocalPath;
            if (string.IsNullOrEmpty(dstDir) || !Directory.Exists(dstDir))
            {
                SprStatusText.Text = "Invalid destination folder.";
                return;
            }

            if (string.Equals(Path.GetFullPath(srcDir), Path.GetFullPath(dstDir), StringComparison.Ordinal))
            {
                bool ok = await ConfirmHere(
                    "Overwrite originals?",
                    "Source and destination are the same folder. Original PNGs will be overwritten. Continue?");
                if (!ok) { SprStatusText.Text = "Cancelled."; return; }
            }

            // Target size = current sheet's tile size if a sheet is loaded, else 32×32.
            int targetW = SheetWrap.Children.Count > 0 ? SpriteWidth : 32;
            int targetH = SheetWrap.Children.Count > 0 ? SpriteHeight : 32;

            var files = Directory.EnumerateFiles(srcDir, "*.png", SearchOption.TopDirectoryOnly)
                                 .Concat(Directory.EnumerateFiles(srcDir, "*.bmp", SearchOption.TopDirectoryOnly))
                                 .ToList();
            if (files.Count == 0)
            {
                SprStatusText.Text = "No PNG/BMP files found in source folder.";
                return;
            }

            int done = 0, skipped = 0, failed = 0;
            foreach (var path in files)
            {
                try
                {
                    using var src = SKBitmap.Decode(path);
                    if (src == null) { failed++; continue; }

                    if (src.Width == targetW && src.Height == targetH)
                    {
                        // Already the right size — just copy through unchanged.
                        string copyOut = Path.Combine(dstDir, Path.GetFileName(path));
                        File.Copy(path, copyOut, overwrite: true);
                        skipped++;
                        continue;
                    }

                    // Nearest-neighbor preserves pixel-art edges. SkiaSharp maps
                    // SKFilterQuality.None → nearest-neighbor sampling.
                    using var scaled = src.Resize(
                        new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul),
                        SKFilterQuality.None);
                    if (scaled == null) { failed++; continue; }

                    using var img = SKImage.FromBitmap(scaled);
                    using var data = img.Encode(SKEncodedImageFormat.Png, 100);

                    string outName = Path.GetFileNameWithoutExtension(path) + $"_{targetW}x{targetH}.png";
                    string outPath = Path.Combine(dstDir, outName);
                    await using (var fs = File.Create(outPath))
                        data.SaveTo(fs);
                    done++;
                }
                catch (Exception ex)
                {
                    failed++;
                    MainWindow.Log($"[SprEditor] Downscale {Path.GetFileName(path)} failed: {ex.Message}", "Error");
                }
            }

            SprStatusText.Text = $"Downscaled {done} file(s) to {targetW}×{targetH}. "
                               + $"Passed-through: {skipped}. Failed: {failed}. Output: {dstDir}";
        }

        // ── Single-sprite export ─────────────────────────────────────────────

        private async void ExportSingleSprite_Click(object? sender, RoutedEventArgs e)
        {
            int idx = focusedIndex;
            if (idx < 0 || idx >= SheetWrap.Children.Count || !_tilePngs.ContainsKey(idx))
            {
                SprStatusText.Text = "Hover a non-empty tile, then click Export.";
                return;
            }
            await ExportTileAsPngAsync(idx);
        }

        private async System.Threading.Tasks.Task ExportTileAsPngAsync(int idx)
        {
            if (!_tilePngs.TryGetValue(idx, out var png) || png == null || png.Length == 0)
            {
                SprStatusText.Text = $"Tile {idx} is empty.";
                return;
            }

            // Prefer the real Tibia sprite id when we know the sheet's base id.
            string suggested = CurrentSheet != null
                ? $"sprite_{CurrentSheet.FirstSpriteid + idx}.png"
                : $"sprite_tile{idx}.png";

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Sprite as PNG",
                DefaultExtension = "png",
                SuggestedFileName = suggested,
                FileTypeChoices = [new FilePickerFileType("PNG Image") { Patterns = ["*.png"] }]
            });
            if (file == null) return;

            try
            {
                await using var stream = await file.OpenWriteAsync();
                await stream.WriteAsync(png);
                SprStatusText.Text = $"Exported tile {idx} ({SpriteWidth}×{SpriteHeight}).";
            }
            catch (Exception ex)
            {
                SprStatusText.Text = $"Export failed: {ex.Message}";
            }
        }

        private void SheetWrap_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            try
            {
                if (_moveSrcIdx < 0) return;
                var p = e.GetPosition(SheetWrap);
                int tgt = HitTestTile(p);
                if (tgt < 0 || tgt == _moveSrcIdx) return;
                if (tgt >= SheetWrap.Children.Count) return;

                _tilePngs.TryGetValue(_moveSrcIdx, out var srcPng);
                _tilePngs.TryGetValue(tgt,         out var dstPng);
                SetTilePng(tgt,         srcPng);
                SetTilePng(_moveSrcIdx, dstPng);
                SprStatusText.Text = $"Moved tile {_moveSrcIdx} → {tgt}.";
            }
            finally
            {
                _moveSrcIdx = -1;
                e.Pointer.Capture(null);
            }
        }
    }
}
