using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Tibia.Protobuf.Appearances;

namespace Assets_Editor
{
    // Imports objects and sprites into the legacy editor from another Tibia.dat/Tibia.spr pair
    // (read with that client's own flags) or from an .obd file.
    public partial class ImportManager : Window
    {
        private readonly LegacyDatEditor _editor;

        public ImportManager(LegacyDatEditor editor)
        {
            InitializeComponent();
            _editor = editor;
            Closed += ImportManager_Closed;

            var versions = MainWindow.datStructure.GetAllVersions().ToList();
            SourceStructure.ItemsSource = versions;
            int loaded = MainWindow.GetCurrentLoadedVersion();
            SourceStructure.SelectedItem = versions.FirstOrDefault(v => v.Structure == loaded) ?? versions.LastOrDefault();

            ObjListView.LayoutUpdated += (_, _) => HookScroll(ObjListView, ref _objScrollHooked, LoadVisibleObjectImages);
            SprListView.LayoutUpdated += (_, _) => HookScroll(SprListView, ref _sprScrollHooked, LoadVisibleSpriteImages);
        }

        private Appearances? ImportAppearances;
        private SpriteStorage? _sourceStorage;
        private List<ShowList> AllSprList = [];
        private ObservableCollection<ShowList> ThingsOutfit = [];
        private ObservableCollection<ShowList> ThingsItem = [];
        private ObservableCollection<ShowList> ThingsEffect = [];
        private ObservableCollection<ShowList> ThingsMissile = [];
        private string? _datPath;
        private string? _sprPath;
        private bool _loading;

        // decoded .obd: the appearance plus its sprites keyed by the ids stored in the file
        private Appearance? _obdAppearance;
        private ConcurrentDictionary<int, MemoryStream> _obdSprites = new();

        private void ImportManager_Closed(object? sender, EventArgs e)
        {
            Closed -= ImportManager_Closed;
            _sourceStorage?.CloseFile();
            _sourceStorage = null;
            AllSprList.Clear();
            ThingsOutfit.Clear();
            ThingsItem.Clear();
            ThingsEffect.Clear();
            ThingsMissile.Clear();
            _obdSprites.Clear();
            GC.Collect();
        }

        #region Source client

        private async void OpenClient_Click(object? sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Client Directory"
            });

            if (folders.Count == 0) return;

            string assetsPath = folders[0].Path.LocalPath;
            ClientPathText.Text = assetsPath;

            // the .otfi (OTClient) describes the files exactly; without one the flags stay as set
            string? datFile = null, sprFile = null;
            string? otfiPath = Directory.GetFiles(assetsPath, "*.otfi").FirstOrDefault();
            if (otfiPath != null)
            {
                try
                {
                    OTMLNode root = OTMLDocument.Parse(otfiPath).At("DatSpr");
                    SourceExtended.IsChecked = root.Get("extended")?.ValueAs<bool>() ?? false;
                    SprTransparent.IsChecked = root.Get("transparency")?.ValueAs<bool>() ?? false;
                    SourceFrameDurations.IsChecked = root.Get("frame-durations")?.ValueAs<bool>() ?? false;
                    SourceFrameGroups.IsChecked = root.Get("frame-groups")?.ValueAs<bool>() ?? false;
                    datFile = root.Get("metadata-file")?.ValueAs<string>();
                    sprFile = root.Get("sprites-file")?.ValueAs<string>();
                    string? assetsName = root.Get("assets-name")?.ValueAs<string>();
                    if (assetsName != null)
                    {
                        datFile ??= assetsName + ".dat";
                        sprFile ??= assetsName + ".spr";
                    }
                }
                catch (Exception ex)
                {
                    ErrorManager.ShowWarning($"Unable to read {Path.GetFileName(otfiPath)}:\n\n{ex.Message}");
                }
            }

