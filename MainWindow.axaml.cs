using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using SkiaSharp;
using Tibia.Protobuf.Appearances;
using static Assets_Editor.LogView;

namespace Assets_Editor;

public class PresetSettings : INotifyPropertyChanged
{
    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set
        {
            if (_name != value) { _name = value; OnPropertyChanged(nameof(Name)); }
        }
    }

    public int Version { get; set; }
    public bool Extended { get; set; }
    public bool Transparent { get; set; }
    public bool FrameDurations { get; set; }
    public bool FrameGroups { get; set; }
    public string? ServerPath { get; set; }
    public string? ClientPath { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Reset()
    {
        Name = "Default";
        Version = 0;
        Extended = false;
        Transparent = false;
        FrameDurations = false;
        FrameGroups = false;
        ServerPath = null;
        ClientPath = null;
    }

    public override string ToString() => Name;
}

public partial class MainWindow : Window
{
    public static string _assetsPath = "";
    public static string _datPath = "";
    public static string _sprPath = "";
    public static string _imgExportPath = "";
    public static ushort ObjectCount { get; set; }
    public static ushort OutfitCount { get; set; }
    public static ushort EffectCount { get; set; }
    public static ushort MissileCount { get; set; }

    public static Dictionary<uint, Sprite> sprites = [];
    public static SpriteStorage MainSprStorage = new();
    private static PresetSettings? currentPreset;
    public static Settings SettingsList = new();
    public static OTBReader ServerOTB = new();
    public static LogView logView = new();
    public static readonly DatStructure datStructure = new();
    private static readonly string settingsFilePath = AppPaths.UserFile("Settings.json");
    private PresetSettings? _editingPreset;

    public MainWindow()
    {
        InitializeComponent();
        LoadEditorSettings();
        LoadDatChoices();
        CurrentPresetDropdown.SelectedIndex = -1;
        CurrentPresetDropdown.SelectionChanged += CurrentPresetDropdown_SelectionChanged;

        logView.Closing += (sender, e) =>
        {
            e.Cancel = true;
            logView.Hide();
        };

        CurrentPresetDropdown.SelectedIndex = Math.Clamp(SettingsList.LastPresetChoice, 0, Math.Max(CurrentPresetDropdown.ItemCount - 1, 0));
    }

    private void LoadDatChoices()
    {
        var items = new List<VersionInfo>();
        foreach (VersionInfo version in datStructure.GetAllVersions())
            items.Add(version);
        DatStructureDropdown.ItemsSource = items;
    }

    public enum UiDensity { Comfortable = 0, Cozy = 1, Compact = 2 }

    public class Settings
    {
        public bool DarkMode { get; set; }
        public int LastPresetChoice { get; set; }
        public UiDensity Density { get; set; } = UiDensity.Cozy;
        public ObservableCollection<PresetSettings> Presets { get; set; } = [];

        // Pass 1 additions
        public List<string> RecentAppearanceDats  { get; set; } = [];
        public List<string> RecentItemsXml        { get; set; } = [];
        public List<string> RecentProficiencies   { get; set; } = [];
        public List<string> RecentMonsterFolders  { get; set; } = [];
        public int AutoSaveIntervalMinutes { get; set; } = 5;
        public bool AutoSaveEnabled { get; set; } = true;
        public int DefaultPreviewFps { get; set; } = 5;
        // Bookmarks: "outfit:123", "item:456", ...
        public List<string> Bookmarks { get; set; } = [];
    }

    public static Settings GetSettings() => SettingsList;
    public static void PersistSettings()
    {
        try
        {
            var json = JsonConvert.SerializeObject(SettingsList, Formatting.Indented);
            AppPaths.WriteAllTextAtomic(settingsFilePath, json);
        }
        catch (Exception e)
        {
            Log($"Unable to save settings to {settingsFilePath}: {e.Message}", "Error");
        }
    }

    // Track recent files — capped at 10, most recent first, no duplicates
    public static void AddRecentAppearanceDat(string path)
    {
        SettingsList.RecentAppearanceDats.RemoveAll(p => p == path);
        SettingsList.RecentAppearanceDats.Insert(0, path);
        while (SettingsList.RecentAppearanceDats.Count > 10)
            SettingsList.RecentAppearanceDats.RemoveAt(SettingsList.RecentAppearanceDats.Count - 1);
        PersistSettings();
    }

    public static void AddRecentItemsXml(string path)
    {
        SettingsList.RecentItemsXml.RemoveAll(p => p == path);
        SettingsList.RecentItemsXml.Insert(0, path);
        while (SettingsList.RecentItemsXml.Count > 10)
            SettingsList.RecentItemsXml.RemoveAt(SettingsList.RecentItemsXml.Count - 1);
        PersistSettings();
    }

    public static void AddRecentProficiencies(string path)
    {
        SettingsList.RecentProficiencies.RemoveAll(p => p == path);
        SettingsList.RecentProficiencies.Insert(0, path);
        while (SettingsList.RecentProficiencies.Count > 10)
            SettingsList.RecentProficiencies.RemoveAt(SettingsList.RecentProficiencies.Count - 1);
        PersistSettings();
    }

