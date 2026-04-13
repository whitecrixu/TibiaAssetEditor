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

            // Window-level Del handler so the key works whenever the editor has focus.
            KeyDown += SprEditor_KeyDown;
        }

        private async void SprEditor_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key != Avalonia.Input.Key.Delete) return;

            int idx = focusedIndex;
            if (idx < 0 || idx >= SheetWrap.Children.Count) return;
            if (!_tilePngs.ContainsKey(idx)) return;

            bool ok = await ErrorManager.ConfirmAsync(
                "Delete sprite",
                $"Clear sprite from tile {idx}?");
            if (!ok) return;

            SetTilePng(idx, null);
            SprStatusText.Text = $"Cleared tile {idx}. Click 💾 to persist.";
            e.Handled = true;
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
                border.BorderBrush = new SolidColorBrush(Colors.Blue);
                border.BorderThickness = new Avalonia.Thickness(2);

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
                bool showGrid = GridOverlayToggle.IsChecked == true;
                border.BorderBrush = showGrid ? new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)) : null;
                border.BorderThickness = showGrid ? new Avalonia.Thickness(1) : new Avalonia.Thickness(0);
            }
            TileInfoText.Text = "";
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
                    int firstId = 1;
                    if (MainWindow.catalog.Any(c => c.Type == "sprite"))
                        firstId = MainWindow.catalog.Where(c => c.Type == "sprite").Max(c => c.LastSpriteid) + 1;

                    int tileCount = layout.Cols * layout.Rows;
                    int lastId = firstId + tileCount - 1;

                    // Tibia assets use content-addressed names: sprites-<sha256>.bmp.lzma
                    // where the hash is over the raw pixel buffer. Match that convention so
                    // the written file visually belongs with the rest of the sheet files.
                    byte[] pixels = sheetBmp.Bytes;
                    using var sha = System.Security.Cryptography.SHA256.Create();
                    string hash = Convert.ToHexString(sha.ComputeHash(pixels)).ToLowerInvariant();
                    string sheetFilename = $"sprites-{hash}.bmp.lzma";

                    CurrentSheet = new Catalog
                    {
                        Type = "sprite",
                        File = sheetFilename,
                        SpriteType = SprType,
                        FirstSpriteid = firstId,
                        LastSpriteid = lastId,
                    };
                    MainWindow.catalog.Add(CurrentSheet);
                    SaveCatalog();

                    for (int i = firstId; i <= lastId; i++)
                    {
                        MainWindow.SprLists[i] = null!;
                        MainWindow.AllSprList.Add(new ShowList { Id = (uint)i });
                    }

                    AddToSheetsList(CurrentSheet);
                    SprStatusText.Text = $"New sheet registered: {sheetFilename} (IDs {firstId}–{lastId}).";
                }

                string outPath = Path.Combine(MainWindow._assetsPath, CurrentSheet.File);
                LZMA.ExportLzmaFile(sheetBmp, ref outPath);

                // Invalidate cached SprLists entries so they reload next time
                for (int i = CurrentSheet.FirstSpriteid; i <= CurrentSheet.LastSpriteid; i++)
                {
                    if (MainWindow.SprLists.ContainsKey(i))
                        MainWindow.SprLists[i] = null!;
                }

                SprStatusText.Text = $"Saved: {CurrentSheet.File} (IDs {CurrentSheet.FirstSpriteid}–{CurrentSheet.LastSpriteid}).";
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

                // Determine next available sprite ID
                int firstId = 1;
                if (MainWindow.catalog.Any(c => c.Type == "sprite"))
                    firstId = MainWindow.catalog.Where(c => c.Type == "sprite").Max(c => c.LastSpriteid) + 1;

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

                // Update SprLists and AllSprList
                for (int i = firstId; i <= lastId; i++)
                    MainWindow.SprLists[i] = null!;

                for (int i = MainWindow.AllSprList.Count; i <= lastId; i++)
                    MainWindow.AllSprList.Add(new ShowList { Id = (uint)i });

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

        private void SaveCatalog()
        {
            string catalogPath = Path.Combine(MainWindow._assetsPath, "catalog-content.json");
            // Tibia's catalog uses lowercase field names (type/file/spriteType/…).
            var settings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver
                {
                    NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy(),
                },
            };
            string json = JsonConvert.SerializeObject(MainWindow.catalog, Formatting.Indented, settings);
            File.WriteAllText(catalogPath, json);
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
            catch { }
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

            // Right-click on a filled tile → export it as a single PNG.
            var props = e.GetCurrentPoint(SheetWrap).Properties;
            if (props.IsRightButtonPressed)
            {
                _moveSrcIdx = -1;
                if (_tilePngs.ContainsKey(idx))
                {
                    _ = ExportTileAsPngAsync(idx);
                    e.Handled = true;
                }
                return;
            }

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
                catch
                {
                    failed++;
                }
            }

            SprStatusText.Text = $"Exported {exported} sprite(s) to {dstDir}. "
                               + $"Missing: {missing}. Failed: {failed}.";
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
                bool ok = await ErrorManager.ConfirmAsync(
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
                catch
                {
                    failed++;
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