            _datPath = FindClientFile(assetsPath, datFile, "Tibia.dat", "*.dat");
            _sprPath = FindClientFile(assetsPath, sprFile, "Tibia.spr", "*.spr");
            if (_datPath == null || _sprPath == null)
            {
                ErrorManager.ShowError("No .dat / .spr found in the selected directory.");
                ReloadClientButton.IsEnabled = false;
                return;
            }

            ReloadClientButton.IsEnabled = true;
            await LoadSourceClient();
        }

        private static string? FindClientFile(string directory, string? preferred, string standard, string pattern)
        {
            foreach (string? name in new[] { preferred, standard })
            {
                if (name == null) continue;
                string path = Path.Combine(directory, name);
                if (File.Exists(path)) return path;
            }
            return Directory.GetFiles(directory, pattern).FirstOrDefault();
        }

        private async void ReloadClient_Click(object? sender, RoutedEventArgs e)
        {
            await LoadSourceClient();
        }

        // Reads the source dat/spr with the flags chosen above. When they do not match the file every
        // structure / flag combination is tried and the one that reads the whole file is shown.
        private async Task LoadSourceClient()
        {
            if (_loading || _datPath == null || _sprPath == null) return;
            if (SourceStructure.SelectedItem is not VersionInfo version)
            {
                ErrorManager.ShowError("Select the dat structure of the source client.");
                return;
            }

            _loading = true;
            ReloadClientButton.IsEnabled = false;
            string datPath = _datPath, sprPath = _sprPath;
            bool transparency = SprTransparent.IsChecked ?? false;
            var requested = new PresetSettings
            {
                Version = version.Structure,
                Extended = SourceExtended.IsChecked ?? false,
                Transparent = transparency,
                FrameDurations = SourceFrameDurations.IsChecked ?? false,
                FrameGroups = SourceFrameGroups.IsChecked ?? false,
            };
            var progressReporter = new Progress<int>(value => LoadProgress.Value = value);

            try
            {
                var (appearances, preset) = await Task.Run(() => ReadSourceDat(datPath, requested));
                if (appearances == null || preset == null)
                {
                    ErrorManager.ShowError($"{Path.GetFileName(datPath)} could not be read with any structure / flag combination.");
                    return;
                }

                if (preset.Version != requested.Version || preset.Extended != requested.Extended
                    || preset.FrameDurations != requested.FrameDurations || preset.FrameGroups != requested.FrameGroups)
                {
                    SourceStructure.SelectedItem = MainWindow.datStructure.GetAllVersions().FirstOrDefault(v => v.Structure == preset.Version);
                    SourceExtended.IsChecked = preset.Extended;
                    SourceFrameDurations.IsChecked = preset.FrameDurations;
                    SourceFrameGroups.IsChecked = preset.FrameGroups;
                    ErrorManager.ShowInfo("The chosen flags did not match the source dat; detected and applied:\n" +
                                          $"structure {preset.Version}, extended {preset.Extended}, frame durations {preset.FrameDurations}, frame groups {preset.FrameGroups}.");
                }

                SpriteStorage storage = new(sprPath, transparency, progressReporter);
                await Task.Run(() => storage.LoadSprites(preset.Extended));

                _sourceStorage?.CloseFile();
                _sourceStorage = storage;
                ImportAppearances = appearances;
                FillLists();
                LoadProgress.Value = 100;
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Unable to load the source client:\n\n{ex.Message}");
            }
            finally
            {
                _loading = false;
                ReloadClientButton.IsEnabled = true;
            }
        }

        private static (Appearances?, PresetSettings?) ReadSourceDat(string datPath, PresetSettings requested)
        {
            Appearances? TryRead(PresetSettings preset)
            {
                try
                {
                    LegacyAppearance dat = new();
                    dat.ReadLegacyDat(datPath, preset.Version, preset);
                    return dat.Appearances;
                }
                catch
                {
                    return null;
                }
            }

            Appearances? result = TryRead(requested);
            if (result != null)
                return (result, requested);

            foreach (VersionInfo version in MainWindow.datStructure.GetAllVersions().Reverse())
            {
                for (int flags = 0; flags < 8; flags++)
                {
                    var preset = new PresetSettings
                    {
                        Version = version.Structure,
                        Transparent = requested.Transparent,
                        Extended = (flags & 1) != 0,
                        FrameDurations = (flags & 2) != 0,
                        FrameGroups = (flags & 4) != 0,
                    };
                    result = TryRead(preset);
                    if (result != null)
                        return (result, preset);
                }
            }
            return (null, null);
        }

        private void FillLists()
        {
            ThingsOutfit.Clear();
            ThingsItem.Clear();
            ThingsEffect.Clear();
            ThingsMissile.Clear();
            if (ImportAppearances != null)
            {
                foreach (var outfit in ImportAppearances.Outfit)
                    ThingsOutfit.Add(new ShowList() { Id = outfit.Id });
                foreach (var item in ImportAppearances.Object)
                    ThingsItem.Add(new ShowList() { Id = item.Id });
                foreach (var effect in ImportAppearances.Effect)
                    ThingsEffect.Add(new ShowList() { Id = effect.Id });
                foreach (var missile in ImportAppearances.Missile)
                    ThingsMissile.Add(new ShowList() { Id = missile.Id });
            }

            AllSprList = [];
            uint count = _sourceStorage?.SpriteCount ?? 0;
            for (uint i = 0; i <= count; i++)
                AllSprList.Add(new ShowList() { Id = i });

            _lastSprStart = _lastSprEnd = -1;
            SprListView.ItemsSource = null;
            SprListView.ItemsSource = AllSprList;
            UpdateShowList(ObjectMenu.SelectedIndex);
            Dispatcher.UIThread.Post(LoadVisibleSpriteImages, DispatcherPriority.Background);
        }

        private void UpdateShowList(int selection)
        {
            if (ObjListView == null) return;

            // the old visible range belongs to the old list
            if (ObjListView.ItemsSource is IList<ShowList> previous)
                for (int i = Math.Max(0, _lastObjStart); i < _lastObjEnd && i < previous.Count; i++)
                    previous[i].Image = null;
            _lastObjStart = _lastObjEnd = -1;

            ObjListView.ItemsSource = GetShowList(selection);
            if (ObjListView.ItemCount > 0)
            {
                ObjListView.SelectedIndex = 0;
                ObjListView.ScrollIntoView(0);
            }
            Dispatcher.UIThread.Post(LoadVisibleObjectImages, DispatcherPriority.Background);
        }

        private ObservableCollection<ShowList> GetShowList(int category) => category switch
        {
            0 => ThingsOutfit,
            1 => ThingsItem,
            2 => ThingsEffect,
            3 => ThingsMissile,
            _ => ThingsOutfit
        };

        private Google.Protobuf.Collections.RepeatedField<Appearance>? GetSourceCollection(int category)
        {
            if (ImportAppearances == null) return null;
            return category switch
            {
                0 => ImportAppearances.Outfit,
                1 => ImportAppearances.Object,
                2 => ImportAppearances.Effect,
                3 => ImportAppearances.Missile,
                _ => null
            };
        }

        // ids are positional: row i of a category is collection[i]
        private Appearance? GetSourceAppearance(int category, int row, uint id)
        {
            var collection = GetSourceCollection(category);
            if (collection == null) return null;
            if (row >= 0 && row < collection.Count && collection[row].Id == id)
                return collection[row];
            return collection.FirstOrDefault(a => a.Id == id);
        }

        private void ObjectMenuChanged(object? sender, SelectionChangedEventArgs e)
        {
            UpdateShowList(ObjectMenu.SelectedIndex);
        }

        private bool _syncingIndex;

        private void ObjListView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_syncingIndex || ObjListView.SelectedItem is not ShowList showList) return;
            _syncingIndex = true;
            ObjListViewSelectedIndex.Value = (int)showList.Id;
            _syncingIndex = false;
        }

        private void ObjListViewSelectedIndex_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
        {
            if (_syncingIndex || ObjListView.ItemsSource is not IList<ShowList> list) return;
            uint id = (uint)(ObjListViewSelectedIndex.Value ?? 0);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Id != id) continue;
                _syncingIndex = true;
                ObjListView.SelectedIndex = i;
                ObjListView.ScrollIntoView(i);
                _syncingIndex = false;
                break;
            }
        }

        #endregion

        #region Import

        private bool CanImport()
        {
            if (MainWindow.appearances == null)
            {
                ErrorManager.ShowWarning("No dat loaded in the editor.");
                return false;
            }
            if (_editor.IsCompiling)
            {
                ErrorManager.ShowWarning("The editor is compiling; try again when it has finished.");
                return false;
            }
            return true;
        }

        // Copies an object with its sprites into the editor: every referenced source sprite becomes a new
        // sprite in the loaded spr (each source id once; 0 and empty sprites stay 0) and the object gets the
        // next free id of its category.
        private void ObjectImport_Click(object? sender, RoutedEventArgs e)
        {
            if (!CanImport() || _sourceStorage == null || ObjListView.SelectedItem is not ShowList selected) return;

            int category = ObjectMenu.SelectedIndex;
            Appearance? source = GetSourceAppearance(category, ObjListView.SelectedIndex, selected.Id);
            if (source == null) return;

            try
            {
                Appearance appearance = source.Clone();
                SpriteStorage sourceStorage = _sourceStorage;
                var remap = new Dictionary<uint, uint> { [0] = 0 };
                int added = RemapSprites(appearance, category, id =>
                {
                    byte[]? pixels = id <= sourceStorage.SpriteCount ? sourceStorage.GetCompressedPixels(id) : null;
                    return pixels != null ? SpriteStorage.ConvertCompressed(pixels, sourceStorage.Transparency, MainWindow.MainSprStorage.Transparency) : null;
                }, remap);

                uint newId = _editor.AddImportedAppearance(appearance, category);
                _editor.RefreshSpriteList(added > 0 ? MainWindow.MainSprStorage.SpriteCount : null);
                ErrorManager.ShowInfo($"Imported {CategoryName(category)} {selected.Id} as id {newId} ({added} new sprites).");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Import failed: {ex.Message}");
            }
        }

        // Replaces every sprite id of the appearance by a new sprite of the loaded spr. getPixels returns
        // the compressed pixels (in the loaded spr's layout) of a source id, or null for an empty sprite.
        // Returns the number of sprites added.
        private static int RemapSprites(Appearance appearance, int category, Func<uint, byte[]?> getPixels, Dictionary<uint, uint> remap)
        {
            // only outfits of clients with frame groups keep more than one group
            bool frameGroups = MainWindow.GetCurrentPreset()?.FrameGroups ?? false;
            if ((category != 0 || !frameGroups) && appearance.FrameGroup.Count > 1)
            {
                while (appearance.FrameGroup.Count > 1)
                    appearance.FrameGroup.RemoveAt(appearance.FrameGroup.Count - 1);
            }

            int added = 0;
            foreach (FrameGroup group in appearance.FrameGroup)
            {
                var ids = group.SpriteInfo.SpriteId;
                for (int i = 0; i < ids.Count; i++)
                {
                    uint sourceId = ids[i];
                    if (!remap.TryGetValue(sourceId, out uint newId))
                    {
                        byte[]? pixels = getPixels(sourceId);
                        newId = pixels != null && pixels.Length > 0 ? LegacyDatEditor.AddSprite(pixels) : 0;
                        if (newId != 0) added++;
                        remap[sourceId] = newId;
                    }
                    ids[i] = newId;
                }
            }
            return added;
        }

        private static string CategoryName(int category) => category switch
        {
            0 => "outfit",
            1 => "item",
            2 => "effect",
            3 => "missile",
            _ => "object"
        };

        // appends the selected source sprites to the loaded spr
        private void ImportSpr_Click(object? sender, RoutedEventArgs e)
        {
            if (!CanImport() || _sourceStorage == null) return;
            var selected = SprListView.SelectedItems?.OfType<ShowList>().Where(s => s.Id != 0).OrderBy(s => s.Id).ToList() ?? [];
            if (selected.Count == 0)
            {
                ErrorManager.ShowInfo("Select one or more sprites (not sprite 0) in the sprite list first.");
                return;
            }

            try
            {
                uint first = 0, last = 0;
                foreach (ShowList sprite in selected)
                {
                    byte[]? pixels = _sourceStorage.GetCompressedPixels(sprite.Id);
                    if (pixels != null)
                        pixels = SpriteStorage.ConvertCompressed(pixels, _sourceStorage.Transparency, MainWindow.MainSprStorage.Transparency);
                    last = LegacyDatEditor.AddSprite(pixels);
                    if (first == 0) first = last;
                }
                _editor.RefreshSpriteList(last);
                ErrorManager.ShowInfo(selected.Count == 1
                    ? $"Imported sprite {selected[0].Id} as sprite {first}."
                    : $"Imported {selected.Count} sprites as {first}-{last}.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Sprite import failed: {ex.Message}");
            }
        }

        #endregion

        #region OBD

        private async void ImportObject_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select OBD File",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Object Builder files") { Patterns = ["*.obd"] }]
            });
            if (files.Count == 0) return;

            string path = files[0].Path.LocalPath;
            if (ObdDecoder.GetDatStructure() == null)
            {
                ErrorManager.ShowError("Appearances.xml defines no OBD structure; .obd files cannot be read.");
                return;
            }

            var sprites = new ConcurrentDictionary<int, MemoryStream>();
            Appearance? appearance = await Task.Run(() => ObdDecoder.Load(path, ref sprites));
            if (appearance == null || appearance.FrameGroup.Count == 0)
            {
                _obdAppearance = null;
                OBDImportButton.IsEnabled = false;
                OBDImage.Source = null;
                OBDInfo.Text = "Not a valid .obd file.";
                ErrorManager.ShowError($"{Path.GetFileName(path)} is not a valid .obd file (versions 2 and 3 are supported).");
                return;
            }

            _obdAppearance = appearance;
            _obdSprites = sprites;
            OBDImportButton.IsEnabled = true;
            int category = ObdCategory(appearance);
            var info = appearance.FrameGroup[0].SpriteInfo;
            OBDInfo.Text = $"{Path.GetFileName(path)}: {CategoryName(category)}, {info.PatternWidth}x{info.PatternHeight}, " +
                           $"{appearance.FrameGroup.Count} group(s), {sprites.Count} sprites";
            ShowObdPreview();

            if (ObjImportSlider.Value >= 1)
                ImportObd();
        }

        private void OBDImport_Click(object? sender, RoutedEventArgs e) => ImportObd();

        private static int ObdCategory(Appearance appearance) => appearance.AppearanceType switch
        {
            APPEARANCE_TYPE.AppearanceOutfit => 0,
            APPEARANCE_TYPE.AppearanceEffect => 2,
            APPEARANCE_TYPE.AppearanceMissile => 3,
            _ => 1
        };

        private SKBitmap? DecodeObdSprite(uint id)
        {
            // decode from bytes: SKBitmap.Decode(Stream) disposes the stream
            return _obdSprites.TryGetValue((int)id, out MemoryStream? png) && png != null ? SKBitmap.Decode(png.ToArray()) : null;
        }

        private void ShowObdPreview()
        {
            if (_obdAppearance == null) return;
            bool outfit = _obdAppearance.AppearanceType == APPEARANCE_TYPE.AppearanceOutfit;
            uint patternX = _obdAppearance.FrameGroup[0].SpriteInfo.PatternX;
            using SKBitmap? bitmap = LegacyAppearance.RenderObject(_obdAppearance, DecodeObdSprite, 0,
                outfit && patternX > 2 ? 2 : 0, 0, 0, 0, outfit ? 0 : -1);
            OBDImage.Source = Utils.MemoryStreamToBitmap(LegacyAppearance.EncodePng(bitmap));
        }

        // .obd sprites are 32x32 ARGB tiles keyed by the ids of the client they came from; each one
        // becomes a new sprite (fully transparent tiles become sprite 0)
        private void ImportObd()
        {
            if (_obdAppearance == null || !CanImport()) return;
            try
            {
                Appearance appearance = _obdAppearance.Clone();
                int category = ObdCategory(appearance);
                bool transparent = MainWindow.MainSprStorage.Transparency;
                var remap = new Dictionary<uint, uint>();
                int added = RemapSprites(appearance, category, id =>
                {
                    using SKBitmap? tile = DecodeObdSprite(id);
                    if (tile == null || tile.Width != Sprite.DefaultSize || tile.Height != Sprite.DefaultSize)
                        return null;
                    return LegacyAppearance.SplitIntoTiles(tile, transparent, out _, out _)[0];
                }, remap);

                uint newId = _editor.AddImportedAppearance(appearance, category);
                _editor.RefreshSpriteList(added > 0 ? MainWindow.MainSprStorage.SpriteCount : null);
                ErrorManager.ShowInfo($"Imported .obd {CategoryName(category)} as id {newId} ({added} new sprites).");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"OBD import failed: {ex.Message}");
            }
        }

        #endregion

        #region Scroll-based image loading

        // 64px image + ~12px ListBoxItem padding; must match the rendered row height
        private const double ItemHeight = 76;
        private bool _objScrollHooked;
        private bool _sprScrollHooked;
        private int _lastObjStart = -1;
        private int _lastObjEnd = -1;
        private int _lastSprStart = -1;
        private int _lastSprEnd = -1;

        private static void HookScroll(ListBox list, ref bool hooked, Action load)
        {
            if (hooked) return;
            ScrollViewer? sv = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (sv == null) return;
            sv.ScrollChanged += (_, _) => load();
            hooked = true;
        }

        private static (int start, int end) VisibleRange(ListBox list, int itemCount)
        {
            ScrollViewer? sv = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (sv == null || itemCount == 0) return (0, 0);
            int offset = Math.Max(0, (int)(sv.Offset.Y / ItemHeight));
            int visible = (int)(sv.Viewport.Height / ItemHeight) + 2;
            return (Math.Min(offset, itemCount), Math.Min(offset + visible, itemCount));
        }

        private static void LoadVisible(IList<ShowList> items, (int start, int end) range, ref int lastStart, ref int lastEnd, Func<int, ShowList, Bitmap?> load)
        {
            if (lastStart >= 0)
                for (int i = lastStart; i < lastEnd && i < items.Count; i++)
                    if (i < range.start || i >= range.end)
                        items[i].Image = null;

            for (int i = range.start; i < range.end; i++)
            {
                if (items[i].Image != null) continue;
                try { items[i].Image = load(i, items[i]); }
                catch { }
            }
            lastStart = range.start;
            lastEnd = range.end;
        }

        private void LoadVisibleObjectImages()
        {
            if (_sourceStorage == null || ObjListView.ItemsSource is not IList<ShowList> items) return;
            SpriteStorage storage = _sourceStorage;
            int category = ObjectMenu.SelectedIndex;
            LoadVisible(items, VisibleRange(ObjListView, items.Count), ref _lastObjStart, ref _lastObjEnd, (i, row) =>
            {
                Appearance? appearance = GetSourceAppearance(category, i, row.Id);
                return appearance != null ? Utils.MemoryStreamToBitmap(LegacyAppearance.GetObjectImage(appearance, storage)) : null;
            });
        }

        private void LoadVisibleSpriteImages()
        {
            if (_sourceStorage == null) return;
            SpriteStorage storage = _sourceStorage;
            LoadVisible(AllSprList, VisibleRange(SprListView, Math.Min(SprListView.ItemCount, AllSprList.Count)), ref _lastSprStart, ref _lastSprEnd,
                (_, row) => Utils.MemoryStreamToBitmap(storage.getSpriteStream(row.Id)));
        }

        #endregion
    }
}