    public static void AddRecentMonsterFolder(string path)
    {
        SettingsList.RecentMonsterFolders.RemoveAll(p => p == path);
        SettingsList.RecentMonsterFolders.Insert(0, path);
        while (SettingsList.RecentMonsterFolders.Count > 10)
            SettingsList.RecentMonsterFolders.RemoveAt(SettingsList.RecentMonsterFolders.Count - 1);
        PersistSettings();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    public class Catalog
    {
        public Catalog()
        {
            SpriteType = int.MinValue;
            FirstSpriteid = int.MinValue;
            LastSpriteid = int.MinValue;
            Area = int.MinValue;
            Version = int.MinValue;
        }
        public string Type { get; set; } = "";
        public string File { get; set; } = "";
        [DefaultValue(int.MinValue)] public int SpriteType { get; set; }
        [DefaultValue(int.MinValue)] public int FirstSpriteid { get; set; }
        [DefaultValue(int.MinValue)] public int LastSpriteid { get; set; }
        [DefaultValue(int.MinValue)] public int Area { get; set; }
        [DefaultValue(int.MinValue)] public int Version { get; set; }
    }

    public static List<Catalog> catalog = [];
    public static Appearances? appearances;
    public static List<ShowList> AllSprList = [];
    public static ConcurrentDictionary<int, MemoryStream> SprLists = [];
    public static HashSet<int> DirtySpriteIds = [];
    public static bool LegacyClient = false;
    public static uint DatSignature { get; set; }
    public static uint SprSignature { get; set; }
    private static bool Loaded = false;

    private static readonly PresetSettings defaultPreset = new()
    {
        Name = "Default",
        Version = 1,
        ServerPath = "",
        ClientPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tibia/packages/Tibia/assets")
    };

    public static PresetSettings? GetCurrentPreset() => currentPreset;

    private static string CastToAppData(string? path, string localAppData)
    {
        if (path == null) return "";
        if (path.StartsWith("~/"))
            return Path.Combine(localAppData, path[2..]);
        // handle Windows-style paths on Linux
        if (path.StartsWith(@"~\"))
            return Path.Combine(localAppData, path[2..]);
        return path;
    }

    private void CreateSettingsChoices()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var preset in SettingsList.Presets)
        {
            preset.ServerPath = CastToAppData(preset.ServerPath, localAppData);
            preset.ClientPath = CastToAppData(preset.ClientPath, localAppData);
        }
        CurrentPresetDropdown.ItemsSource = SettingsList.Presets;
    }

    private static void LoadDefaultSettings()
    {
        Settings defaultSettings = new()
        {
            DarkMode = false,
            Presets = [defaultPreset]
        };
        SettingsList = defaultSettings;
    }

    private void LoadEditorSettings()
    {
        if (File.Exists(settingsFilePath))
        {
            try
            {
                string json = File.ReadAllText(settingsFilePath);
                Settings? jsonSettings = JsonConvert.DeserializeObject<Settings>(json);
                if (jsonSettings == null) { LoadDefaultSettings(); return; }
                if (jsonSettings.Presets.Count == 0) jsonSettings.Presets = [defaultPreset];
                SettingsList = jsonSettings;
            }
            catch (Exception e)
            {
                LoadDefaultSettings();
                ErrorManager.ShowWarning($"Unable to read settings! Using defaults.\n\n{e.Message}");
            }
        }
        else
        {
            LoadDefaultSettings();
        }

        DarkModeToggle.IsChecked = SettingsList.DarkMode;
        DensityDropdown.SelectedIndex = (int)SettingsList.Density;
        App.SetDensity(SettingsList.Density);
        CreateSettingsChoices();
    }

    private void LoadCatalogJson()
    {
        using StreamReader r = new(_assetsPath + "catalog-content.json");
        string json = r.ReadToEnd();
        var settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore
        };
        catalog = JsonConvert.DeserializeObject<List<Catalog>>(json, settings) ?? [];
    }

    private void UpdateAppearancesCount()
    {
        AppearancesCount.Text =
            $"Items:\t{ObjectCount}\n" +
            $"Outfits:\t{OutfitCount}\n" +
            $"Effects:\t{EffectCount}\n" +
            $"Missiles:\t{MissileCount}\n" +
            $"Sprites:\t{SprLists.Count}";
    }

    private void LoadAppearances()
    {
        _datPath = String.Format("{0}{1}", _assetsPath, catalog[0].File);
        if (File.Exists(_datPath) == false) return;

        using var appStream = new FileStream(_datPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        appearances = Appearances.Parser.ParseFrom(appStream);
        ObjectCount = Utils.GetLastIdOrZero(appearances.Object, o => o.Id);
        OutfitCount = Utils.GetLastIdOrZero(appearances.Outfit, o => o.Id);
        EffectCount = Utils.GetLastIdOrZero(appearances.Effect, e => e.Id);
        MissileCount = Utils.GetLastIdOrZero(appearances.Missile, m => m.Id);
        UpdateAppearancesCount();
    }

    // dat signature -> Appearances.xml structure
    private static readonly Dictionary<uint, int> KnownDatStructures = new()
    {
        [0x4C2C7993] = 5, // 8.60 (NewEra / WolfOTS client, data/things/860)
    };

    private void LoadLegacyDat()
    {
        using FileStream stream = File.OpenRead(_datPath);
        using BinaryReader r = new(stream);
        DatInfo info = DatStructure.ReadAppearanceInfo(r);
        DatSignature = info.Signature;

        // known signatures pick their dat structure, so the first load attempt uses the right flag table
        if (currentPreset != null && KnownDatStructures.TryGetValue(info.Signature, out int structure))
        {
            currentPreset.Version = structure;
            RefreshUIToggles();
        }

        ObjectCount = info.ObjectCount;
        OutfitCount = info.OutfitCount;
        EffectCount = info.EffectCount;
        MissileCount = info.MissileCount;
        UpdateAppearancesCount();
    }

    private void ReadSprSignature()
    {
        using FileStream stream = File.OpenRead(_sprPath);
        using BinaryReader r = new(stream);
        SprSignature = r.ReadUInt32();
    }

    private void LoadLegacySpr()
    {
        bool transparency = false;
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            transparency = DatTransparentToggle.IsChecked ?? false;
        }).Wait();

        var progressReporter = new Progress<int>(value =>
        {
            Dispatcher.UIThread.InvokeAsync(() => LoadProgress.Value = value);
        });

        MainSprStorage = new SpriteStorage(_sprPath, transparency, progressReporter);
        MainSprStorage.LoadSprites();
        SprSignature = MainSprStorage.Signature;
        SprLists = MainSprStorage.SprLists;
        sprites = MainSprStorage.Sprites;
        for (uint i = 0; i < sprites.Count; i++)
            AllSprList.Add(new ShowList() { Id = i });
    }

    private async void SelectAssetsFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Assets Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            _assetsPath = folders[0].Path.LocalPath;
            if (!_assetsPath.EndsWith(Path.DirectorySeparatorChar))
                _assetsPath += Path.DirectorySeparatorChar;
            AssetsPath.Text = _assetsPath;

            if (currentPreset != null)
                currentPreset.ClientPath = _assetsPath;
            SaveEditorSettings();
        }

        InternalSelectAssets();
    }

    private void NewPreset_Click(object? sender, RoutedEventArgs e)
    {
        PresetDialogTitle.Text = "New Preset";
        PresetNameTextBox.Text = $"Preset {SettingsList.Presets.Count + 1}";
        _editingPreset = null;
        PresetDialogOverlay.IsVisible = true;
    }

    private void RenamePreset_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentPresetDropdown.SelectedItem is not PresetSettings preset)
        {
            ShowStatus("No preset selected!");
            return;
        }
        PresetDialogTitle.Text = "Rename Preset";
        PresetNameTextBox.Text = preset.Name;
        _editingPreset = preset;
        PresetDialogOverlay.IsVisible = true;
    }

    private void DeletePreset_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentPresetDropdown.SelectedItem == null)
        {
            ShowStatus("No preset available!");
            return;
        }
        if (CurrentPresetDropdown.SelectedItem is not PresetSettings preset)
        {
            ShowStatus("Failed to remove preset!");
            return;
        }
        if (CurrentPresetDropdown.ItemCount == 1)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            preset.Reset();
            preset.ClientPath = Path.Combine(localAppData, "Tibia/packages/Tibia/assets/");
            preset.ServerPath = "";
            SetCurrentPreset();
            SaveEditorSettings();
            return;
        }

        int index = CurrentPresetDropdown.SelectedIndex;
        if (index == SettingsList.Presets.Count - 1) index--;
        else index++;

        CurrentPresetDropdown.SelectedIndex = index;
        SettingsList.Presets.Remove(preset);
        SaveEditorSettings();
    }

    private void ModifyPresetConfirm_Click(object? sender, RoutedEventArgs e)
    {
        string newName = PresetNameTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(newName))
        {
            ShowStatus("Preset name cannot be empty!");
            return;
        }

        if (_editingPreset != null)
        {
            _editingPreset.Name = newName;
        }
        else
        {
            PresetSettings newPreset = new()
            {
                Name = newName,
                ClientPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tibia/packages/Tibia/assets/"),
                ServerPath = ""
            };
            SettingsList.Presets.Add(newPreset);
            CurrentPresetDropdown.SelectedItem = newPreset;
        }

        PresetDialogOverlay.IsVisible = false;
        SaveEditorSettings();
    }

    private void ModifyPresetCancel_Click(object? sender, RoutedEventArgs e)
    {
        PresetDialogOverlay.IsVisible = false;
    }

    private void CopyAssetsPath(object? sender, RoutedEventArgs e)
    {
        ClipboardManager.CopyText(AssetsPath.Text ?? "", "Path", msg => ShowStatus(msg));
    }

    private void ShowStatus(string message)
    {
        AssetsPathLoadingMessage.Text = message;
    }

    private static void SaveEditorSettings()
    {
        try
        {
            var json = JsonConvert.SerializeObject(SettingsList, Formatting.Indented);
            AppPaths.WriteAllTextAtomic(settingsFilePath, json);
        }
        catch (Exception e)
        {
            // keep the presets in memory: resetting them here made the next successful save persist the defaults
            ErrorManager.ShowError($"Unable to save settings to {settingsFilePath}!\n\n{e.Message}");
        }
    }

    private bool InternalTryLoadDat()
    {
        foreach (VersionInfo version in datStructure.GetAllVersions().Reverse())
        {
            try
            {
                LegacyAppearance Dat = new();
                Dat.ReadLegacyDat(_datPath, version.Structure);
                if (currentPreset != null)
                    currentPreset.Version = version.Structure;
                appearances = Dat.Appearances;
                return true;
            }
            catch
            {
                // expected for every structure that does not match; the caller reports the overall failure
            }
        }
        return false;
    }

    private bool InternalTryLoadSpr()
    {
        try { LoadLegacySpr(); return true; }
        catch (Exception ex)
        {
            Log($"Unable to load {_sprPath}: {ex.Message}", "Error");
            return false;
        }
    }

    private void RefreshUIToggles()
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (currentPreset == null) return;
            DatExtendedToggle.IsChecked = currentPreset.Extended;
            DatTransparentToggle.IsChecked = currentPreset.Transparent;
            DatAnimationsToggle.IsChecked = currentPreset.FrameDurations;
            DatFrameGroupsToggle.IsChecked = currentPreset.FrameGroups;

            foreach (VersionInfo version in datStructure.GetAllVersions().Reverse())
            {
                if (currentPreset.Version == version.Structure)
                    DatStructureDropdown.SelectedItem = version;
            }
        });
    }

    private void SetUILock(bool locked)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            bool enabled = !locked;
            LoadAssets.IsEnabled = enabled;
        });
    }

    private async void LoadAssets_Click(object? sender, RoutedEventArgs e)
    {
        SetUILock(true);

        if (currentPreset == null)
        {
            SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_ERROR, "No preset selected!");
            SetUILock(false);
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                if (LegacyClient == false)
                {
                    LoadSprSheet();
                    Loaded = true;
                }
                else
                {
                    SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_WIP, "Loading dat file ...");

                    bool datFound = false;
                    try
                    {
                        LegacyAppearance Dat = new();
                        Dat.ReadLegacyDat(_datPath, currentPreset.Version);
                        appearances = Dat.Appearances;
                        datFound = true;
                    }
                    catch (Exception ex)
                    {
                        Log($"Dat did not load with the preset's structure {currentPreset.Version}: {ex.InnerException?.Message ?? ex.Message}", "Warning");
                    }

                    // the otfi flags describe the file exactly; try every structure with them before guessing flags
                    if (!datFound)
                    {
                        SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_WIP, "Searching for matching version ...");
                        datFound = InternalTryLoadDat();
                    }

                    if (!datFound)
                    {
                        // then every combination of the three layout flags (a read must end exactly at EOF,
                        // so a wrong guess cannot be accepted)
                        var (extended, durations, groups) = (currentPreset.Extended, currentPreset.FrameDurations, currentPreset.FrameGroups);
                        for (int combo = 0; combo < 8 && !datFound; combo++)
                        {
                            currentPreset.Extended = (combo & 1) != 0;
                            currentPreset.FrameDurations = (combo & 2) != 0;
                            currentPreset.FrameGroups = (combo & 4) != 0;
                            datFound = InternalTryLoadDat();
                        }
                        if (datFound)
                            Log($"Dat layout detected: extended={currentPreset.Extended}, frame durations={currentPreset.FrameDurations}, frame groups={currentPreset.FrameGroups}, structure {currentPreset.Version}");
                        else
                            (currentPreset.Extended, currentPreset.FrameDurations, currentPreset.FrameGroups) = (extended, durations, groups);
                    }

                    if (!datFound)
                    {
                        SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_ERROR, "Unable to load dat! (details in Logger)");
                        RefreshUIToggles();
                        return;
                    }

                    RefreshUIToggles();
                    SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_WIP, "Loading spr file ...");

                    if (!InternalTryLoadSpr())
                    {
                        SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_ERROR, "Unable to load spr!");
                        RefreshUIToggles();
                        return;
                    }

                    if (!String.IsNullOrEmpty(currentPreset.ServerPath))
                    {
                        string otbPath = Path.Combine(currentPreset.ServerPath, "data", "items", "items.otb");
                        try
                        {
                            if (File.Exists(otbPath))
                                ServerOTB.Read(otbPath);
                        }
                        catch (Exception ex)
                        {
                            // a broken items.otb must not abort loading the client
                            Log($"Unable to read {otbPath}: {ex.Message}", "Warning");
                        }
                    }

                    Loaded = true;
                }
            });
        }
        catch (Exception ex)
        {
            // reported here instead of killing the app; the loaders log the details
            Log($"Loading assets failed: {ex}", "Error");
            SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_ERROR, $"Loading failed: {ex.Message}");
            Loaded = false;
        }

        SetUILock(false);
        // Sprite count isn't known until LoadSprSheet/InternalTryLoadSpr populate
        // SprLists; refresh the stats panel now that it's final.
        UpdateAppearancesCount();

        if (appearances == null)
        {
            SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_ERROR, "No suitable version was found.");
            return;
        }

        if (!Loaded) { LoadAssets.IsEnabled = true; return; }

        // Assets are loaded — unlock the tiles that need them and stay on the
        // hub (MainWindow no longer auto-navigates to DAT editor). Update only
        // the text label, NOT the Button.Content, because replacing Content
        // would blow away the inner ProgressBar and break the % binding.
        OpenDatEditorTile.IsEnabled = true;
        OpenSpriteEditorTile.IsEnabled = true;
        LoadAssetsLabel.Text = "Reload Assets";
    }

    private DatEditor? _datEditorWindow;
    private LegacyDatEditor? _legacyDatEditorWindow;

    private void OpenDatEditor_Click(object? sender, RoutedEventArgs e)
    {
        if (appearances == null) return;
        if (LegacyClient)
        {
            if (_legacyDatEditorWindow == null || !_legacyDatEditorWindow.IsVisible)
                _legacyDatEditorWindow = new LegacyDatEditor(appearances, (VersionInfo)DatStructureDropdown.SelectedItem!);
            _legacyDatEditorWindow.Show();
            _legacyDatEditorWindow.Activate();
        }
        else
        {
            if (_datEditorWindow == null || !_datEditorWindow.IsVisible)
                _datEditorWindow = new DatEditor(appearances);
            _datEditorWindow.Show();
            _datEditorWindow.Activate();
        }
    }

    private void OpenSpriteEditor_Click(object? sender, RoutedEventArgs e)
    {
        if (appearances == null) return;
        // SprEditor needs a DatEditor to talk to for list refresh; spin one up
        // (it stays open) if the user hasn't already clicked the DAT tile.
        if (LegacyClient)
        {
            ErrorManager.ShowInfo("Sprite Editor is available on modern clients only.");
            return;
        }
        if (_datEditorWindow == null || !_datEditorWindow.IsVisible)
        {
            _datEditorWindow = new DatEditor(appearances);
            _datEditorWindow.Show();
        }
        new SprEditor(_datEditorWindow).Show();
    }

    private void OpenMonsterEditor_Click(object? sender, RoutedEventArgs e)
    {
        new MonsterEditor().Show();
    }

    private void OpenProficienciesEditor_Click(object? sender, RoutedEventArgs e)
    {
        new ProficienciesEditor().Show();
    }

    private void OpenItemsXmlEditor_Click(object? sender, RoutedEventArgs e)
    {
        new ItemsXmlEditor().Show();
    }

    private void OpenOtbEditor_Click(object? sender, RoutedEventArgs e)
    {
        // OTBEditor uses dynamic dispatch on the "editor" param to reach
        // ObjectMenu/ObjListView — works with either a DatEditor (modern) or
        // this MainWindow (falls back to no-op when those fields are missing,
        // which is fine for server-side-only workflows).
        dynamic owner = _datEditorWindow ?? (object)this;
        var otb = new OTBEditor(owner, LegacyClient);
        otb.Show();
    }

    private static void GenerateTileSetImageList(SKBitmap bitmap, Catalog sheet)
    {
        // Total sprites in this sheet = LastSpriteid - FirstSpriteid + 1
        int tileCount = sheet.LastSpriteid - sheet.FirstSpriteid + 1;
        int sprCount = 0;

        var sprType = sheet.SpriteType;
        if (sprType < 0) return;

        var layout = DatEditor.GetSpriteLayout(sprType);
        int tWidth  = layout.SpriteSizeX;
        int tHeight = layout.SpriteSizeY;
        int xCols   = layout.Cols;   // columns across the 384-wide sheet
        int yCols   = layout.Rows;   // rows down  the 384-high  sheet

        for (int row = 0; row < yCols && sprCount < tileCount; row++)
        {
            for (int col = 0; col < xCols && sprCount < tileCount; col++)
            {
                var tile = new SKBitmap(tWidth, tHeight);
                using var canvas = new SKCanvas(tile);
                canvas.DrawBitmap(bitmap,
                    SKRect.Create(col * tWidth, row * tHeight, tWidth, tHeight),
                    SKRect.Create(0, 0, tWidth, tHeight));

                using var image = SKImage.FromBitmap(tile);
                using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
                SprLists[sheet.FirstSpriteid + sprCount] = new MemoryStream(data.ToArray());
                tile.Dispose();
                sprCount++;
            }
        }
    }

    private void LoadSprSheet()
    {
        int progress = 0;
        SprLists = new ConcurrentDictionary<int, MemoryStream>();
        DirtySpriteIds = [];
        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 5 };

        // Register placeholder (null) entries only for sprite-type catalog entries
        var spriteCatalog = catalog.Where(c => c.Type == "sprite").ToList();
        Parallel.ForEach(spriteCatalog, options, (spr, state) =>
        {
            for (int i = spr.FirstSpriteid; i <= spr.LastSpriteid; i++)
                SprLists[i] = null!;
            Interlocked.Increment(ref progress);
            Dispatcher.UIThread.InvokeAsync(() =>
                LoadProgress.Value = (int)((double)progress / spriteCatalog.Count * 100));
        });

        if (SprLists.IsEmpty) return;

        // AllSprList shows only real sprite IDs (sorted), not every integer up to maxVal
        foreach (int id in SprLists.Keys.OrderBy(k => k))
            AllSprList.Add(new ShowList { Id = (uint)id });
    }

    // Look up the physical pixel dimensions of a sprite ID by finding its owning
    // catalog entry and mapping the sheet's spriteType to DatEditor.SpriteSizes.
    // Returns (0,0) when the ID isn't registered in any sprite catalog entry.
    public static (int w, int h) GetSpriteSize(int spriteId)
    {
        foreach (var cat in catalog)
        {
            if (cat.Type != "sprite") continue;
            if (spriteId < cat.FirstSpriteid || spriteId > cat.LastSpriteid) continue;
            int st = cat.SpriteType;
            if (st < 0 || st >= DatEditor.SpriteSizes.Count) return (0, 0);
            var sz = DatEditor.SpriteSizes[st];
            return (sz.Width, sz.Height);
        }
        return (0, 0);
    }

    public static MemoryStream? getSpriteStream(int spriteId)
    {
        // legacy spr: SprLists only holds placeholders until a sprite is decoded, the storage reads it
        if (LegacyClient && !string.IsNullOrEmpty(MainSprStorage.SprPath))
            return spriteId >= 0 && MainSprStorage.Sprites.ContainsKey((uint)spriteId)
                ? MainSprStorage.getSpriteStream((uint)spriteId)
                : null;

        // Fast path: already loaded
        if (SprLists.TryGetValue(spriteId, out var cached) && cached != null)
            return cached;

        // Key missing entirely → this ID has no sprite data
        if (!SprLists.ContainsKey(spriteId))
            return null;

        // Key exists but value is null → lazy-load from the LZMA sheet file
        Catalog? catalogInfo = null;
        foreach (var cat in catalog)
        {
            if (cat.Type == "sprite" &&
                spriteId >= cat.FirstSpriteid &&
                spriteId <= cat.LastSpriteid)
            {
                catalogInfo = cat;
                break;
            }
        }

        if (catalogInfo != null)
        {
            string sprFilePath = Path.Combine(_assetsPath, catalogInfo.File);
            if (File.Exists(sprFilePath))
            {
                using SKBitmap sheet = LZMA.DecompressFileLZMA(sprFilePath);
                GenerateTileSetImageList(sheet, catalogInfo);
            }
        }

        SprLists.TryGetValue(spriteId, out var result);
        if (result != null) result.Seek(0, SeekOrigin.Begin);
        return result;
    }

    private bool TryLoadAssets()
    {
        try
        {
            if (!File.Exists(Path.Combine(_assetsPath, "catalog-content.json")))
            {
                SetVersionDescription("No version loaded");
                SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_ERROR, "No assets found in selected directory");
                return false;
            }

            LoadCatalogJson();
            LoadAppearances();
            LegacyClient = false;
            SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_INFO, "FOUND: Assets directory");
            return true;
        }
        catch (Exception e)
        {
            SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_ERROR, $"Unable to load assets\n\n{e.Message}");
        }
        return false;
    }

    private static string? SearchAssetFile(string? defaultFileName, string fallbackExtension)
    {
        if (defaultFileName != null && File.Exists(Path.Combine(_assetsPath, defaultFileName)))
            return Path.Combine(_assetsPath, defaultFileName);

        defaultFileName = "Tibia" + fallbackExtension;
        if (File.Exists(Path.Combine(_assetsPath, defaultFileName)))
            return Path.Combine(_assetsPath, defaultFileName);

        if (!Directory.Exists(_assetsPath)) return null;

        string[] found = Directory.GetFiles(_assetsPath, $"*{fallbackExtension}");
        return found.Length > 0 ? Path.Combine(_assetsPath, found[0]) : null;
    }

    private bool TryLoadLegacyAssets()
    {
        string? otfiDatFile = null;
        string? otfiSprFile = null;
        bool has_otfi = false;
        bool extended = false, transparency = false, frameDurations = false, frameGroups = false;

        SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_WIP, "Searching for OTFI ...");

        try
        {
            string? otfiPath = SearchAssetFile(null, ".otfi");
            if (File.Exists(otfiPath))
            {
                var doc = OTMLDocument.Parse(otfiPath);
                OTMLNode root = doc.At("DatSpr");
                extended = root.Get("extended")?.ValueAs<bool>() ?? false;
                transparency = root.Get("transparency")?.ValueAs<bool>() ?? false;
                frameDurations = root.Get("frame-durations")?.ValueAs<bool>() ?? false;
                frameGroups = root.Get("frame-groups")?.ValueAs<bool>() ?? false;
                otfiDatFile = root.Get("metadata-file")?.ValueAs<string>();
                otfiSprFile = root.Get("sprites-file")?.ValueAs<string>();
                string? assetsName = root.Get("assets-name")?.ValueAs<string>();
                if (assetsName != null)
                {
                    otfiDatFile ??= assetsName + ".dat";
                    otfiSprFile ??= assetsName + ".spr";
                }
                has_otfi = true;
            }
        }
        catch (Exception e)
        {
            ErrorManager.ShowWarning($"Unable to read otfi!\n\n{e.Message}");
        }

        SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_WIP, "Searching for DAT file ...");

        string? datPath = SearchAssetFile(otfiDatFile, ".dat");
        if (!File.Exists(datPath))
        {
            if (has_otfi)
            {
                SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_ERROR, "DAT file not found!");
                return true;
            }
            SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_ERROR, "Invalid assets directory!");
            return false;
        }

        string? sprPath = SearchAssetFile(otfiSprFile, ".spr");
        if (!File.Exists(sprPath))
        {
            SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_ERROR, "SPR file not found!");
            return false;
        }

        if (currentPreset != null)
        {
            currentPreset.Extended = extended;
            currentPreset.Transparent = transparency;
            currentPreset.FrameDurations = frameDurations;
            currentPreset.FrameGroups = frameGroups;
            RefreshUIToggles();
        }

        _datPath = datPath;
        _sprPath = sprPath;

        try { LoadLegacyDat(); }
        catch (Exception ex)
        {
            ErrorManager.ShowWarning($"Unable to read dat file!\n\n{ex.Message}");
            SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_ERROR, "Unable to load dat file!");
            return true;
        }

        try { ReadSprSignature(); }
        catch (Exception ex)
        {
            ErrorManager.ShowWarning($"Unable to read spr file!\n\n{ex.Message}");
            SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_ERROR, "Unable to load spr file!");
            return true;
        }

        LoadAssets.IsEnabled = true;
        SetVersionDescription($"DAT: 0x{DatSignature:X}\nSPR: 0x{SprSignature:X}");

        if (DatSignature == 0x0801930A)
        {
            ErrorManager.ShowWarning("Detected DAT signature is matching appearances.dat, but catalog-content.json was not detected in the selected directory.");
            SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_WARNING, "Possibly missing catalog-content.json!");
        }
        else
        {
            SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_INFO, "FOUND: Legacy SPR/DAT");
        }

        return true;
    }

    private void OnInvalidVersionLoaded()
    {
        DatExtendedToggle.IsEnabled = false;
        DatTransparentToggle.IsEnabled = false;
        DatAnimationsToggle.IsEnabled = false;
        DatFrameGroupsToggle.IsEnabled = false;
        DatStructureDropdown.IsEnabled = false;
        LoadAssets.IsEnabled = false;
        ObjectCount = 0; OutfitCount = 0; EffectCount = 0; MissileCount = 0;
        UpdateAppearancesCount();
    }

    private void OnLegacyAssetsLoaded()
    {
        LegacyClient = true;
        DatStructureDropdown.IsEnabled = true;
        DatExtendedToggle.IsEnabled = true;
        DatTransparentToggle.IsEnabled = true;
        DatAnimationsToggle.IsEnabled = true;
        DatFrameGroupsToggle.IsEnabled = true;
    }

    private void OnAssetsLoaded()
    {
        LegacyClient = false;
        DatStructureDropdown.IsEnabled = false;
        DatExtendedToggle.IsChecked = true;
        DatTransparentToggle.IsChecked = true;
        DatAnimationsToggle.IsChecked = true;
        DatFrameGroupsToggle.IsChecked = true;
        DatExtendedToggle.IsEnabled = false;
        DatTransparentToggle.IsEnabled = false;
        DatAnimationsToggle.IsEnabled = false;
        DatFrameGroupsToggle.IsEnabled = false;
        LoadAssets.IsEnabled = true;

        string version = "No data";
        try
        {
            string assetsDir = Path.GetDirectoryName(_assetsPath)!;
            string parentDir = Directory.GetParent(assetsDir)!.FullName;
            string packageJsonPath = Path.Combine(parentDir, "package.json");
            if (File.Exists(packageJsonPath))
            {
                string json = File.ReadAllText(packageJsonPath);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("version", out JsonElement versionElement))
                {
                    string? v = versionElement.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) version = v;
                }
            }
        }
        catch { }

        SetVersionDescription($"Assets Version:\n{version}");
    }

    private void InternalSelectAssets()
    {
        if (_assetsPath == "")
        {
            SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_INFO, "No assets path selected");
            return;
        }

        LegacyClient = true;
        if (TryLoadAssets()) { OnAssetsLoaded(); return; }
        if (TryLoadLegacyAssets()) { OnLegacyAssetsLoaded(); return; }

        string oldAssetsPath = _assetsPath;
        _assetsPath = Path.Combine(_assetsPath, "assets/");
        if (Directory.Exists(_assetsPath) && TryLoadAssets())
        {
            OnAssetsLoaded();
            AssetsPath.Text = _assetsPath;
            return;
        }

        _assetsPath = oldAssetsPath;
        SetVersionDescription("No version loaded");
        SetLoadingStatus(AssetsLoadingStatus.ASSETS_LOADING_INFO, "No assets found in selected directory.");
        OnInvalidVersionLoaded();
    }

    private void SetCurrentPreset()
    {
        int presetId = CurrentPresetDropdown.SelectedIndex;
        if (presetId < 0) return;

        if (SettingsList.Presets.Count == 0)
        {
            SettingsList.Presets = [defaultPreset];
            presetId = 0;
        }

        PresetSettings preset = SettingsList.Presets[presetId];
        currentPreset = preset;
        SettingsList.LastPresetChoice = presetId;

        _assetsPath = preset.ClientPath ?? "";
        if (!_assetsPath.EndsWith(Path.DirectorySeparatorChar) && _assetsPath.Length > 0)
            _assetsPath += Path.DirectorySeparatorChar;
        AssetsPath.Text = _assetsPath;

        if (DatStructureDropdown.Items != null)
        {
            foreach (var item in DatStructureDropdown.Items)
            {
                if (item is VersionInfo versionInfo && preset.Version == versionInfo.Structure)
                {
                    DatStructureDropdown.SelectedItem = item;
                    break;
                }
            }
        }

        InternalSelectAssets();
    }

    private void CurrentPresetDropdown_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        SetCurrentPreset();
    }

    public static int GetCurrentLoadedVersion() => currentPreset?.Version ?? 0;

    private void DatExtended_Changed(object? sender, RoutedEventArgs e)
    {
        if (currentPreset != null) currentPreset.Extended = DatExtendedToggle.IsChecked ?? false;
        SaveEditorSettings();
    }

    private void DatTransparent_Changed(object? sender, RoutedEventArgs e)
    {
        if (currentPreset != null) currentPreset.Transparent = DatTransparentToggle.IsChecked ?? false;
        SaveEditorSettings();
    }

    private void DatAnimations_Changed(object? sender, RoutedEventArgs e)
    {
        if (currentPreset != null) currentPreset.FrameDurations = DatAnimationsToggle.IsChecked ?? false;
        SaveEditorSettings();
    }

    private void DatFrameGroup_Changed(object? sender, RoutedEventArgs e)
    {
        if (currentPreset != null) currentPreset.FrameGroups = DatFrameGroupsToggle.IsChecked ?? false;
        SaveEditorSettings();
    }

    private void DatStructureType_SelectionChanged(object? sender, RoutedEventArgs e)
    {
        if (DatStructureDropdown.SelectedItem is VersionInfo selectedVersion)
        {
            if (currentPreset != null) currentPreset.Version = selectedVersion.Structure;
            SaveEditorSettings();
        }
    }

    public static void Log(string message, string level = "Info")
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Message = message
        };
        logView.AddLogEntry(entry);
    }

    public static void SetCurrentTheme(bool isDarkMode)
    {
        App.SetTheme(isDarkMode);
        SettingsList.DarkMode = isDarkMode;
        SaveEditorSettings();
    }

    public static bool IsDarkModeSet() => SettingsList.DarkMode;

    private void DarkModeToggle_Changed(object? sender, RoutedEventArgs e)
    {
        SetCurrentTheme(DarkModeToggle.IsChecked ?? false);
    }

    private void DensityDropdown_Changed(object? sender, SelectionChangedEventArgs e)
    {
        int idx = DensityDropdown.SelectedIndex;
        if (idx < 0) return;
        SettingsList.Density = (UiDensity)idx;
        App.SetDensity(SettingsList.Density);
        PersistSettings();
    }

    private void SetVersionDescription(string text)
    {
        Dispatcher.UIThread.InvokeAsync(() => AssetsVersionInfo.Text = text);
    }

    enum AssetsLoadingStatus
    {
        ASSETS_LOADING_NONE = 0,
        ASSETS_LOADING_WIP = 1,
        ASSETS_LOADING_INFO = 2,
        ASSETS_LOADING_WARNING = 3,
        ASSETS_LOADING_ERROR = 4,
    }

    private void SetLoadingStatus(AssetsLoadingStatus newStatus, string text)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (newStatus == AssetsLoadingStatus.ASSETS_LOADING_NONE)
            {
                AssetsPathLoadingMessage.IsVisible = false;
                StatusIcon.IsVisible = false;
                return;
            }

            AssetsPathLoadingMessage.Text = text;
            AssetsPathLoadingMessage.IsVisible = true;

            switch (newStatus)
            {
                case AssetsLoadingStatus.ASSETS_LOADING_WIP:
                    StatusIcon.Text = "⏳";
                    StatusIcon.IsVisible = true;
                    break;
                case AssetsLoadingStatus.ASSETS_LOADING_INFO:
                    StatusIcon.Text = "ℹ️";
                    StatusIcon.IsVisible = true;
                    break;
                case AssetsLoadingStatus.ASSETS_LOADING_WARNING:
                    StatusIcon.Text = "⚠️";
                    StatusIcon.IsVisible = true;
                    break;
                case AssetsLoadingStatus.ASSETS_LOADING_ERROR:
                    StatusIcon.Text = "❌";
                    StatusIcon.IsVisible = true;
                    break;
            }
        });
    }
}
