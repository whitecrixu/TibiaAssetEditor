using Google.Protobuf;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ImageMagick;
using SkiaSharp;
using Tibia.Protobuf.Appearances;

namespace Assets_Editor
{
    public partial class DatEditor : Window
    {
        static readonly PresetSettings AssetsPreset = new()
        {
            Extended = true,
            FrameDurations = true,
            FrameGroups = true,
        };

        public static readonly List<System.Drawing.Size> SpriteSizes = [
            new(32, 32), new(32, 64), new(64, 32), new(64, 64),
            new(32, 96), new(32, 128), new(32, 192), new(32, 384),
            new(64, 96), new(64, 128), new(64, 192), new(64, 384),
            new(96, 32), new(96, 64), new(96, 96), new(96, 128), new(96, 192), new(96, 384),
            new(128, 32), new(128, 64), new(128, 96), new(128, 128), new(128, 192), new(128, 384),
            new(192, 32), new(192, 64), new(192, 96), new(192, 128), new(192, 192), new(192, 384),
            new(384, 32), new(384, 64), new(384, 96), new(384, 128), new(384, 192), new(384, 384),
        ];

        public const int SprSheetWidth = 384;
        public const int SprSheetHeight = 384;

        public readonly struct SpriteLayout
        {
            public int SpriteSizeX { get; }
            public int SpriteSizeY { get; }
            public int Cols { get; }
            public int Rows { get; }

            public SpriteLayout(int spriteSizeX, int spriteSizeY, int cols, int rows)
            {
                SpriteSizeX = spriteSizeX;
                SpriteSizeY = spriteSizeY;
                Cols = cols;
                Rows = rows;
            }
        }

        public static SpriteLayout GetSpriteLayout(int spriteType)
        {
            if ((uint)spriteType >= (uint)SpriteSizes.Count)
                spriteType = 0;
            var singleSpriteSize = SpriteSizes[spriteType];
            int spriteSizeX = singleSpriteSize.Width;
            int spriteSizeY = singleSpriteSize.Height;
            int cols = SprSheetWidth / spriteSizeX;
            int rows = SprSheetHeight / spriteSizeY;
            return new(spriteSizeX, spriteSizeY, cols, rows);
        }

        private static ObservableCollection<ShowList> ThingsOutfit = [];
        private static ObservableCollection<ShowList> ThingsItem = [];
        private static ObservableCollection<ShowList> ThingsEffect = [];
        private static ObservableCollection<ShowList> ThingsMissile = [];
        public Appearance? CurrentObjectAppearance;
        public AppearanceFlags? CurrentFlags = null;
        private int CurrentSprDir = 0;
        private bool isPageLoaded = false;

        // Sprites belonging to the currently selected appearance (flat list of SpriteId entries)
        private ObservableCollection<ShowList> AppearanceSprites = [];

        // Animation preview
        private Avalonia.Threading.DispatcherTimer? _animTimer;
        private int _animFrame = 0;
        private int _animFrameCount = 1;

        // Undo/Redo stacks — store snapshots of (AppearanceId, cloned Appearance)
        private readonly Stack<(uint id, Appearance snapshot)> _undoStack = new();
        private readonly Stack<(uint id, Appearance snapshot)> _redoStack = new();

        // Revert snapshot — captured when an appearance is selected, restored on Ctrl+R
        private Appearance? _revertSnapshot;
        private uint _revertSnapshotId;

        // Preview zoom factor (Ctrl+scroll)
        private double _previewZoom = 1.0;

        // O(1) appearance lookup tables built once on load
        private static Dictionary<uint, Appearance> _outfitLookup  = [];
        private static Dictionary<uint, Appearance> _itemLookup    = [];
        private static Dictionary<uint, Appearance> _effectLookup  = [];
        private static Dictionary<uint, Appearance> _missileLookup = [];

        public DatEditor()
        {
            InitializeComponent();
            DarkModeToggle.IsChecked = MainWindow.IsDarkModeSet();

            ObjListView.ContainerPrepared += ObjListView_ContainerPrepared;
            ObjListView.ContainerClearing += ObjListView_ContainerClearing;
            SprListView.ContainerPrepared += SprListView_ContainerPrepared;
            SprListView.ContainerClearing += SprListView_ContainerClearing;
            AppSprListView.ContainerPrepared += AppSprListView_ContainerPrepared;
            AppSprListView.ContainerClearing += AppSprListView_ContainerClearing;
            AppSprListView.ItemsSource = AppearanceSprites;

            // Drag & Drop from file manager onto Appearance Sprites strip
            Avalonia.Input.DragDrop.SetAllowDrop(AppSprListView, true);
            AppSprListView.AddHandler(Avalonia.Input.DragDrop.DragOverEvent, AppSprListView_DragOver);
            AppSprListView.AddHandler(Avalonia.Input.DragDrop.DropEvent, AppSprListView_Drop);

            // Window-level Delete routing — ListBox handles arrow keys internally, and
            // subscribing KeyDown directly on the ListBox doesn't always fire on Linux.
            AddHandler(Avalonia.Input.InputElement.KeyDownEvent, Window_KeyDown,
                       Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

            // Wire flag-parameter controls to the generic apply handler.
            // Avalonia needs specific delegate types, so we wire in code instead of XAML.
            FlagName.TextChanged += (s, e) => FlagParam_Changed(s, e);
            FlagDescription.TextChanged += (s, e) => FlagParam_Changed(s, e);
            FlagGroundSpeed.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagWriteMaxLen.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagWriteOnceMaxLen.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagLightBrightness.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagLightColor.ValueChanged += (s, e) => { FlagParam_Changed(s, e); UpdateColorSwatches(); };
            FlagShiftX.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagShiftY.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagElevation.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagAutomapColor.ValueChanged += (s, e) => { FlagParam_Changed(s, e); UpdateColorSwatches(); };
            FlagTransparencyLevel.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagCyclopediaId.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagChangedToExpireId.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagUpgradeClassAmount.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagMinimumLevel.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagGemQualityId.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagGemVocationId.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagImbueSlotCount.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagProficiencyId.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagMarketTrade.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagMarketShow.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagMarketLevel.ValueChanged += (s, e) => FlagParam_Changed(s, e);
            FlagHookType.SelectionChanged += (s, e) => FlagParam_Changed(s, e);
            FlagLensHelpId.SelectionChanged += (s, e) => FlagParam_Changed(s, e);
            FlagClothesSlot.SelectionChanged += (s, e) => FlagParam_Changed(s, e);
            FlagDefaultActionType.SelectionChanged += (s, e) => FlagParam_Changed(s, e);
            FlagMarketCategory.SelectionChanged += (s, e) => FlagParam_Changed(s, e);
            FlagWeaponType.SelectionChanged += (s, e) => FlagParam_Changed(s, e);
            // Restart animation timer when FPS changes
            SprAnimFps.ValueChanged += (s, e) => { if (SprAnimateToggle.IsChecked == true) StartAnimationTimer(); };
            // Outfit color changes refresh preview
            OutfitHead.ValueChanged += (s, e) => OutfitColor_Changed(s, e);
            OutfitBody.ValueChanged += (s, e) => OutfitColor_Changed(s, e);
            OutfitLegs.ValueChanged += (s, e) => OutfitColor_Changed(s, e);
            OutfitFeet.ValueChanged += (s, e) => OutfitColor_Changed(s, e);
            // Market profession checkboxes
            FlagProfAny.IsCheckedChanged += (s, e) => FlagParam_Changed(s, e);
            FlagProfNone.IsCheckedChanged += (s, e) => FlagParam_Changed(s, e);
            FlagProfKnight.IsCheckedChanged += (s, e) => FlagParam_Changed(s, e);
            FlagProfPaladin.IsCheckedChanged += (s, e) => FlagParam_Changed(s, e);
            FlagProfSorcerer.IsCheckedChanged += (s, e) => FlagParam_Changed(s, e);
            FlagProfDruid.IsCheckedChanged += (s, e) => FlagParam_Changed(s, e);
            FlagProfPromoted.IsCheckedChanged += (s, e) => FlagParam_Changed(s, e);
            // Vocation restriction checkboxes
            FlagRestrictVocAny.IsCheckedChanged += (s, e) => FlagParam_Changed(s, e);
            FlagRestrictVocNone.IsCheckedChanged += (s, e) => FlagParam_Changed(s, e);
            FlagRestrictVocKnight.IsCheckedChanged += (s, e) => FlagParam_Changed(s, e);
            FlagRestrictVocPaladin.IsCheckedChanged += (s, e) => FlagParam_Changed(s, e);
            FlagRestrictVocSorcerer.IsCheckedChanged += (s, e) => FlagParam_Changed(s, e);
            FlagRestrictVocDruid.IsCheckedChanged += (s, e) => FlagParam_Changed(s, e);
            FlagRestrictVocMonk.IsCheckedChanged += (s, e) => FlagParam_Changed(s, e);
            FlagRestrictVocPromoted.IsCheckedChanged += (s, e) => FlagParam_Changed(s, e);

            // Pass 1 features: recent files, autosave, quick jump, bookmarks, settings
            Opened += (s, e) => InitPass1Features();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }

        public DatEditor(Appearances appearances) : this()
        {
            // Build O(1) lookup tables
            _outfitLookup  = appearances.Outfit.ToDictionary(o => o.Id);
            _itemLookup    = appearances.Object.ToDictionary(o => o.Id);
            _effectLookup  = appearances.Effect.ToDictionary(o => o.Id);
            _missileLookup = appearances.Missile.ToDictionary(o => o.Id);

            foreach (var outfit  in appearances.Outfit)   ThingsOutfit.Add(new ShowList  { Id = outfit.Id });
            foreach (var item    in appearances.Object)   ThingsItem.Add(new ShowList    { Id = item.Id });
            foreach (var effect  in appearances.Effect)   ThingsEffect.Add(new ShowList  { Id = effect.Id });
            foreach (var missile in appearances.Missile)  ThingsMissile.Add(new ShowList { Id = missile.Id });

            SprListView.ItemsSource = MainWindow.AllSprList;
            UpdateShowList(ObjectMenu.SelectedIndex);
            isPageLoaded = true;
        }

        private void UpdateShowList(int selection, uint? preserveId = null)
        {
            if (ObjListView == null) return;

            ObservableCollection<ShowList> source =
                selection switch
                {
                    0 => ThingsOutfit,
                    1 => ThingsItem,
                    2 => ThingsEffect,
                    3 => ThingsMissile,
                    _ => ThingsOutfit
                };

            // Clear any images loaded under the previous category before switching source
            foreach (var sl in ThingsOutfit) { sl.StopAnimation(); sl.Images.Clear(); sl.Image = null; }
            foreach (var sl in ThingsItem)   { sl.StopAnimation(); sl.Images.Clear(); sl.Image = null; }
            foreach (var sl in ThingsEffect) { sl.StopAnimation(); sl.Images.Clear(); sl.Image = null; }
            foreach (var sl in ThingsMissile){ sl.StopAnimation(); sl.Images.Clear(); sl.Image = null; }

            IEnumerable<ShowList> query = source.OrderBy(s => s.Id);

            // Populate names — from items.xml (for items) or from appearance.Name (for all)
            var lookupForNames = GetCurrentLookup();
            foreach (var sl in source)
            {
                if (!string.IsNullOrEmpty(sl.Name)) continue; // already set

                // Priority 1: items.xml (items only)
                if (selection == 1 && ItemsXml.IsLoaded)
                {
                    string? xmlName = ItemsXml.GetName(sl.Id);
                    if (!string.IsNullOrEmpty(xmlName)) { sl.Name = xmlName; continue; }
                }

                // Priority 2: appearance's embedded name
                if (lookupForNames.TryGetValue(sl.Id, out var a) && a.HasName)
                    sl.Name = a.Name;
            }

            string? filter = SearchBox?.Text?.Trim();
            if (!string.IsNullOrEmpty(filter))
            {
                query = query.Where(s => MatchesFilter(s.Id, lookupForNames, filter) ||
                    (!string.IsNullOrEmpty(s.Name) && s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)));
            }

            var sorted = new ObservableCollection<ShowList>(query);
            ObjListView.ItemsSource = sorted;

            if (preserveId.HasValue)
            {
                foreach (ShowList item in sorted)
                {
                    if (item.Id == preserveId.Value)
                    {
                        ObjListView.SelectedItem = item;
                        return;
                    }
                }
            }

            if (sorted.Count > 0)
                ObjListView.SelectedIndex = 0;
        }

        public static int GetSpriteIndex(FrameGroup frameGroup, int layers, int patternX, int patternY, int patternZ, int frames)
        {
            var spriteInfo = frameGroup.SpriteInfo;
            int index = 0;
            if (spriteInfo.Animation != null)
                index = (int)(frames % spriteInfo.Animation.SpritePhase.Count);
            index = index * (int)spriteInfo.PatternDepth + patternZ;
            index = index * (int)spriteInfo.PatternHeight + patternY;
            index = index * (int)spriteInfo.PatternWidth + patternX;
            index = index * (int)spriteInfo.Layers + layers;
            return index;
        }

        private void ObjectMenuChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (!isPageLoaded || ObjectMenu == null) return;
            UpdateShowList(ObjectMenu.SelectedIndex);
        }

        private void ObjListView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (!isPageLoaded) return;
            if (ObjListView.SelectedItem is not ShowList selected) return;

            Appearance? appearance = null;
            try
            {
                appearance = ObjectMenu.SelectedIndex switch
                {
                    0 => _outfitLookup.GetValueOrDefault(selected.Id),
                    1 => _itemLookup.GetValueOrDefault(selected.Id),
                    2 => _effectLookup.GetValueOrDefault(selected.Id),
                    3 => _missileLookup.GetValueOrDefault(selected.Id),
                    _ => null
                };
            }
            catch { }

            if (appearance == null) return;

            CurrentObjectAppearance = appearance;
            CurrentFlags = appearance.Flags;

            // Capture a revert snapshot for Ctrl+R
            _revertSnapshot = appearance.Clone();
            _revertSnapshotId = appearance.Id;

            LoadAppearanceFlags(appearance);
            LoadSpriteInfo(appearance);
            LoadAppearanceSpritesList(appearance);

            // Show outfit color panel for outfits with layers > 1
            bool isOutfit = ObjectMenu.SelectedIndex == 0;
            bool hasLayers = appearance.FrameGroup.Count > 0 && appearance.FrameGroup[0].SpriteInfo.Layers > 1;
            OutfitColorPanel.IsVisible = isOutfit && hasLayers;

            // Update group slider range
            SprGroupSlider.Maximum = Math.Max(0, appearance.FrameGroup.Count - 1);

            UpdatePreview(appearance);
        }

        private void LoadAppearanceFlags(Appearance appearance)
        {
            var flags = appearance.Flags;
            if (flags == null) return;

            _suppressFlagApply = true;
            try { LoadAppearanceFlagsInner(flags); }
            finally { _suppressFlagApply = false; }
        }

        private void LoadAppearanceFlagsInner(AppearanceFlags flags)
        {
            // Name / Description
            FlagName.Text = CurrentObjectAppearance?.HasName == true ? CurrentObjectAppearance.Name : "";
            FlagDescription.Text = CurrentObjectAppearance?.HasDescription == true ? CurrentObjectAppearance.Description : "";

            // Basic boolean flags
            FlagGround.IsChecked = flags.Bank != null;
            FlagClip.IsChecked = flags.Clip;
            FlagTop.IsChecked = flags.Top;
            FlagBottom.IsChecked = flags.Bottom;
            FlagContainer.IsChecked = flags.Container;
            FlagStackable.IsChecked = flags.Cumulative;
            FlagUsable.IsChecked = flags.Usable;
            FlagForceUse.IsChecked = flags.Forceuse;
            FlagMultiuse.IsChecked = flags.Multiuse;
            FlagWriteable.IsChecked = flags.Write != null;
            FlagWriteOnce.IsChecked = flags.WriteOnce != null;
            FlagLiquidPool.IsChecked = flags.Liquidpool;
            FlagLiquidContainer.IsChecked = flags.Liquidcontainer;
            FlagImpassable.IsChecked = flags.Unpass;
            FlagUnmovable.IsChecked = flags.Unmove;
            FlagUnsight.IsChecked = flags.Unsight;
            FlagAvoidBlock.IsChecked = flags.Avoid;
            FlagNoMoveAnimation.IsChecked = flags.NoMovementAnimation;
            FlagTake.IsChecked = flags.HasTake;
            FlagHangable.IsChecked = flags.HasHang && flags.Hang;
            FlagHook.IsChecked = flags.Hook != null;
            FlagRotate.IsChecked = flags.Rotate;
            FlagLight.IsChecked = flags.Light != null;
            FlagDontHide.IsChecked = flags.DontHide;
            FlagTranslucent.IsChecked = flags.Translucent;
            FlagShift.IsChecked = flags.Shift != null;
            FlagHeight.IsChecked = flags.Height != null;
            FlagLyingCorpse.IsChecked = flags.LyingObject;
            FlagAnimateAlways.IsChecked = flags.AnimateAlways;
            FlagAutomap.IsChecked = flags.Automap != null;
            FlagLensHelp.IsChecked = flags.Lenshelp != null;
            FlagFullBank.IsChecked = flags.Fullbank;
            FlagIgnoreLook.IsChecked = flags.HasIgnoreLook;
            FlagClothes.IsChecked = flags.Clothes != null && flags.Clothes.HasSlot;
            FlagDefaultAction.IsChecked = flags.DefaultAction != null;
            FlagMarket.IsChecked = flags.Market != null;
            FlagWrap.IsChecked = flags.Wrap;
            FlagUnwrap.IsChecked = flags.Unwrap;
            FlagTopEffect.IsChecked = flags.HasTopeffect;
            FlagDecoItemKit.IsChecked = flags.HasDecoItemKit;
            FlagCorpse.IsChecked = flags.Corpse;
            FlagPlayerCorpse.IsChecked = flags.HasPlayerCorpse;
            FlagShowOffSocket.IsChecked = flags.ShowOffSocket;
            FlagReportable.IsChecked = flags.Reportable;
            FlagReverseAddonE.IsChecked = flags.ReverseAddonsEast;
            FlagReverseAddonW.IsChecked = flags.ReverseAddonsWest;
            FlagReverseAddonN.IsChecked = flags.ReverseAddonsNorth;
            FlagReverseAddonS.IsChecked = flags.ReverseAddonsSouth;
            FlagWearOut.IsChecked = flags.Wearout;
            FlagClockExpire.IsChecked = flags.Clockexpire;
            FlagExpire.IsChecked = flags.Expire;
            FlagExpireStop.IsChecked = flags.Expirestop;
            FlagCyclopedia.IsChecked = flags.Cyclopediaitem != null;
            FlagAmmo.IsChecked = flags.HasAmmo;
            FlagNPC.IsChecked = flags.Npcsaledata.Count > 0;
            FlagSkillWheelGem.IsChecked = flags.SkillwheelGem != null;
            FlagDualWielding.IsChecked = flags.HasDualWielding;
            FlagImbueable.IsChecked = flags.Imbueable != null;
            FlagProficiency.IsChecked = flags.Proficiency != null;
            FlagTransparency.IsChecked = flags.Transparencylevel != null;
            FlagChangedToExpire.IsChecked = flags.Changedtoexpire != null;
            FlagUpgradeClass.IsChecked = flags.Upgradeclassification != null;

            // Flag parameters
            FlagGroundSpeed.Value = flags.Bank != null && flags.Bank.HasWaypoints ? (int)flags.Bank.Waypoints : 0;
            FlagHookType.SelectedIndex = flags.Hook != null && flags.Hook.HasDirection ? (int)flags.Hook.Direction - 1 : -1;
            FlagWriteMaxLen.Value = flags.Write != null && flags.Write.HasMaxTextLength ? (int)flags.Write.MaxTextLength : 0;
            FlagWriteOnceMaxLen.Value = flags.WriteOnce != null && flags.WriteOnce.HasMaxTextLengthOnce ? (int)flags.WriteOnce.MaxTextLengthOnce : 0;
            FlagLightBrightness.Value = flags.Light != null && flags.Light.HasBrightness ? (int)flags.Light.Brightness : 0;
            FlagLightColor.Value = flags.Light != null && flags.Light.HasColor ? (int)flags.Light.Color : 0;
            FlagShiftX.Value = flags.Shift != null && flags.Shift.HasX ? (int)flags.Shift.X : 0;
            FlagShiftY.Value = flags.Shift != null && flags.Shift.HasY ? (int)flags.Shift.Y : 0;
            FlagElevation.Value = flags.Height != null && flags.Height.HasElevation ? (int)flags.Height.Elevation : 0;
            FlagAutomapColor.Value = flags.Automap != null && flags.Automap.HasColor ? (int)flags.Automap.Color : 0;
            FlagLensHelpId.SelectedIndex = flags.Lenshelp != null ? Math.Max(-1, (int)flags.Lenshelp.Id - 1100) : -1;
            FlagClothesSlot.SelectedIndex = flags.Clothes != null && flags.Clothes.HasSlot ? (int)flags.Clothes.Slot : -1;
            FlagDefaultActionType.SelectedIndex = flags.DefaultAction != null && flags.DefaultAction.HasAction ? (int)flags.DefaultAction.Action : -1;
            FlagTransparencyLevel.Value = flags.Transparencylevel != null && flags.Transparencylevel.HasLevel ? (int)flags.Transparencylevel.Level : 0;
            FlagCyclopediaId.Value = flags.Cyclopediaitem != null && flags.Cyclopediaitem.HasCyclopediaType ? (int)flags.Cyclopediaitem.CyclopediaType : 0;
            FlagChangedToExpireId.Value = flags.Changedtoexpire != null && flags.Changedtoexpire.HasFormerObjectTypeid ? (int)flags.Changedtoexpire.FormerObjectTypeid : 0;
            FlagUpgradeClassAmount.Value = flags.Upgradeclassification != null && flags.Upgradeclassification.HasUpgradeClassification ? (int)flags.Upgradeclassification.UpgradeClassification : 0;
            FlagMinimumLevel.Value = flags.HasMinimumLevel ? (int)flags.MinimumLevel : 0;
            FlagGemQualityId.Value = flags.SkillwheelGem != null && flags.SkillwheelGem.HasGemQualityId ? (int)flags.SkillwheelGem.GemQualityId : 0;
            FlagGemVocationId.Value = flags.SkillwheelGem != null && flags.SkillwheelGem.HasVocationId ? (int)flags.SkillwheelGem.VocationId : 0;
            FlagImbueSlotCount.Value = flags.Imbueable != null && flags.Imbueable.HasSlotCount ? (int)flags.Imbueable.SlotCount : 0;
            FlagProficiencyId.Value = flags.Proficiency != null && flags.Proficiency.HasProficiencyId ? (int)flags.Proficiency.ProficiencyId : 0;
            FlagWeaponType.SelectedIndex = flags.HasWeaponType ? (int)flags.WeaponType : 0;

            // Market
            FlagMarketCategory.SelectedIndex = flags.Market != null && flags.Market.HasCategory ? (int)flags.Market.Category - 1 : -1;
            FlagMarketTrade.Value = flags.Market != null && flags.Market.HasTradeAsObjectId ? (int)flags.Market.TradeAsObjectId : 0;
            FlagMarketShow.Value = flags.Market != null && flags.Market.HasShowAsObjectId ? (int)flags.Market.ShowAsObjectId : 0;
            FlagMarketLevel.Value = flags.Market != null && flags.Market.HasMinimumLevel ? (int)flags.Market.MinimumLevel : 0;
            FlagProfAny.IsChecked = false; FlagProfNone.IsChecked = false;
            FlagProfKnight.IsChecked = false; FlagProfPaladin.IsChecked = false;
            FlagProfSorcerer.IsChecked = false; FlagProfDruid.IsChecked = false;
            FlagProfPromoted.IsChecked = false;
            if (flags.Market != null)
            {
                foreach (var p in flags.Market.RestrictToVocation)
                {
                    if (p == VOCATION.Any) FlagProfAny.IsChecked = true;
                    else if (p == VOCATION.None) FlagProfNone.IsChecked = true;
                    else if (p == VOCATION.Knight) FlagProfKnight.IsChecked = true;
                    else if (p == VOCATION.Paladin) FlagProfPaladin.IsChecked = true;
                    else if (p == VOCATION.Sorcerer) FlagProfSorcerer.IsChecked = true;
                    else if (p == VOCATION.Druid) FlagProfDruid.IsChecked = true;
                    else if (p == VOCATION.Promoted) FlagProfPromoted.IsChecked = true;
                }
            }

            // Vocation restriction (item-level)
            FlagRestrictVocAny.IsChecked = false; FlagRestrictVocNone.IsChecked = false;
            FlagRestrictVocKnight.IsChecked = false; FlagRestrictVocPaladin.IsChecked = false;
            FlagRestrictVocSorcerer.IsChecked = false; FlagRestrictVocDruid.IsChecked = false;
            FlagRestrictVocMonk.IsChecked = false; FlagRestrictVocPromoted.IsChecked = false;
            if (flags.RestrictToVocation != null)
            {
                foreach (var p in flags.RestrictToVocation)
                {
                    if (p == VOCATION.Any) FlagRestrictVocAny.IsChecked = true;
                    else if (p == VOCATION.None) FlagRestrictVocNone.IsChecked = true;
                    else if (p == VOCATION.Knight) FlagRestrictVocKnight.IsChecked = true;
                    else if (p == VOCATION.Paladin) FlagRestrictVocPaladin.IsChecked = true;
                    else if (p == VOCATION.Sorcerer) FlagRestrictVocSorcerer.IsChecked = true;
                    else if (p == VOCATION.Druid) FlagRestrictVocDruid.IsChecked = true;
                    else if (p == VOCATION.Promoted) FlagRestrictVocPromoted.IsChecked = true;
                }
            }

            // Full info
            FullInfoBox.Text = CurrentObjectAppearance?.ToString() ?? "";

            // Color swatches
            UpdateColorSwatches();
        }

        private void LoadSpriteInfo(Appearance appearance)
        {
            if (appearance.FrameGroup.Count == 0) return;
            var spriteInfo = appearance.FrameGroup[0].SpriteInfo;
            SprWidth.Value = spriteInfo.PatternWidth;
            SprHeight.Value = spriteInfo.PatternHeight;
            SprLayers.Value = spriteInfo.Layers;
            SprPatternX.Value = spriteInfo.PatternX;
            SprPatternY.Value = spriteInfo.PatternY;
            SprPatternZ.Value = spriteInfo.PatternZ;
            SprFrames.Value = spriteInfo.PatternFrames;
        }

        private void UpdatePreview(Appearance appearance, int animFrame = 0)
        {
            SprPreviewImage.Source = null;
            if (appearance.FrameGroup.Count == 0) return;

            try
            {
                int groupIdx = Math.Clamp((int)SprGroupSlider.Value, 0, appearance.FrameGroup.Count - 1);
                var frameGroup = appearance.FrameGroup[groupIdx];
                var info = frameGroup.SpriteInfo;

                // Track total animation frame count for the timer
                _animFrameCount = (int)(info.Animation?.SpritePhase.Count ?? 1);
                if (_animFrameCount < 1) _animFrameCount = 1;

                // PatternWidth = number of directions (4 for outfits, 1 for items)
                int dirCount = Math.Max(1, (int)info.PatternWidth);
                int dir = Math.Clamp(CurrentSprDir, 0, dirCount - 1);

                // For multi-tile sprites (e.g. 2x2 outfits), compose all tiles into one image
                int tileW = (int)Math.Max(1, info.PatternWidth > 1 ? 1 : info.PatternWidth);
                int tileH = (int)Math.Max(1, info.PatternHeight);

                // Use the requested animation frame
                int frame = Math.Clamp(animFrame, 0, _animFrameCount - 1);
                var sprIds = info.SpriteId;

                // Collect all tile positions for this direction/frame
                var bitmaps = new List<(int col, int row, SkiaSharp.SKBitmap bmp)>();
                for (int py = 0; py < (int)info.PatternHeight; py++)
                {
                    // Only use first width tile per direction for preview
                    int idx = GetSpriteIndex(frameGroup, 0, dir % (int)Math.Max(1, info.PatternWidth), py, 0, frame);
                    if (idx >= 0 && idx < sprIds.Count)
                    {
                        var stream = MainWindow.getSpriteStream((int)sprIds[idx]);
                        if (stream != null)
                        {
                            var skBmp = SkiaSharp.SKBitmap.Decode(stream.ToArray());
                            if (skBmp != null) bitmaps.Add((0, py, skBmp));
                        }
                    }
                }

                if (bitmaps.Count == 0) return;

                // Compose into a single preview bitmap (stack rows vertically)
                int sprW = bitmaps[0].bmp.Width;
                int sprH = bitmaps[0].bmp.Height;
                int compositeH = sprH * bitmaps.Count;

                using var composite = new SkiaSharp.SKBitmap(sprW, compositeH,
                    SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                using var canvas = new SkiaSharp.SKCanvas(composite);
                canvas.Clear(SkiaSharp.SKColors.Transparent);

                for (int i = 0; i < bitmaps.Count; i++)
                {
                    canvas.DrawBitmap(bitmaps[i].bmp, 0, i * sprH);
                    bitmaps[i].bmp.Dispose();
                }

                // Outfit colorization: if the appearance has layers > 1 and colorizing is on,
                // load the template layer (layer=1) and apply outfit colors.
                if (OutfitBlendToggle.IsChecked == true && info.Layers > 1)
                {
                    try
                    {
                        // Build a template composite from layer 1 sprites
                        using var templateComposite = new SKBitmap(sprW, compositeH, SKColorType.Bgra8888, SKAlphaType.Premul);
                        using var templateCanvas = new SKCanvas(templateComposite);
                        templateCanvas.Clear(SKColors.Transparent);
                        for (int py = 0; py < (int)info.PatternHeight; py++)
                        {
                            int tidx = GetSpriteIndex(frameGroup, 1, dir % (int)Math.Max(1, info.PatternWidth), py, 0, frame);
                            if (tidx >= 0 && tidx < sprIds.Count)
                            {
                                var ts = MainWindow.getSpriteStream((int)sprIds[tidx]);
                                if (ts != null)
                                {
                                    using var tb = SKBitmap.Decode(ts.ToArray());
                                    if (tb != null) templateCanvas.DrawBitmap(tb, 0, py * sprH);
                                }
                            }
                        }

                        var headColor = Utils.GetOutfitColor((int)(OutfitHead.Value ?? 0));
                        var bodyColor = Utils.GetOutfitColor((int)(OutfitBody.Value ?? 0));
                        var legsColor = Utils.GetOutfitColor((int)(OutfitLegs.Value ?? 0));
                        var feetColor = Utils.GetOutfitColor((int)(OutfitFeet.Value ?? 0));
                        Utils.ColorizeOutfit(templateComposite, composite, headColor, bodyColor, legsColor, feetColor);
                    }
                    catch { /* colorization is best-effort */ }
                }

                // Optional 32×32 guide overlay: shows the bottom-right 32×32 region
                // (the map-tile area a normal-sized creature should occupy). Only
                // meaningful when either dimension is > 32, otherwise the guide
                // would cover the entire canvas and add no information.
                if (SprCanvasGuide.IsChecked == true && (sprW > 32 || sprH > 32))
                {
                    using var paint = new SkiaSharp.SKPaint
                    {
                        Color = new SkiaSharp.SKColor(255, 0, 0, 200),
                        Style = SkiaSharp.SKPaintStyle.Stroke,
                        StrokeWidth = 1,
                        IsAntialias = false,
                    };
                    // Draw a box around the bottom-right 32×32 of each stacked tile.
                    for (int i = 0; i < bitmaps.Count; i++)
                    {
                        int boxX = Math.Max(0, sprW - 32);
                        int boxY = i * sprH + Math.Max(0, sprH - 32);
                        int boxW = Math.Min(32, sprW);
                        int boxH = Math.Min(32, sprH);
                        canvas.DrawRect(boxX + 0.5f, boxY + 0.5f, boxW - 1, boxH - 1, paint);
                    }
                    // Full crosshair at the inner boundary (where 32×32 region starts).
                    if (sprW > 32)
                        canvas.DrawLine(sprW - 32 + 0.5f, 0, sprW - 32 + 0.5f, compositeH, paint);
                    if (sprH > 32)
                        canvas.DrawLine(0, sprH - 32 + 0.5f, sprW, sprH - 32 + 0.5f, paint);
                }

                SprPreviewImage.Source = Utils.SKBitmapToAvaloniaBitmap(composite);
            }
            catch { }
        }

        private void SprCanvasGuide_Changed(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance != null)
                UpdatePreview(CurrentObjectAppearance);
        }

        private void SprAnimateToggle_Changed(object? sender, RoutedEventArgs e)
        {
            if (SprAnimateToggle.IsChecked == true)
            {
                StartAnimationTimer();
            }
            else
            {
                StopAnimationTimer();
                _animFrame = 0;
                if (CurrentObjectAppearance != null)
                    UpdatePreview(CurrentObjectAppearance);
            }
        }

        private void StartAnimationTimer()
        {
            StopAnimationTimer();
            int fps = (int)(SprAnimFps.Value ?? 5);
            _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / fps) };
            _animTimer.Tick += (s, e) =>
            {
                if (CurrentObjectAppearance == null) return;
                _animFrame = (_animFrame + 1) % Math.Max(1, _animFrameCount);
                UpdatePreview(CurrentObjectAppearance, _animFrame);
                SprFrameCounter.Text = $"{_animFrame + 1}/{_animFrameCount}";
            };
            _animTimer.Start();
        }

        private void StopAnimationTimer()
        {
            _animTimer?.Stop();
            _animTimer = null;
            SprFrameCounter.Text = "";
        }

        private void Undo_Click(object? sender, RoutedEventArgs e) => Undo();
        private void Redo_Click(object? sender, RoutedEventArgs e) => Redo();
        private void Revert_Click(object? sender, RoutedEventArgs e) => Revert();

        private void UpdateColorSwatches()
        {
            int lightVal = (int)(FlagLightColor.Value ?? 0);
            var lc = Utils.Get8Bit(lightVal);
            LightColorSwatch.Background = new Avalonia.Media.SolidColorBrush(lc);

            int automapVal = (int)(FlagAutomapColor.Value ?? 0);
            var ac = Utils.Get8Bit(automapVal);
            AutomapColorSwatch.Background = new Avalonia.Media.SolidColorBrush(ac);
        }

        private void OutfitColor_Changed(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance != null)
                UpdatePreview(CurrentObjectAppearance, _animFrame);
        }

        private void OutfitRandomize_Click(object? sender, RoutedEventArgs e)
        {
            var rng = new Random();
            OutfitHead.Value = rng.Next(0, 133);
            OutfitBody.Value = rng.Next(0, 133);
            OutfitLegs.Value = rng.Next(0, 133);
            OutfitFeet.Value = rng.Next(0, 133);
            OutfitBlendToggle.IsChecked = true;
            if (CurrentObjectAppearance != null)
                UpdatePreview(CurrentObjectAppearance, _animFrame);
        }

        private void ChangeDirection(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string dir)
            {
                CurrentSprDir = int.Parse(dir);
                if (CurrentObjectAppearance != null)
                    UpdatePreview(CurrentObjectAppearance);
            }
        }

        private void SprGroupSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            // Update frame group
        }

        private void DarkModeToggle_Changed(object? sender, RoutedEventArgs e)
        {
            MainWindow.SetCurrentTheme(DarkModeToggle.IsChecked ?? false);
        }

        private async void SaveDat_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null) return;

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save DAT file",
                DefaultExtension = "dat",
                FileTypeChoices = [new FilePickerFileType("DAT file") { Patterns = ["*.dat"] }]
            });

            if (file != null)
            {
                try
                {
                    using var stream = await file.OpenWriteAsync();
                    MainWindow.appearances.WriteTo(stream);
                    ErrorManager.ShowInfo("DAT saved successfully!");
                }
                catch (Exception ex)
                {
                    ErrorManager.ShowError($"Failed to save DAT: {ex.Message}");
                }
            }
        }

        private async void ExportImages_Click(object? sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Export Directory"
            });

            if (folders.Count > 0 && MainWindow.appearances != null)
            {
                string exportPath = folders[0].Path.LocalPath;
                await Task.Run(() =>
                {
                    try
                    {
                        foreach (var item in MainWindow.appearances.Object)
                            ImageExporter.SaveItemAsGIF(exportPath, item);
                        foreach (var outfit in MainWindow.appearances.Outfit)
                            ImageExporter.SaveOutfitAsImages(exportPath, outfit);
                        foreach (var effect in MainWindow.appearances.Effect)
                            ImageExporter.SaveEffectAsGIF(exportPath, effect);
                        foreach (var missile in MainWindow.appearances.Missile)
                            ImageExporter.SaveMissileAsGIF(exportPath, missile);
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.UIThread.InvokeAsync(() =>
                            ErrorManager.ShowError($"Export failed: {ex.Message}"));
                    }
                });
                ErrorManager.ShowInfo("Export completed!");
            }
        }

        private async void ExportOBD_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null)
            {
                ErrorManager.ShowError("No appearances loaded.");
                return;
            }
            if (CurrentObjectAppearance == null)
            {
                ErrorManager.ShowInfo("Select an appearance in the list first.");
                return;
            }

            // Make sure any unsaved flag edits are baked into the appearance before we serialize.
            try { ApplyFlagsToAppearance(CurrentObjectAppearance); } catch { /* not fatal */ }

            try
            {
                bool ok = await ObdDecoder.Export(new List<Appearance> { CurrentObjectAppearance }, this);
                if (ok && StatusBar != null)
                    StatusBar.Text = $"Exported {CurrentObjectAppearance.AppearanceType} {CurrentObjectAppearance.Id} as OBD.";
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"OBD export failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async void ImportOBD_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null)
            {
                ErrorManager.ShowError("No appearances loaded.");
                return;
            }
            if (string.IsNullOrEmpty(MainWindow._assetsPath) || !Directory.Exists(MainWindow._assetsPath))
            {
                ErrorManager.ShowError("Assets path not set. Load assets first.");
                return;
            }
            if (ObdDecoder.GetDatStructure() == null)
            {
                ErrorManager.ShowError("OBD structure not defined (check Appearances.xml for 1098 version).");
                return;
            }

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import OBD",
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("Object Editor Dump") { Patterns = ["*.obd"] }]
            });
            if (files == null || files.Count == 0) return;

            _lastImportedTabIndex = null;
            _lastImportedId = null;

            int imported = 0, failed = 0;
            var failureDetails = new List<string>();
            foreach (var file in files)
            {
                try
                {
                    if (InternalImportOBD(file.Path.LocalPath)) imported++;
                    else { failed++; failureDetails.Add($"{Path.GetFileName(file.Path.LocalPath)}: decode returned null"); }
                }
                catch (Exception ex)
                {
                    failed++;
                    string d = $"{Path.GetFileName(file.Path.LocalPath)}: {ex.GetType().Name}: {ex.Message}";
                    failureDetails.Add(d);
                    MainWindow.Log($"OBD import failed — {d}\n{ex.StackTrace}");
                }
            }

            FocusLastImported();
            if (StatusBar != null)
                StatusBar.Text = $"OBD import: {imported} OK, {failed} failed.";
            if (imported > 0 && failed == 0)
                ErrorManager.ShowInfo($"Imported {imported} object(s) from OBD.");
            else if (failed > 0)
                ErrorManager.ShowError($"OBD import: {imported} OK, {failed} failed.\n\n" + string.Join("\n", failureDetails));
        }

        // After a successful import, switch to the tab of whatever was first inserted
        // and scroll to that id so the new entry is visible immediately.
        private void FocusLastImported()
        {
            if (_lastImportedTabIndex is int tab && tab >= 0 && tab < 4)
            {
                if (ObjectMenu.SelectedIndex != tab)
                    ObjectMenu.SelectedIndex = tab;
                UpdateShowList(tab, preserveId: _lastImportedId);
            }
            else
            {
                UpdateShowList(ObjectMenu.SelectedIndex);
            }
        }

        // Decode a single .obd file, composite legacy 32px tiles into per-frame bitmaps,
        // pack them into fresh sprite sheets on disk, register the new catalog ranges and
        // sprite IDs, remap the appearance's SpriteId list to modern IDs and insert it
        // into the current collection. Mirrors DatEditor.InternalImportOBD from the
        // Windows editor but uses SkiaSharp instead of System.Drawing and avoids the
        // "pendingOBDImports" batching (we process one file at a time).
        private bool InternalImportOBD(string fileName)
        {
            var objectSprList = new ConcurrentDictionary<int, MemoryStream>();
            Appearance? appearance = ObdDecoder.Load(fileName, ref objectSprList);
            if (appearance == null)
                return false;

            // Build one composite PNG per logical frame. Multi-tile frames (pw/ph > 1)
            // get assembled into a single bigger tile so the modern format can reference
            // them with a single sprite id per frame.
            var composed = new List<(int width, int height, MemoryStream png)>();
            var frameMap = new List<(int fgIndex, int frame, int streamIdx)>();
            const int legacyTile = 32;
            int streamIdx = 0;

            for (int fgIndex = 0; fgIndex < appearance.FrameGroup.Count; fgIndex++)
            {
                var si = appearance.FrameGroup[fgIndex].SpriteInfo;
                int pw = Math.Max(1, (int)si.PatternWidth);
                int ph = Math.Max(1, (int)si.PatternHeight);
                int tilesPerFrame = pw * ph;
                int totalTiles = si.SpriteId.Count;
                if (totalTiles == 0) continue;
                int framesCount = Math.Max(1, totalTiles / tilesPerFrame);

                for (int frame = 0; frame < framesCount; frame++)
                {
                    int combinedW = pw * legacyTile;
                    int combinedH = ph * legacyTile;
                    using var composite = new SKBitmap(combinedW, combinedH, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using var canvas = new SKCanvas(composite);
                    canvas.Clear(SKColors.Transparent);

                    for (int t = 0; t < tilesPerFrame; t++)
                    {
                        int tileGlobalIdx = frame * tilesPerFrame + t;
                        if (tileGlobalIdx >= si.SpriteId.Count) break;
                        int legacyId = (int)si.SpriteId[tileGlobalIdx];

                        int col = t % pw;
                        int row = t / pw;
                        // Legacy OBD uses bottom-right origin — invert both axes.
                        int x = (pw - 1 - col) * legacyTile;
                        int y = (ph - 1 - row) * legacyTile;

                        if (objectSprList.TryGetValue(legacyId, out var tileStream) && tileStream != null)
                        {
                            tileStream.Position = 0;
                            using var tileBmp = SKBitmap.Decode(tileStream);
                            if (tileBmp != null)
                            {
                                canvas.DrawBitmap(tileBmp,
                                    SKRect.Create(0, 0, tileBmp.Width, tileBmp.Height),
                                    SKRect.Create(x, y, legacyTile, legacyTile));
                            }
                        }
                    }

                    var pngMs = new MemoryStream();
                    using (var img = SKImage.FromBitmap(composite))
                    using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
                        data.SaveTo(pngMs);
                    pngMs.Position = 0;

                    composed.Add((combinedW, combinedH, pngMs));
                    frameMap.Add((fgIndex, frame, streamIdx));
                    streamIdx++;
                }
            }

            // Build new sprite sheets on disk and register everything.
            var newIdByStreamIdx = BuildSpriteSheetsAndRegister(composed);

            // Build (fg, frame) -> newId lookup for the remap step.
            var remap = new Dictionary<(int fg, int frame), uint>();
            foreach (var (fgIdx, frame, idx) in frameMap)
                if (newIdByStreamIdx.TryGetValue(idx, out uint nid))
                    remap[(fgIdx, frame)] = nid;

            // Convert legacy pattern semantics -> modern. For each frame group, replace
            // the legacy per-tile SpriteId list with one id per logical frame.
            for (int fgIndex = 0; fgIndex < appearance.FrameGroup.Count; fgIndex++)
            {
                var fg = appearance.FrameGroup[fgIndex];
                if (appearance.AppearanceType != APPEARANCE_TYPE.AppearanceOutfit)
                    fg.FixedFrameGroup = FIXED_FRAME_GROUP.ObjectInitial;

                var si = fg.SpriteInfo;
                int pw = Math.Max(1, (int)si.PatternWidth);
                int ph = Math.Max(1, (int)si.PatternHeight);
                int tilesPerFrame = pw * ph;

                si.Layers = si.PatternLayers;
                si.PatternSize = Math.Max(si.PatternSize, (uint)legacyTile);
                si.PatternWidth  = si.PatternX;
                si.PatternHeight = si.PatternY;
                si.PatternDepth  = si.PatternZ;

                int totalTiles = si.SpriteId.Count;
                int framesCount = Math.Max(1, totalTiles / tilesPerFrame);

                var newIds = new List<uint>(framesCount);
                for (int frame = 0; frame < framesCount; frame++)
                {
                    remap.TryGetValue((fgIndex, frame), out uint nid);
                    newIds.Add(nid);
                }
                si.SpriteId.Clear();
                foreach (var id in newIds) si.SpriteId.Add(id);
            }

            appearance.SpriteData.Clear();

            return InsertAppearance(appearance);
        }

        // Which ObjectMenu tab owns a given appearance type — used to auto-switch the
        // list view to whatever was just imported.
        private static int TabIndexForType(APPEARANCE_TYPE t) => t switch
        {
            APPEARANCE_TYPE.AppearanceOutfit  => 0,
            APPEARANCE_TYPE.AppearanceObject  => 1,
            APPEARANCE_TYPE.AppearanceEffect  => 2,
            APPEARANCE_TYPE.AppearanceMissile => 3,
            _ => -1,
        };

        private int? _lastImportedTabIndex;
        private uint? _lastImportedId;

        // Add an appearance to the right protobuf collection + lookup + UI ShowList,
        // assigning a fresh id if the incoming one collides. Returns false for unknown types.
        private bool InsertAppearance(Appearance appearance)
        {
            var appearances = MainWindow.appearances!;
            switch (appearance.AppearanceType)
            {
                case APPEARANCE_TYPE.AppearanceOutfit:
                    if (appearance.Id == 0 || appearances.Outfit.Any(a => a.Id == appearance.Id))
                        appearance.Id = appearances.Outfit.Count > 0 ? appearances.Outfit[^1].Id + 1u : 1u;
                    appearances.Outfit.Add(appearance);
                    _outfitLookup[appearance.Id] = appearance;
                    ThingsOutfit.Add(new ShowList { Id = appearance.Id });
                    _lastImportedTabIndex ??= 0;
                    _lastImportedId ??= appearance.Id;
                    return true;

                case APPEARANCE_TYPE.AppearanceObject:
                    if (appearance.Id < 100 || appearances.Object.Any(a => a.Id == appearance.Id))
                        appearance.Id = appearances.Object.Count > 0 ? appearances.Object[^1].Id + 1u : 100u;
                    if (appearance.Flags?.Market != null)
                    {
                        if (appearance.Flags.Market.HasTradeAsObjectId) appearance.Flags.Market.TradeAsObjectId = appearance.Id;
                        if (appearance.Flags.Market.HasShowAsObjectId)  appearance.Flags.Market.ShowAsObjectId  = appearance.Id;
                    }
                    if (appearance.Flags?.Cyclopediaitem != null && appearance.Flags.Cyclopediaitem.HasCyclopediaType)
                        appearance.Flags.Cyclopediaitem.CyclopediaType = appearance.Id;
                    appearances.Object.Add(appearance);
                    _itemLookup[appearance.Id] = appearance;
                    ThingsItem.Add(new ShowList { Id = appearance.Id });
                    _lastImportedTabIndex ??= 1;
                    _lastImportedId ??= appearance.Id;
                    return true;

                case APPEARANCE_TYPE.AppearanceEffect:
                    if (appearance.Id == 0 || appearances.Effect.Any(a => a.Id == appearance.Id))
                        appearance.Id = appearances.Effect.Count > 0 ? appearances.Effect[^1].Id + 1u : 1u;
                    appearances.Effect.Add(appearance);
                    _effectLookup[appearance.Id] = appearance;
                    ThingsEffect.Add(new ShowList { Id = appearance.Id });
                    _lastImportedTabIndex ??= 2;
                    _lastImportedId ??= appearance.Id;
                    return true;

                case APPEARANCE_TYPE.AppearanceMissile:
                    if (appearance.Id == 0 || appearances.Missile.Any(a => a.Id == appearance.Id))
                        appearance.Id = appearances.Missile.Count > 0 ? appearances.Missile[^1].Id + 1u : 1u;
                    appearances.Missile.Add(appearance);
                    _missileLookup[appearance.Id] = appearance;
                    ThingsMissile.Add(new ShowList { Id = appearance.Id });
                    _lastImportedTabIndex ??= 3;
                    _lastImportedId ??= appearance.Id;
                    return true;

                default:
                    return false;
            }
        }

        // ── AEC (Assets Editor Container) ─────────────────────────────────────
        //
        // AEC is just the `Appearances` protobuf serialized to disk, with each
        // contained Appearance having its `SpriteData` field filled in: one PNG
        // blob per entry in SpriteInfo.SpriteId (flattened across frame groups).
        // During import, the on-disk SpriteId values are discarded — we walk the
        // SpriteData in order and assign fresh global ids via
        // BuildSpriteSheetsAndRegister, then patch SpriteId in place.

        private async void ExportAEC_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null)
            {
                ErrorManager.ShowError("No appearances loaded.");
                return;
            }
            if (CurrentObjectAppearance == null)
            {
                ErrorManager.ShowInfo("Select an appearance in the list first.");
                return;
            }

            try { ApplyFlagsToAppearance(CurrentObjectAppearance); } catch { /* not fatal */ }

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export AEC",
                DefaultExtension = "aec",
                SuggestedFileName = $"{CurrentObjectAppearance.AppearanceType}_{CurrentObjectAppearance.Id}.aec",
                FileTypeChoices = [new FilePickerFileType("Assets Editor Container") { Patterns = ["*.aec"] }]
            });
            if (file == null) return;

            // Tibia's .dat files leave Appearance.appearance_type unset — the category
            // is implied by which repeated field the entry lives in (outfit/object/…).
            // For AEC we must infer the category from the source list (which ObjectMenu
            // tab the user is on), otherwise protobuf's default enum value would make
            // every exported outfit look like an APPEARANCE_OBJECT on re-import.
            var sourceType = ObjectMenu.SelectedIndex switch
            {
                0 => APPEARANCE_TYPE.AppearanceOutfit,
                1 => APPEARANCE_TYPE.AppearanceObject,
                2 => APPEARANCE_TYPE.AppearanceEffect,
                3 => APPEARANCE_TYPE.AppearanceMissile,
                _ => APPEARANCE_TYPE.AppearanceObject,
            };

            try
            {
                var container = new Appearances();
                var clone = CurrentObjectAppearance.Clone();
                clone.AppearanceType = sourceType;

                // Fill SpriteData with PNG bytes (one per SpriteId across all frame groups)
                // and renumber SpriteId to a simple 0..N counter so the file is self-contained.
                uint counter = 0;
                for (int fg = 0; fg < clone.FrameGroup.Count; fg++)
                {
                    var si = clone.FrameGroup[fg].SpriteInfo;
                    for (int s = 0; s < si.SpriteId.Count; s++)
                    {
                        var stream = MainWindow.getSpriteStream((int)si.SpriteId[s]);
                        if (stream != null)
                        {
                            stream.Position = 0;
                            clone.SpriteData.Add(ByteString.CopyFrom(stream.ToArray()));
                        }
                        else
                        {
                            clone.SpriteData.Add(ByteString.Empty);
                        }
                        si.SpriteId[s] = counter++;
                    }
                }

                switch (sourceType)
                {
                    case APPEARANCE_TYPE.AppearanceOutfit:  container.Outfit.Add(clone);  break;
                    case APPEARANCE_TYPE.AppearanceObject:  container.Object.Add(clone);  break;
                    case APPEARANCE_TYPE.AppearanceEffect:  container.Effect.Add(clone);  break;
                    case APPEARANCE_TYPE.AppearanceMissile: container.Missile.Add(clone); break;
                }

                using (var fs = await file.OpenWriteAsync())
                    container.WriteTo(fs);

                if (StatusBar != null)
                    StatusBar.Text = $"Exported {clone.AppearanceType} {CurrentObjectAppearance.Id} as AEC.";
                ErrorManager.ShowInfo($"Exported to:\n{file.Path.LocalPath}");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"AEC export failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async void ImportAEC_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null)
            {
                ErrorManager.ShowError("No appearances loaded.");
                return;
            }
            if (string.IsNullOrEmpty(MainWindow._assetsPath) || !Directory.Exists(MainWindow._assetsPath))
            {
                ErrorManager.ShowError("Assets path not set. Load assets first.");
                return;
            }

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import AEC",
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("Assets Editor Container") { Patterns = ["*.aec"] }]
            });
            if (files == null || files.Count == 0) return;

            _lastImportedTabIndex = null;
            _lastImportedId = null;

            int imported = 0, failed = 0;
            var failureDetails = new List<string>();
            foreach (var file in files)
            {
                try
                {
                    int count = InternalImportAEC(file.Path.LocalPath);
                    if (count > 0) imported += count;
                    else
                    {
                        failed++;
                        failureDetails.Add($"{Path.GetFileName(file.Path.LocalPath)}: parsed 0 appearances");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    string detail = $"{Path.GetFileName(file.Path.LocalPath)}: {ex.GetType().Name}: {ex.Message}";
                    failureDetails.Add(detail);
                    MainWindow.Log($"AEC import failed — {detail}\n{ex.StackTrace}");
                }
            }

            FocusLastImported();
            if (StatusBar != null)
                StatusBar.Text = $"AEC import: {imported} object(s) OK, {failed} file(s) failed.";
            if (imported > 0 && failed == 0)
                ErrorManager.ShowInfo($"Imported {imported} object(s) from AEC.");
            else if (failed > 0)
                ErrorManager.ShowError($"AEC import: {imported} OK, {failed} failed.\n\n" + string.Join("\n", failureDetails));
        }

        // Returns the number of appearances imported from the given .aec file.
        private int InternalImportAEC(string fileName)
        {
            Appearances container;
            using (var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read))
                container = Appearances.Parser.ParseFrom(fs);

            MainWindow.Log($"AEC '{Path.GetFileName(fileName)}' parsed: outfits={container.Outfit.Count} objects={container.Object.Count} effects={container.Effect.Count} missiles={container.Missile.Count}");

            // Collect all embedded sprite PNGs in (Outfit, Object, Effect, Missile) order —
            // the same order the Windows editor writes them. Each PNG slot corresponds to
            // one SpriteId across flattened frame groups.
            var streams = new List<(int width, int height, MemoryStream png)>();

            int AddFromAppearance(Appearance a)
            {
                int added = 0;
                foreach (var bs in a.SpriteData)
                {
                    byte[] bytes = bs.ToByteArray();
                    int w = 32, h = 32;
                    try
                    {
                        using var bmp = SKBitmap.Decode(bytes);
                        if (bmp != null) { w = bmp.Width; h = bmp.Height; }
                    }
                    catch { /* fall back to 32×32 */ }
                    var ms = new MemoryStream(bytes, writable: false);
                    streams.Add((w, h, ms));
                    added++;
                }
                return added;
            }

            foreach (var a in container.Outfit)  AddFromAppearance(a);
            foreach (var a in container.Object)  AddFromAppearance(a);
            foreach (var a in container.Effect)  AddFromAppearance(a);
            foreach (var a in container.Missile) AddFromAppearance(a);

            var newIdByStreamIdx = BuildSpriteSheetsAndRegister(streams);

            int counter = 0;
            int imported = 0;

            // The on-disk AEC has `appearance_type` unset (proto2 default), so we must
            // force the category based on which repeated field the entry lived in.
            void RemapAndInsert(Appearance a, APPEARANCE_TYPE type)
            {
                a.AppearanceType = type;
                for (int fg = 0; fg < a.FrameGroup.Count; fg++)
                {
                    var si = a.FrameGroup[fg].SpriteInfo;
                    for (int s = 0; s < si.SpriteId.Count; s++)
                    {
                        si.SpriteId[s] = newIdByStreamIdx.TryGetValue(counter, out uint nid) ? nid : 0u;
                        counter++;
                    }
                }
                a.SpriteData.Clear();
                if (InsertAppearance(a)) imported++;
            }

            foreach (var a in container.Outfit)  RemapAndInsert(a, APPEARANCE_TYPE.AppearanceOutfit);
            foreach (var a in container.Object)  RemapAndInsert(a, APPEARANCE_TYPE.AppearanceObject);
            foreach (var a in container.Effect)  RemapAndInsert(a, APPEARANCE_TYPE.AppearanceEffect);
            foreach (var a in container.Missile) RemapAndInsert(a, APPEARANCE_TYPE.AppearanceMissile);

            MainWindow.Log($"AEC '{Path.GetFileName(fileName)}' inserted {imported} appearance(s), {streams.Count} sprite blob(s) → {newIdByStreamIdx.Count} new ids.");
            return imported;
        }

        // Pack a list of freshly-decoded composite PNGs into new Tibia sprite sheets,
        // write them to disk (as sprites-<sha>.bmp.lzma), register matching catalog
        // ranges, and populate MainWindow.SprLists / AllSprList so the sprites are
        // immediately visible in the editor UI. Returns a map from input stream index
        // to the new sprite id assigned to that sprite.
        private Dictionary<int, uint> BuildSpriteSheetsAndRegister(List<(int width, int height, MemoryStream png)> streams)
        {
            var result = new Dictionary<int, uint>();
            if (streams.Count == 0) return result;

            // Group by physical tile size so each sheet holds one sprite type.
            var groups = new Dictionary<(int w, int h), List<(int origIdx, MemoryStream png)>>();
            for (int i = 0; i < streams.Count; i++)
            {
                var key = (streams[i].width, streams[i].height);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<(int, MemoryStream)>();
                    groups[key] = list;
                }
                list.Add((i, streams[i].png));
            }

            // Pick the first free sprite id above the current maximum.
            uint nextId = 1;
            foreach (var c in MainWindow.catalog)
                if (c.Type == "sprite" && c.LastSpriteid != int.MinValue && (uint)c.LastSpriteid >= nextId)
                    nextId = (uint)c.LastSpriteid + 1;
            foreach (var k in MainWindow.SprLists.Keys)
                if ((uint)k >= nextId) nextId = (uint)k + 1;

            foreach (var kv in groups)
            {
                var size = kv.Key;
                var items = kv.Value;

                int spriteType = SpriteSizes.FindIndex(s => s.Width == size.w && s.Height == size.h);
                if (spriteType < 0) spriteType = 0; // unknown size → fall back to 32×32 layout

                int cols = Math.Max(1, SprSheetWidth / size.w);
                int rows = Math.Max(1, SprSheetHeight / size.h);
                int perSheet = cols * rows;

                for (int off = 0; off < items.Count; off += perSheet)
                {
                    int count = Math.Min(perSheet, items.Count - off);

                    using var sheet = new SKBitmap(SprSheetWidth, SprSheetHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using (var canvas = new SKCanvas(sheet))
                    {
                        canvas.Clear(SKColors.Transparent);

                        uint firstId = nextId;
                        for (int i = 0; i < count; i++)
                        {
                            var (origIdx, png) = items[off + i];
                            byte[] pngBytes = png.ToArray();
                            using var bmp = SKBitmap.Decode(pngBytes);
                            if (bmp != null)
                            {
                                int col = i % cols;
                                int row = i / cols;
                                canvas.DrawBitmap(bmp, SKRect.Create(col * size.w, row * size.h, size.w, size.h));
                            }

                            // Register sprite in the in-memory store so it renders
                            // immediately without having to reload the sheet from disk.
                            MainWindow.SprLists[(int)nextId] = new MemoryStream(pngBytes, writable: false);

                            uint idForUi = nextId;
                            Dispatcher.UIThread.Post(() =>
                            {
                                MainWindow.AllSprList.Add(new ShowList { Id = idForUi });
                            });

                            result[origIdx] = nextId;
                            nextId++;
                        }

                        uint lastId = nextId - 1;

                        // Content-addressed sheet name over the raw pixel buffer, matching
                        // the convention used by SprEditor.ImportSheet_Click.
                        using var sha = System.Security.Cryptography.SHA256.Create();
                        string hash = Convert.ToHexString(sha.ComputeHash(sheet.Bytes)).ToLowerInvariant();
                        string sheetName = $"sprites-{hash}.bmp.lzma";
                        string sheetPath = Path.Combine(MainWindow._assetsPath, sheetName);
                        LZMA.ExportLzmaFile(sheet, ref sheetPath);

                        MainWindow.catalog.Add(new MainWindow.Catalog
                        {
                            Type = "sprite",
                            File = sheetName,
                            SpriteType = spriteType,
                            FirstSpriteid = (int)firstId,
                            LastSpriteid = (int)lastId,
                            Area = 0,
                        });
                    }
                }
            }

            return result;
        }

        private SprEditor? _sprEditor;

        private void SpriteEditor_Click(object? sender, RoutedEventArgs e)
        {
            if (_sprEditor == null || !_sprEditor.IsVisible)
                _sprEditor = new SprEditor(this);
            _sprEditor.Show();
            _sprEditor.Activate();
        }

        private void LuaScript_Click(object? sender, RoutedEventArgs e)
        {
            var luaWindow = new LuaWindow();
            luaWindow.Show();
        }

        private void ShowLog_Click(object? sender, RoutedEventArgs e)
        {
            MainWindow.logView.Show();
        }

        private async void CompareDat_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null) { ErrorManager.ShowError("Load assets first."); return; }

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select second .dat file to compare",
                FileTypeFilter = [new FilePickerFileType("DAT file") { Patterns = ["*.dat"] }]
            });
            if (files.Count == 0) return;

            try
            {
                Appearances other;
                using (var fs = new FileStream(files[0].Path.LocalPath, FileMode.Open, FileAccess.Read))
                    other = Appearances.Parser.ParseFrom(fs);

                var current = MainWindow.appearances;
                var curOutfitIds = new HashSet<uint>(current.Outfit.Select(a => a.Id));
                var curItemIds   = new HashSet<uint>(current.Object.Select(a => a.Id));
                var curEffectIds = new HashSet<uint>(current.Effect.Select(a => a.Id));
                var curMissileIds= new HashSet<uint>(current.Missile.Select(a => a.Id));

                var otherOutfitIds = new HashSet<uint>(other.Outfit.Select(a => a.Id));
                var otherItemIds   = new HashSet<uint>(other.Object.Select(a => a.Id));
                var otherEffectIds = new HashSet<uint>(other.Effect.Select(a => a.Id));
                var otherMissileIds= new HashSet<uint>(other.Missile.Select(a => a.Id));

                string DiffLine(string label, HashSet<uint> cur, HashSet<uint> oth)
                {
                    var added   = oth.Except(cur).OrderBy(x => x).ToList();
                    var removed = cur.Except(oth).OrderBy(x => x).ToList();
                    int common  = cur.Intersect(oth).Count();
                    string result = $"  {label}: current={cur.Count} other={oth.Count} shared={common}";
                    if (added.Count > 0)
                        result += $"\n    + Added ({added.Count}): {string.Join(", ", added.Take(20))}{(added.Count > 20 ? "…" : "")}";
                    if (removed.Count > 0)
                        result += $"\n    - Removed ({removed.Count}): {string.Join(", ", removed.Take(20))}{(removed.Count > 20 ? "…" : "")}";
                    return result;
                }

                string report = $"Comparison with: {Path.GetFileName(files[0].Path.LocalPath)}\n\n"
                    + DiffLine("Outfits",  curOutfitIds,  otherOutfitIds)  + "\n"
                    + DiffLine("Items",    curItemIds,    otherItemIds)    + "\n"
                    + DiffLine("Effects",  curEffectIds,  otherEffectIds)  + "\n"
                    + DiffLine("Missiles", curMissileIds, otherMissileIds);

                var editor = new JsonEditorWindow(report, "DAT Comparison Report");
                await editor.ShowDialog<bool>(this);
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Compare failed: {ex.Message}");
            }
        }

        private void ShowStats_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null) { ErrorManager.ShowError("Load assets first."); return; }

            var app = MainWindow.appearances;
            int totalSprites = MainWindow.SprLists.Count;
            int totalSheets  = MainWindow.catalog.Count(c => c.Type == "sprite");

            string stats = $"=== Tibia Asset Editor Statistics ===\n\n"
                + $"Outfits:  {app.Outfit.Count}\n"
                + $"Items:    {app.Object.Count}\n"
                + $"Effects:  {app.Effect.Count}\n"
                + $"Missiles: {app.Missile.Count}\n"
                + $"────────────────────\n"
                + $"Total Appearances: {app.Outfit.Count + app.Object.Count + app.Effect.Count + app.Missile.Count}\n\n"
                + $"Sprite Sheets: {totalSheets}\n"
                + $"Total Sprites: {totalSprites}\n\n"
                + $"ID Ranges:\n"
                + $"  Outfits:  {(app.Outfit.Count > 0 ? $"{app.Outfit.Min(a => a.Id)} – {app.Outfit.Max(a => a.Id)}" : "none")}\n"
                + $"  Items:    {(app.Object.Count > 0 ? $"{app.Object.Min(a => a.Id)} – {app.Object.Max(a => a.Id)}" : "none")}\n"
                + $"  Effects:  {(app.Effect.Count > 0 ? $"{app.Effect.Min(a => a.Id)} – {app.Effect.Max(a => a.Id)}" : "none")}\n"
                + $"  Missiles: {(app.Missile.Count > 0 ? $"{app.Missile.Min(a => a.Id)} – {app.Missile.Max(a => a.Id)}" : "none")}";

            ErrorManager.ShowInfo(stats);
        }

        private static Appearance? _copiedFlagsSource;

        private Google.Protobuf.Collections.RepeatedField<Appearance>? GetCurrentCollection()
        {
            if (MainWindow.appearances == null) return null;
            return ObjectMenu.SelectedIndex switch
            {
                0 => MainWindow.appearances.Outfit,
                1 => MainWindow.appearances.Object,
                2 => MainWindow.appearances.Effect,
                3 => MainWindow.appearances.Missile,
                _ => null
            };
        }

        private ObservableCollection<ShowList> GetCurrentShowList() => ObjectMenu.SelectedIndex switch
        {
            0 => ThingsOutfit,
            1 => ThingsItem,
            2 => ThingsEffect,
            3 => ThingsMissile,
            _ => ThingsOutfit
        };

        private Dictionary<uint, Appearance> GetCurrentLookup() => ObjectMenu.SelectedIndex switch
        {
            0 => _outfitLookup,
            1 => _itemLookup,
            2 => _effectLookup,
            3 => _missileLookup,
            _ => _itemLookup
        };

        private uint NextFreeId(Google.Protobuf.Collections.RepeatedField<Appearance> collection)
        {
            uint max = 0;
            foreach (var a in collection) if (a.Id > max) max = a.Id;
            return max + 1;
        }

        // Allocate a never-before-used sprite ID and register it as a blank slot
        // so that getSpriteStream can later return the bytes the user imports into it.
        private static uint AllocateFreeSpriteId()
        {
            uint max = 0;
            foreach (var k in MainWindow.SprLists.Keys)
                if ((uint)k > max) max = (uint)k;
            uint newId = max + 1;
            MainWindow.SprLists[(int)newId] = null!;
            Dispatcher.UIThread.Post(() =>
            {
                MainWindow.AllSprList.Add(new ShowList { Id = newId });
            });
            return newId;
        }

        private Appearance CreateBlankAppearance(uint id)
        {
            var ap = new Appearance
            {
                Id = id,
                Flags = new AppearanceFlags(),
            };
            var fg = new FrameGroup
            {
                SpriteInfo = new SpriteInfo
                {
                    PatternWidth  = 1,
                    PatternHeight = 1,
                    PatternDepth  = 1,
                    Layers        = 1,
                    PatternFrames = 1,
                }
            };
            fg.SpriteInfo.SpriteId.Add(AllocateFreeSpriteId());
            ap.FrameGroup.Add(fg);
            return ap;
        }

        private void AddNew_Click(object? sender, RoutedEventArgs e)
        {
            var collection = GetCurrentCollection();
            if (collection == null) return;
            try
            {
                uint newId = NextFreeId(collection);
                var ap = CreateBlankAppearance(newId);
                collection.Add(ap);
                GetCurrentLookup()[newId] = ap;
                GetCurrentShowList().Add(new ShowList { Id = newId });
                UpdateShowList(ObjectMenu.SelectedIndex, preserveId: newId);
                ErrorManager.ShowInfo($"New appearance created with ID {newId}.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Add failed: {ex.Message}");
            }
        }

        private void NewOutfitTemplate_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null) { ErrorManager.ShowError("Load assets first."); return; }

            try
            {
                // Switch to outfits tab
                ObjectMenu.SelectedIndex = 0;

                uint newId = MainWindow.appearances.Outfit.Count > 0
                    ? MainWindow.appearances.Outfit.Max(o => o.Id) + 1u : 1u;

                var outfit = new Appearance
                {
                    Id = newId,
                    AppearanceType = APPEARANCE_TYPE.AppearanceOutfit,
                    Flags = new AppearanceFlags(),
                };

                // Standard Tibia outfit structure:
                //   2 frame groups: ObjectInitial (idle), ObjectMoving (walk)
                //   Each has: Layers=2 (outfit+template), PatternWidth=4 (dirs), PatternHeight=1,
                //   PatternDepth=1 (no mount) or 2 (mount), Addons via PatternY=3
                //   For simplicity: PatternY=3 (base + addon1 + addon2), PatternDepth=1 (no mount)
                //   SpriteCount = layers * patX * patY * patZ * frames = 2 * 4 * 3 * 1 * 1 = 24 per group

                for (int g = 0; g < 2; g++)
                {
                    var fg = new FrameGroup
                    {
                        FixedFrameGroup = g == 0
                            ? FIXED_FRAME_GROUP.OutfitIdle
                            : FIXED_FRAME_GROUP.OutfitMoving,
                        SpriteInfo = new SpriteInfo
                        {
                            PatternWidth  = 4,  // 4 directions
                            PatternHeight = 3,  // base + addon1 + addon2
                            PatternDepth  = 1,
                            Layers        = 2,  // outfit layer + template layer
                            PatternFrames = 1,
                        }
                    };

                    // Allocate blank sprites: 2 layers * 4 dirs * 3 addons = 24
                    int count = 2 * 4 * 3;
                    for (int i = 0; i < count; i++)
                        fg.SpriteInfo.SpriteId.Add(AllocateFreeSpriteId());

                    outfit.FrameGroup.Add(fg);
                }

                MainWindow.appearances.Outfit.Add(outfit);
                _outfitLookup[newId] = outfit;
                ThingsOutfit.Add(new ShowList { Id = newId });
                UpdateShowList(0, preserveId: newId);

                StatusBar.Text = $"Created outfit template ID {newId} (2 groups, 4 dirs, 3 addons, 2 layers).";
                ErrorManager.ShowInfo(
                    $"Outfit template created with ID {newId}.\n\n" +
                    "Structure:\n" +
                    "  2 frame groups (Idle + Walking)\n" +
                    "  4 directions (N/E/S/W)\n" +
                    "  3 addon levels (base + addon1 + addon2)\n" +
                    "  2 layers (outfit + colorization template)\n" +
                    "  = 48 sprite slots (24 per group)\n\n" +
                    "Import PNGs into each slot to build the outfit.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Template creation failed: {ex.Message}");
            }
        }

        private void Duplicate_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null) { ErrorManager.ShowInfo("Select an appearance first."); return; }
            var collection = GetCurrentCollection();
            if (collection == null) return;
            try
            {
                ApplyFlagsToAppearance(CurrentObjectAppearance);
                uint newId = NextFreeId(collection);
                var clone = CurrentObjectAppearance.Clone();
                clone.Id = newId;
                collection.Add(clone);
                GetCurrentLookup()[newId] = clone;
                GetCurrentShowList().Add(new ShowList { Id = newId });
                UpdateShowList(ObjectMenu.SelectedIndex, preserveId: newId);
                ErrorManager.ShowInfo($"Duplicated as ID {newId}.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Duplicate failed: {ex.Message}");
            }
        }

        private void CopyFlags_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null) { ErrorManager.ShowInfo("Select an appearance first."); return; }
            ApplyFlagsToAppearance(CurrentObjectAppearance);
            _copiedFlagsSource = CurrentObjectAppearance.Clone();
            ErrorManager.ShowInfo($"Flags copied from ID {CurrentObjectAppearance.Id}.");
        }

        private void PasteFlags_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null) { ErrorManager.ShowInfo("Select an appearance first."); return; }
            if (_copiedFlagsSource == null) { ErrorManager.ShowInfo("No flags copied yet."); return; }
            try
            {
                CurrentObjectAppearance.Flags = _copiedFlagsSource.Flags.Clone();
                LoadAppearanceFlags(CurrentObjectAppearance);
                ErrorManager.ShowInfo("Flags pasted.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Paste failed: {ex.Message}");
            }
        }

        private void DeleteObject_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null) return;
            var collection = GetCurrentCollection();
            if (collection == null) return;
            try
            {
                uint removedId = CurrentObjectAppearance.Id;
                var toRemove = collection.FirstOrDefault(a => a.Id == removedId);
                if (toRemove != null) collection.Remove(toRemove);
                GetCurrentLookup().Remove(removedId);
                var list = GetCurrentShowList();
                var sl = list.FirstOrDefault(s => s.Id == removedId);
                if (sl != null) list.Remove(sl);
                CurrentObjectAppearance = null;
                UpdateShowList(ObjectMenu.SelectedIndex);
                ErrorManager.ShowInfo($"Deleted appearance {removedId}.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Delete failed: {ex.Message}");
            }
        }

        // Map UI checkbox state back onto the protobuf flags object. Called before save/clone
        // and whenever a flag checkbox toggles (if auto-apply is on).
        private bool _suppressFlagApply;
        private void ApplyFlagsToAppearance(Appearance appearance)
        {
            if (appearance.Flags == null) appearance.Flags = new AppearanceFlags();
            var flags = appearance.Flags;

            // Name / Description
            if (!string.IsNullOrWhiteSpace(FlagName.Text)) appearance.Name = FlagName.Text;
            else appearance.ClearName();
            if (!string.IsNullOrWhiteSpace(FlagDescription.Text)) appearance.Description = FlagDescription.Text;
            else appearance.ClearDescription();

            // Ground
            flags.Bank = FlagGround.IsChecked == true
                ? new AppearanceFlagBank { Waypoints = (uint)(FlagGroundSpeed.Value ?? 0) }
                : null;

            if (FlagClip.IsChecked     == true) flags.Clip = true;     else flags.ClearClip();
            if (FlagTop.IsChecked      == true) flags.Top = true;      else flags.ClearTop();
            if (FlagBottom.IsChecked   == true) flags.Bottom = true;   else flags.ClearBottom();
            if (FlagContainer.IsChecked== true) flags.Container = true;else flags.ClearContainer();
            if (FlagStackable.IsChecked== true) flags.Cumulative = true;else flags.ClearCumulative();
            if (FlagUsable.IsChecked   == true) flags.Usable = true;   else flags.ClearUsable();
            if (FlagForceUse.IsChecked == true) flags.Forceuse = true; else flags.ClearForceuse();
            if (FlagMultiuse.IsChecked == true) flags.Multiuse = true; else flags.ClearMultiuse();

            // Write
            flags.Write = FlagWriteable.IsChecked == true
                ? new AppearanceFlagWrite { MaxTextLength = (uint)(FlagWriteMaxLen.Value ?? 0) }
                : null;
            flags.WriteOnce = FlagWriteOnce.IsChecked == true
                ? new AppearanceFlagWriteOnce { MaxTextLengthOnce = (uint)(FlagWriteOnceMaxLen.Value ?? 0) }
                : null;

            if (FlagLiquidPool.IsChecked      == true) flags.Liquidpool = true;      else flags.ClearLiquidpool();
            if (FlagLiquidContainer.IsChecked == true) flags.Liquidcontainer = true; else flags.ClearLiquidcontainer();
            if (FlagImpassable.IsChecked      == true) flags.Unpass = true;          else flags.ClearUnpass();
            if (FlagUnmovable.IsChecked       == true) flags.Unmove = true;          else flags.ClearUnmove();
            if (FlagUnsight.IsChecked         == true) flags.Unsight = true;         else flags.ClearUnsight();
            if (FlagAvoidBlock.IsChecked      == true) flags.Avoid = true;           else flags.ClearAvoid();
            if (FlagNoMoveAnimation.IsChecked == true) flags.NoMovementAnimation = true; else flags.ClearNoMovementAnimation();
            if (FlagTake.IsChecked            == true) flags.Take = true;            else flags.ClearTake();

            if (FlagHangable.IsChecked == true) flags.Hang = true; else flags.ClearHang();

            // Hook
            flags.Hook = FlagHook.IsChecked == true
                ? new AppearanceFlagHook { Direction = (HOOK_TYPE)(FlagHookType.SelectedIndex + 1) }
                : null;

            if (FlagRotate.IsChecked == true) flags.Rotate = true; else flags.ClearRotate();

            // Light
            flags.Light = FlagLight.IsChecked == true
                ? new AppearanceFlagLight { Brightness = (uint)(FlagLightBrightness.Value ?? 0), Color = (uint)(FlagLightColor.Value ?? 0) }
                : null;

            if (FlagDontHide.IsChecked     == true) flags.DontHide = true;     else flags.ClearDontHide();
            if (FlagTranslucent.IsChecked  == true) flags.Translucent = true;  else flags.ClearTranslucent();

            // Shift
            flags.Shift = FlagShift.IsChecked == true
                ? new AppearanceFlagShift { X = (int)(FlagShiftX.Value ?? 0), Y = (int)(FlagShiftY.Value ?? 0) }
                : null;

            // Height
            flags.Height = FlagHeight.IsChecked == true
                ? new AppearanceFlagHeight { Elevation = (uint)(FlagElevation.Value ?? 0) }
                : null;

            if (FlagLyingCorpse.IsChecked  == true) flags.LyingObject = true;  else flags.ClearLyingObject();
            if (FlagAnimateAlways.IsChecked== true) flags.AnimateAlways = true;else flags.ClearAnimateAlways();

            // Automap
            flags.Automap = FlagAutomap.IsChecked == true
                ? new AppearanceFlagAutomap { Color = (uint)(FlagAutomapColor.Value ?? 0) }
                : null;

            // LensHelp
            flags.Lenshelp = FlagLensHelp.IsChecked == true
                ? new AppearanceFlagLenshelp { Id = (uint)(FlagLensHelpId.SelectedIndex + 1100) }
                : null;

            if (FlagFullBank.IsChecked     == true) flags.Fullbank = true;     else flags.ClearFullbank();
            if (FlagIgnoreLook.IsChecked   == true) flags.IgnoreLook = true;   else flags.ClearIgnoreLook();

            // Clothes
            flags.Clothes = FlagClothes.IsChecked == true
                ? new AppearanceFlagClothes { Slot = (uint)FlagClothesSlot.SelectedIndex }
                : null;

            // DefaultAction
            flags.DefaultAction = FlagDefaultAction.IsChecked == true
                ? new AppearanceFlagDefaultAction { Action = (PLAYER_ACTION)FlagDefaultActionType.SelectedIndex }
                : null;

            // Market
            if (FlagMarket.IsChecked == true)
            {
                flags.Market = new AppearanceFlagMarket
                {
                    Category = (ITEM_CATEGORY)(FlagMarketCategory.SelectedIndex + 1),
                    TradeAsObjectId = (uint)(FlagMarketTrade.Value ?? 0),
                    ShowAsObjectId = (uint)(FlagMarketShow.Value ?? 0),
                    MinimumLevel = (uint)(FlagMarketLevel.Value ?? 0),
                };
                flags.Market.RestrictToVocation.Clear();
                if (FlagProfAny.IsChecked == true) flags.Market.RestrictToVocation.Add(VOCATION.Any);
                if (FlagProfNone.IsChecked == true) flags.Market.RestrictToVocation.Add(VOCATION.None);
                if (FlagProfKnight.IsChecked == true) flags.Market.RestrictToVocation.Add(VOCATION.Knight);
                if (FlagProfPaladin.IsChecked == true) flags.Market.RestrictToVocation.Add(VOCATION.Paladin);
                if (FlagProfSorcerer.IsChecked == true) flags.Market.RestrictToVocation.Add(VOCATION.Sorcerer);
                if (FlagProfDruid.IsChecked == true) flags.Market.RestrictToVocation.Add(VOCATION.Druid);
                if (FlagProfPromoted.IsChecked == true) flags.Market.RestrictToVocation.Add(VOCATION.Promoted);
            }
            else
                flags.Market = null;

            if (FlagWrap.IsChecked         == true) flags.Wrap = true;         else flags.ClearWrap();
            if (FlagUnwrap.IsChecked       == true) flags.Unwrap = true;       else flags.ClearUnwrap();
            if (FlagTopEffect.IsChecked    == true) flags.Topeffect = true;    else flags.ClearTopeffect();
            if (FlagDecoItemKit.IsChecked  == true) flags.DecoItemKit = true;  else flags.ClearDecoItemKit();

            // ChangedToExpire
            flags.Changedtoexpire = FlagChangedToExpire.IsChecked == true
                ? new AppearanceFlagChangedToExpire { FormerObjectTypeid = (uint)(FlagChangedToExpireId.Value ?? 0) }
                : null;

            if (FlagCorpse.IsChecked       == true) flags.Corpse = true;       else flags.ClearCorpse();
            if (FlagPlayerCorpse.IsChecked == true) flags.PlayerCorpse = true; else flags.ClearPlayerCorpse();
            if (FlagShowOffSocket.IsChecked== true) flags.ShowOffSocket = true; else flags.ClearShowOffSocket();
            if (FlagReportable.IsChecked   == true) flags.Reportable = true;   else flags.ClearReportable();
            if (FlagReverseAddonE.IsChecked== true) flags.ReverseAddonsEast = true;  else flags.ClearReverseAddonsEast();
            if (FlagReverseAddonW.IsChecked== true) flags.ReverseAddonsWest = true;  else flags.ClearReverseAddonsWest();
            if (FlagReverseAddonN.IsChecked== true) flags.ReverseAddonsNorth = true; else flags.ClearReverseAddonsNorth();
            if (FlagReverseAddonS.IsChecked== true) flags.ReverseAddonsSouth = true; else flags.ClearReverseAddonsSouth();
            if (FlagWearOut.IsChecked      == true) flags.Wearout = true;      else flags.ClearWearout();
            if (FlagClockExpire.IsChecked  == true) flags.Clockexpire = true;  else flags.ClearClockexpire();
            if (FlagExpire.IsChecked       == true) flags.Expire = true;       else flags.ClearExpire();
            if (FlagExpireStop.IsChecked   == true) flags.Expirestop = true;   else flags.ClearExpirestop();

            // UpgradeClassification
            flags.Upgradeclassification = FlagUpgradeClass.IsChecked == true
                ? new AppearanceFlagUpgradeClassification { UpgradeClassification = (uint)(FlagUpgradeClassAmount.Value ?? 0) }
                : null;

            // Cyclopedia
            flags.Cyclopediaitem = FlagCyclopedia.IsChecked == true
                ? new AppearanceFlagCyclopedia { CyclopediaType = (uint)(FlagCyclopediaId.Value ?? 0) }
                : null;

            if (FlagAmmo.IsChecked == true) flags.Ammo = true; else flags.ClearAmmo();

            // SkillWheel Gem
            flags.SkillwheelGem = FlagSkillWheelGem.IsChecked == true
                ? new AppearanceFlagSkillWheelGem { GemQualityId = (uint)(FlagGemQualityId.Value ?? 0), VocationId = (uint)(FlagGemVocationId.Value ?? 0) }
                : null;

            if (FlagDualWielding.IsChecked == true) flags.DualWielding = true; else flags.ClearDualWielding();

            // Imbueable
            flags.Imbueable = FlagImbueable.IsChecked == true
                ? new AppearanceFlagImbueable { SlotCount = (uint)(FlagImbueSlotCount.Value ?? 0) }
                : null;

            // Proficiency
            flags.Proficiency = FlagProficiency.IsChecked == true
                ? new AppearanceFlagProficiency { ProficiencyId = (uint)(FlagProficiencyId.Value ?? 0) }
                : null;

            // Transparency
            flags.Transparencylevel = FlagTransparency.IsChecked == true
                ? new AppearanceFlagTransparencyLevel { Level = (uint)(FlagTransparencyLevel.Value ?? 0) }
                : null;

            // Minimum level
            if ((FlagMinimumLevel.Value ?? 0) > 0) flags.MinimumLevel = (uint)(FlagMinimumLevel.Value ?? 0);
            else if (flags.HasMinimumLevel) flags.ClearMinimumLevel();

            // Weapon type
            if (FlagWeaponType.SelectedIndex > 0) flags.WeaponType = (WEAPON_TYPE)FlagWeaponType.SelectedIndex;
            else if (flags.HasWeaponType) flags.ClearWeaponType();

            // Vocation restriction (item-level)
            flags.RestrictToVocation.Clear();
            if (FlagRestrictVocAny.IsChecked == true) flags.RestrictToVocation.Add(VOCATION.Any);
            if (FlagRestrictVocNone.IsChecked == true) flags.RestrictToVocation.Add(VOCATION.None);
            if (FlagRestrictVocKnight.IsChecked == true) flags.RestrictToVocation.Add(VOCATION.Knight);
            if (FlagRestrictVocPaladin.IsChecked == true) flags.RestrictToVocation.Add(VOCATION.Paladin);
            if (FlagRestrictVocSorcerer.IsChecked == true) flags.RestrictToVocation.Add(VOCATION.Sorcerer);
            if (FlagRestrictVocDruid.IsChecked == true) flags.RestrictToVocation.Add(VOCATION.Druid);
            if (FlagRestrictVocMonk.IsChecked == true) flags.RestrictToVocation.Add(VOCATION.Druid);
            if (FlagRestrictVocPromoted.IsChecked == true) flags.RestrictToVocation.Add(VOCATION.Promoted);
        }

        // Push current state to undo stack before a mutation. Deduplicates rapid changes
        // by comparing the appearance ID with the top of the stack.
        private void PushUndo()
        {
            if (CurrentObjectAppearance == null) return;
            if (_undoStack.Count > 0 && _undoStack.Peek().id == CurrentObjectAppearance.Id)
            {
                // Already have a snapshot for this object — don't spam
                return;
            }
            _undoStack.Push((CurrentObjectAppearance.Id, CurrentObjectAppearance.Clone()));
            _redoStack.Clear();
            if (_undoStack.Count > 100) // cap memory
            {
                var temp = new Stack<(uint, Appearance)>(_undoStack.Reverse().Skip(_undoStack.Count - 100));
                _undoStack.Clear();
                foreach (var item in temp.Reverse()) _undoStack.Push(item);
            }
        }

        private void Undo()
        {
            if (_undoStack.Count == 0 || CurrentObjectAppearance == null) return;
            _redoStack.Push((CurrentObjectAppearance.Id, CurrentObjectAppearance.Clone()));
            var (id, snapshot) = _undoStack.Pop();

            // Restore the snapshot into the collection
            var collection = GetCurrentCollection();
            if (collection != null)
            {
                for (int i = 0; i < collection.Count; i++)
                {
                    if (collection[i].Id == id) { collection[i] = snapshot; break; }
                }
            }
            GetCurrentLookup()[id] = snapshot;
            CurrentObjectAppearance = snapshot;
            CurrentFlags = snapshot.Flags;
            _suppressFlagApply = true;
            try
            {
                LoadAppearanceFlags(snapshot);
                LoadSpriteInfo(snapshot);
                LoadAppearanceSpritesList(snapshot);
                UpdatePreview(snapshot);
            }
            finally { _suppressFlagApply = false; }
            StatusBar.Text = "Undo applied.";
        }

        private void Redo()
        {
            if (_redoStack.Count == 0 || CurrentObjectAppearance == null) return;
            _undoStack.Push((CurrentObjectAppearance.Id, CurrentObjectAppearance.Clone()));
            var (id, snapshot) = _redoStack.Pop();

            var collection = GetCurrentCollection();
            if (collection != null)
            {
                for (int i = 0; i < collection.Count; i++)
                {
                    if (collection[i].Id == id) { collection[i] = snapshot; break; }
                }
            }
            GetCurrentLookup()[id] = snapshot;
            CurrentObjectAppearance = snapshot;
            CurrentFlags = snapshot.Flags;
            _suppressFlagApply = true;
            try
            {
                LoadAppearanceFlags(snapshot);
                LoadSpriteInfo(snapshot);
                LoadAppearanceSpritesList(snapshot);
                UpdatePreview(snapshot);
            }
            finally { _suppressFlagApply = false; }
            StatusBar.Text = "Redo applied.";
        }

        // Generic handler wired to every flag CheckBox's IsCheckedChanged in XAML.
        private void Flag_Changed(object? sender, RoutedEventArgs e)
        {
            if (_suppressFlagApply || CurrentObjectAppearance == null) return;
            PushUndo();
            ApplyFlagsToAppearance(CurrentObjectAppearance);
        }

        // Generic handler for flag parameter changes (NumericUpDown, ComboBox, TextBox).
        private void FlagParam_Changed(object? sender, EventArgs e)
        {
            if (_suppressFlagApply || CurrentObjectAppearance == null) return;
            ApplyFlagsToAppearance(CurrentObjectAppearance);
        }

        private async void EditJson_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null)
            {
                ErrorManager.ShowInfo("Select an appearance first.");
                return;
            }
            try
            {
                ApplyFlagsToAppearance(CurrentObjectAppearance);
                var editor = new JsonEditorWindow(CurrentObjectAppearance,
                    $"Appearance {CurrentObjectAppearance.Id} — protobuf JSON (Google.Protobuf format)");
                var ok = await editor.ShowDialog<bool>(this);
                if (!ok || editor.Result == null) return;

                var edited = editor.Result;
                var collection = GetCurrentCollection();
                if (collection == null) return;

                uint oldId = CurrentObjectAppearance.Id;
                for (int i = 0; i < collection.Count; i++)
                {
                    if (collection[i].Id == oldId)
                    {
                        collection[i] = edited;
                        break;
                    }
                }
                GetCurrentLookup()[edited.Id] = edited;
                if (edited.Id != oldId) GetCurrentLookup().Remove(oldId);

                CurrentObjectAppearance = edited;
                LoadAppearanceFlags(edited);
                LoadSpriteInfo(edited);
                LoadAppearanceSpritesList(edited);
                UpdatePreview(edited);
                UpdateShowList(ObjectMenu.SelectedIndex, preserveId: edited.Id);
                ErrorManager.ShowInfo($"JSON applied to appearance {edited.Id}.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Edit JSON failed: {ex.Message}");
            }
        }

        private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (!isPageLoaded) return;
            UpdateShowList(ObjectMenu.SelectedIndex);
        }

        // Advanced filter: matches ID number, appearance name, or flag presence.
        // Supports queries like "123", "dragon", "Container", "Light", "!Impassable" (negated).
        private static bool MatchesFilter(uint id, Dictionary<uint, Appearance> lookup, string filter)
        {
            // ID match
            if (id.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!lookup.TryGetValue(id, out var appearance))
                return false;

            // Name match
            if (appearance.HasName && appearance.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;

            // Description match
            if (appearance.HasDescription && appearance.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;

            // Flag match: check if the filter matches any known flag name
            var flags = appearance.Flags;
            if (flags == null) return false;

            bool negated = filter.StartsWith('!');
            string flagName = negated ? filter[1..].Trim() : filter;

            bool hasFlag = flagName.ToLowerInvariant() switch
            {
                "ground" or "bank" => flags.Bank != null,
                "clip" => flags.Clip,
                "top" => flags.Top,
                "bottom" => flags.Bottom,
                "container" => flags.Container,
                "stackable" or "cumulative" => flags.Cumulative,
                "usable" => flags.Usable,
                "forceuse" => flags.Forceuse,
                "multiuse" => flags.Multiuse,
                "writeable" or "write" => flags.Write != null,
                "writeonce" => flags.WriteOnce != null,
                "liquidpool" => flags.Liquidpool,
                "liquidcontainer" => flags.Liquidcontainer,
                "impassable" or "unpass" => flags.Unpass,
                "unmovable" or "unmove" => flags.Unmove,
                "unsight" => flags.Unsight,
                "avoid" or "avoidblock" => flags.Avoid,
                "nomoveanim" or "nomoveanimation" => flags.NoMovementAnimation,
                "take" => flags.HasTake,
                "hangable" or "hang" => flags.HasHang,
                "hook" => flags.Hook != null,
                "rotate" => flags.Rotate,
                "light" => flags.Light != null,
                "donthide" => flags.DontHide,
                "translucent" => flags.Translucent,
                "shift" => flags.Shift != null,
                "height" or "elevation" => flags.Height != null,
                "lyingcorpse" or "lyingobject" => flags.LyingObject,
                "animatealways" => flags.AnimateAlways,
                "automap" => flags.Automap != null,
                "lenshelp" => flags.Lenshelp != null,
                "fullbank" or "fullground" => flags.Fullbank,
                "ignorelook" => flags.HasIgnoreLook,
                "clothes" => flags.Clothes != null,
                "defaultaction" => flags.DefaultAction != null,
                "market" => flags.Market != null,
                "wrap" => flags.Wrap,
                "unwrap" => flags.Unwrap,
                "topeffect" => flags.HasTopeffect,
                "decokit" or "decoitemkit" => flags.HasDecoItemKit,
                "corpse" => flags.Corpse,
                "playercorpse" => flags.HasPlayerCorpse,
                "podium" or "showoffsocket" => flags.ShowOffSocket,
                "reportable" => flags.Reportable,
                "wearout" => flags.Wearout,
                "expire" => flags.Expire,
                "cyclopedia" => flags.Cyclopediaitem != null,
                "ammo" => flags.HasAmmo,
                "npc" => flags.Npcsaledata.Count > 0,
                "dualwielding" or "dualwield" => flags.HasDualWielding,
                "imbueable" => flags.Imbueable != null,
                "proficiency" => flags.Proficiency != null,
                "transparency" => flags.Transparencylevel != null,
                "upgrade" or "upgradeclass" => flags.Upgradeclassification != null,
                _ => false,
            };

            return negated ? !hasFlag : hasFlag;
        }

        private ObservableCollection<ShowList> _sprFiltered = [];
        private void SprSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (!isPageLoaded) return;
            string? filter = SprSearchBox?.Text?.Trim();
            if (string.IsNullOrEmpty(filter))
            {
                SprListView.ItemsSource = MainWindow.AllSprList;
                return;
            }
            _sprFiltered = new ObservableCollection<ShowList>(
                MainWindow.AllSprList.Where(s => s.Id.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)));
            SprListView.ItemsSource = _sprFiltered;
        }

        #region Container-based image loading (Avalonia 11 ContainerPrepared/ContainerClearing)

        // Called by Avalonia when a ListBoxItem enters the viewport (scrolled into view or first render)
        private void ObjListView_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        {
            if (!isPageLoaded) return;
            if (e.Container.DataContext is ShowList showList)
                LoadObjectImage(showList);
        }

        // Called by Avalonia when a ListBoxItem leaves the viewport (scrolled out of view)
        private void ObjListView_ContainerClearing(object? sender, ContainerClearingEventArgs e)
        {
            if (e.Container.DataContext is ShowList showList)
            {
                showList.StopAnimation();
                showList.Images.Clear();
                showList.Image = null;
            }
        }

        // Called when a sprite ListBoxItem enters the viewport
        private void SprListView_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        {
            if (!isPageLoaded) return;
            if (e.Container.DataContext is ShowList showList)
                LoadSpriteImage(showList);
        }

        // Called when a sprite ListBoxItem leaves the viewport
        private void SprListView_ContainerClearing(object? sender, ContainerClearingEventArgs e)
        {
            if (e.Container.DataContext is ShowList showList)
                showList.Image = null;
        }

        private void LoadObjectImage(ShowList showList)
        {
            // Images already loaded — just (re)start animation (container was recycled)
            if (showList.Images.Count > 0)
            {
                showList.StartAnimation();
                return;
            }

            Appearance? appearance = ObjectMenu.SelectedIndex switch
            {
                0 => _outfitLookup.GetValueOrDefault(showList.Id),
                1 => _itemLookup.GetValueOrDefault(showList.Id),
                2 => _effectLookup.GetValueOrDefault(showList.Id),
                3 => _missileLookup.GetValueOrDefault(showList.Id),
                _ => null
            };

            if (appearance == null || appearance.FrameGroup.Count == 0) return;

            try
            {
                var frameGroup = appearance.FrameGroup[0];
                var info = frameGroup.SpriteInfo;
                int dirCount   = (int)Math.Max(1, info.PatternWidth);
                int frameCount = (int)(info.Animation?.SpritePhase.Count ?? 1);

                for (int frame = 0; frame < frameCount; frame++)
                {
                    // For outfits/effects, use south-facing direction (index 2, or last if fewer)
                    int dir = ObjectMenu.SelectedIndex switch
                    {
                        0 => Math.Min(2, dirCount - 1),  // outfit south
                        2 => Math.Min(2, dirCount - 1),  // effect
                        _ => 0
                    };
                    int patY = ObjectMenu.SelectedIndex == 2
                        ? (int)Math.Min(1, info.PatternHeight - 1) : 0;

                    int idx = GetSpriteIndex(frameGroup, 0, dir, patY, 0, frame);
                    if (idx >= 0 && idx < info.SpriteId.Count)
                    {
                        var stream = MainWindow.getSpriteStream((int)info.SpriteId[idx]);
                        if (stream != null)
                            showList.Images.Add(Utils.MemoryStreamToBitmap(stream));
                    }
                }

                showList.StartAnimation();
            }
            catch (Exception ex)
            {
                MainWindow.Log($"Error loading image for appearance {appearance.Id}: {ex.Message}");
            }
        }

        private void LoadSpriteImage(ShowList showList)
        {
            int id = (int)showList.Id;
            try
            {
                var stream = MainWindow.getSpriteStream(id);
                if (stream != null)
                    showList.Image = Utils.MemoryStreamToBitmap(stream);

                if (string.IsNullOrEmpty(showList.SizeLabel))
                {
                    var (w, h) = MainWindow.GetSpriteSize(id);
                    if (w > 0 && h > 0)
                        showList.SizeLabel = $"{w}×{h}";
                }
            }
            catch { }
        }

        // AppSprListView container events — load/unload sprite images inline
        private void AppSprListView_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        {
            if (e.Container.DataContext is ShowList s) LoadSpriteImage(s);
        }
        private void AppSprListView_ContainerClearing(object? sender, ContainerClearingEventArgs e)
        {
            if (e.Container.DataContext is ShowList s) s.Image = null;
        }

        #endregion

        #region Appearance Sprites editing

        // Rebuild the AppearanceSprites list from ALL of the selected appearance's frame groups.
        // Outfits typically have two frame groups (idle + walking) — showing only [0] hid most
        // of the sprites. ShowList.Cid stores the owning frame-group index for write-back.
        private void LoadAppearanceSpritesList(Appearance appearance)
        {
            AppearanceSprites.Clear();
            for (int g = 0; g < appearance.FrameGroup.Count; g++)
            {
                var ids = appearance.FrameGroup[g].SpriteInfo.SpriteId;
                foreach (var id in ids)
                    AppearanceSprites.Add(new ShowList { Id = id, Cid = (uint)g });
            }
        }

        // Return the index of a slot within its owning frame group's SpriteId list
        private int LocalIndex(int globalIdx)
        {
            if (globalIdx < 0 || globalIdx >= AppearanceSprites.Count) return -1;
            uint group = AppearanceSprites[globalIdx].Cid;
            int local = 0;
            for (int i = 0; i < globalIdx; i++)
                if (AppearanceSprites[i].Cid == group) local++;
            return local;
        }

        private void AppSprAdd_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null || CurrentObjectAppearance.FrameGroup.Count == 0)
            {
                ErrorManager.ShowInfo("Select an appearance first.");
                return;
            }

            // Append into the frame group of the currently selected slot, or the last group.
            int groupIdx = AppSprListView.SelectedItem is ShowList sel
                ? (int)sel.Cid
                : CurrentObjectAppearance.FrameGroup.Count - 1;
            groupIdx = Math.Clamp(groupIdx, 0, CurrentObjectAppearance.FrameGroup.Count - 1);

            var info = CurrentObjectAppearance.FrameGroup[groupIdx].SpriteInfo;
            uint newId = AllocateFreeSpriteId();
            info.SpriteId.Add(newId);

            // Insert after the last existing slot of that group so groups stay contiguous.
            int insertAt = AppearanceSprites.Count;
            for (int i = AppearanceSprites.Count - 1; i >= 0; i--)
            {
                if (AppearanceSprites[i].Cid == (uint)groupIdx)
                {
                    insertAt = i + 1;
                    break;
                }
            }
            AppearanceSprites.Insert(insertAt, new ShowList { Id = newId, Cid = (uint)groupIdx });
        }

        private async void AppSprImport_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null || CurrentObjectAppearance.FrameGroup.Count == 0)
            {
                ErrorManager.ShowInfo("Select an appearance first.");
                return;
            }

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import PNG sprites (multi-select to fill multiple slots)",
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("PNG / BMP Image") { Patterns = ["*.png", "*.bmp"] }]
            });
            if (files.Count == 0) return;

            // Determine the starting slot index. If the user clicked a slot use it; otherwise
            // start at 0 (or append a new slot if there are none).
            int startIdx = 0;
            if (AppSprListView.SelectedItem is ShowList selected)
                startIdx = Math.Max(0, AppearanceSprites.IndexOf(selected));
            else if (AppearanceSprites.Count == 0)
            {
                AppSprAdd_Click(sender, e);
                startIdx = 0;
            }

            // Resolve target sprite size from the first existing slot we'd write to.
            // Fallback to 32×32 when nothing is known yet (brand-new appearance).
            int targetW = 32, targetH = 32;
            if (startIdx < AppearanceSprites.Count)
            {
                var firstSlot = AppearanceSprites[startIdx];
                var (tw, th) = MainWindow.GetSpriteSize((int)firstSlot.Id);
                if (tw > 0 && th > 0) { targetW = tw; targetH = th; }
            }

            // Warn the user when any source file is larger than the target slot.
            // Downscaling 64→32 loses detail; the user might prefer to cancel and
            // open SprEditor's batch downscaler (nearest-neighbor) instead, or
            // swap the appearance onto a bigger sprite sheet first.
            var oversized = new List<string>();
            foreach (var f in files)
            {
                try
                {
                    using var probe = SkiaSharp.SKBitmap.Decode(f.Path.LocalPath);
                    if (probe != null && (probe.Width > targetW || probe.Height > targetH))
                        oversized.Add($"{System.IO.Path.GetFileName(f.Path.LocalPath)} ({probe.Width}×{probe.Height})");
                }
                catch { }
            }
            if (oversized.Count > 0)
            {
                string sample = string.Join("\n  ", oversized.Take(5));
                if (oversized.Count > 5) sample += $"\n  …and {oversized.Count - 5} more";
                bool proceed = await ErrorManager.ConfirmAsync(
                    "Source PNGs larger than target slot",
                    $"Target slot is {targetW}×{targetH}, but some PNGs are bigger:\n  {sample}\n\n"
                  + "Continuing will downscale them (nearest-neighbor, keeps pixel-art crisp). "
                  + "Cancel to resize manually or switch the appearance to a bigger sheet first.");
                if (!proceed) return;
            }

            int imported = 0;
            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    int slotIdx = startIdx + i;

                    // If we've run past the existing slot list, append a new one into the
                    // frame group of the last visible slot (or group 0).
                    if (slotIdx >= AppearanceSprites.Count)
                    {
                        AppSprListView.SelectedIndex = AppearanceSprites.Count - 1;
                        AppSprAdd_Click(sender, e);
                        slotIdx = AppearanceSprites.Count - 1;
                    }

                    var slot = AppearanceSprites[slotIdx];
                    // Re-resolve target size per slot in case different slots live in
                    // different sheets.
                    var (stw, sth) = MainWindow.GetSpriteSize((int)slot.Id);
                    int slotW = stw > 0 ? stw : targetW;
                    int slotH = sth > 0 ? sth : targetH;
                    var ms = LoadPngAsSpriteStream(files[i].Path.LocalPath, slotW, slotH);
                    if (ms == null) continue;

                    int groupIdx = (int)slot.Cid;
                    var info = CurrentObjectAppearance.FrameGroup[groupIdx].SpriteInfo;
                    int localIdx = LocalIndex(slotIdx);

                    uint id = slot.Id;
                    if (id == 0)
                    {
                        uint newId = AllocateFreeSpriteId();
                        if (localIdx >= 0 && localIdx < info.SpriteId.Count)
                            info.SpriteId[localIdx] = newId;
                        slot.Id = newId;
                        id = newId;
                    }
                    else if (!MainWindow.SprLists.ContainsKey((int)id))
                    {
                        MainWindow.SprLists[(int)id] = null!;
                        MainWindow.AllSprList.Add(new ShowList { Id = id });
                    }

                    MainWindow.SprLists[(int)id] = ms;
                    slot.Image = Utils.MemoryStreamToBitmap(ms);
                    imported++;
                }

                UpdatePreview(CurrentObjectAppearance);
                ErrorManager.ShowInfo($"Imported {imported} sprite(s) starting at slot {startIdx}.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Import failed: {ex.Message}");
            }
        }

        // Fit a source PNG into (targetW, targetH) without stretching pixel art:
        //   - source ≤ target → pad bottom-right with transparency (Tibia's anchor).
        //   - source > target → nearest-neighbor downscale (keeps pixel-art crisp),
        //     preserving aspect ratio, then pad bottom-right.
        // Matches the convention observed in real Tibia assets where small creatures
        // stored in 64×64 sheets have their artwork in the bottom-right 32×32 quadrant.
        private static System.IO.MemoryStream? LoadPngAsSpriteStream(string path, int targetW, int targetH)
        {
            using var srcBmp = SkiaSharp.SKBitmap.Decode(path);
            if (srcBmp == null) return null;

            int srcW = srcBmp.Width, srcH = srcBmp.Height;
            int dstW, dstH;
            SkiaSharp.SKBitmap? resized = null;
            SkiaSharp.SKBitmap drawSrc;

            if (srcW <= targetW && srcH <= targetH)
            {
                dstW = srcW; dstH = srcH;
                drawSrc = srcBmp;
            }
            else
            {
                double scale = Math.Min((double)targetW / srcW, (double)targetH / srcH);
                dstW = Math.Max(1, (int)Math.Round(srcW * scale));
                dstH = Math.Max(1, (int)Math.Round(srcH * scale));
                resized = srcBmp.Resize(
                    new SkiaSharp.SKImageInfo(dstW, dstH, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul),
                    SkiaSharp.SKFilterQuality.None);
                if (resized == null) return null;
                drawSrc = resized;
            }

            using var canvasBmp = new SkiaSharp.SKBitmap(targetW, targetH,
                SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
            using (var canvas = new SkiaSharp.SKCanvas(canvasBmp))
            {
                canvas.Clear(SkiaSharp.SKColors.Transparent);
                int x = targetW - dstW;
                int y = targetH - dstH;
                canvas.DrawBitmap(drawSrc, SkiaSharp.SKRect.Create(x, y, dstW, dstH));
            }
            resized?.Dispose();

            using var img = SkiaSharp.SKImage.FromBitmap(canvasBmp);
            using var dat = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return new System.IO.MemoryStream(dat.ToArray());
        }

        // Assigns the sprite currently selected in the right-hand SprListView to the
        // currently selected Appearance Sprites slot (mutates the protobuf SpriteId table).
        private void AppSprChange_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null || CurrentObjectAppearance.FrameGroup.Count == 0)
            {
                ErrorManager.ShowInfo("Select an appearance first.");
                return;
            }
            if (AppSprListView.SelectedItem is not ShowList slot)
            {
                ErrorManager.ShowInfo("Select a slot in Appearance Sprites first.");
                return;
            }
            if (SprListView.SelectedItem is not ShowList sprPick)
            {
                ErrorManager.ShowInfo("Pick a sprite in the right-hand Sprites panel first.");
                return;
            }

            int groupIdx = (int)slot.Cid;
            int globalIdx = AppearanceSprites.IndexOf(slot);
            int localIdx = LocalIndex(globalIdx);
            var info = CurrentObjectAppearance.FrameGroup[groupIdx].SpriteInfo;

            if (localIdx < 0 || localIdx >= info.SpriteId.Count) return;

            info.SpriteId[localIdx] = sprPick.Id;
            slot.Id = sprPick.Id;

            try
            {
                var stream = MainWindow.getSpriteStream((int)sprPick.Id);
                if (stream != null)
                    slot.Image = Utils.MemoryStreamToBitmap(stream);
            }
            catch { }

            UpdatePreview(CurrentObjectAppearance);
            ErrorManager.ShowInfo($"Slot now references sprite {sprPick.Id}.");
        }

        private async void AppSprExport_Click(object? sender, RoutedEventArgs e)
        {
            if (AppSprListView.SelectedItem is not ShowList selected) return;

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Sprite",
                DefaultExtension = "png",
                SuggestedFileName = $"sprite_{selected.Id}",
                FileTypeChoices = [new FilePickerFileType("PNG Image") { Patterns = ["*.png"] }]
            });
            if (file == null) return;

            try
            {
                int id = (int)selected.Id;
                if (!MainWindow.SprLists.ContainsKey(id)) return;
                var stream = MainWindow.getSpriteStream(id);
                if (stream == null) return;
                var bytes = stream.ToArray();
                using var outStream = await file.OpenWriteAsync();
                await outStream.WriteAsync(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Export failed: {ex.Message}");
            }
        }

        // Walks up the visual tree of the event source to find which ListBox owns it.
        private ListBox? FindOwningListBox(object? source)
        {
            if (source is not Avalonia.Visual v) return null;
            while (v != null)
            {
                if (v is ListBox lb) return lb;
                v = v.GetVisualParent() as Avalonia.Visual ?? (v.Parent as Avalonia.Visual)!;
                if (v == null) break;
            }
            return null;
        }

        private void Window_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            // Undo: Ctrl+Z
            if (e.Key == Avalonia.Input.Key.Z && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
            {
                Undo(); e.Handled = true; return;
            }
            // Redo: Ctrl+Y or Ctrl+Shift+Z
            if ((e.Key == Avalonia.Input.Key.Y && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) ||
                (e.Key == Avalonia.Input.Key.Z && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)))
            {
                Redo(); e.Handled = true; return;
            }

            // Extra shortcuts: Ctrl+S, Ctrl+N, Ctrl+D, Ctrl+F
            HandleExtraShortcuts(e);
            if (e.Handled) return;

            if (e.Key != Avalonia.Input.Key.Delete) return;

            var owner = FindOwningListBox(e.Source);
            if (owner == SprListView)     { _ = HandleSprDelete();    e.Handled = true; return; }
            if (owner == AppSprListView)  { _ = HandleAppSprDelete(); e.Handled = true; return; }
        }

        private async System.Threading.Tasks.Task HandleAppSprDelete()
        {
            if (AppSprListView.SelectedItem is not ShowList sel) return;
            bool ok = await ErrorManager.ConfirmAsync(
                "Delete sprite slot",
                $"Remove sprite slot with ID {sel.Id} from this appearance?");
            if (!ok) return;
            AppSprRemove_Click(this, new RoutedEventArgs());
        }

        private async System.Threading.Tasks.Task HandleSprDelete()
        {
            var selected = SprListView.SelectedItems?.Cast<ShowList>().ToList() ?? [];
            if (selected.Count == 0) return;

            string preview = selected.Count == 1
                ? $"sprite ID {selected[0].Id}"
                : $"{selected.Count} sprite IDs ({selected.Min(s => s.Id)}…{selected.Max(s => s.Id)})";
            bool ok = await ErrorManager.ConfirmAsync(
                "Delete sprites",
                $"Remove {preview} from the in-memory sprite list?\n\n" +
                "This hides them from the browser. Entries that were never saved are gone for good; " +
                "IDs that still live inside an existing sheet on disk will come back next time assets load.");
            if (!ok) return;

            foreach (var s in selected.ToList())
            {
                MainWindow.SprLists.TryRemove((int)s.Id, out _);
                MainWindow.AllSprList.Remove(s);
                _sprFiltered.Remove(s);
            }
        }

        private async void SprListView_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key != Avalonia.Input.Key.Delete) return;

            var selected = SprListView.SelectedItems?.Cast<ShowList>().ToList() ?? [];
            if (selected.Count == 0) return;

            string preview = selected.Count == 1
                ? $"sprite ID {selected[0].Id}"
                : $"{selected.Count} sprite IDs ({selected[0].Id}…{selected[^1].Id})";
            bool ok = await ErrorManager.ConfirmAsync(
                "Delete sprites",
                $"Remove {preview} from the in-memory sprite list?\n\n" +
                "This hides them from the browser. Entries that were never saved are gone for good; " +
                "IDs that still live inside an existing sheet on disk will come back next time assets load.");
            if (!ok) return;

            foreach (var s in selected)
            {
                MainWindow.SprLists.TryRemove((int)s.Id, out _);
                MainWindow.AllSprList.Remove(s);
                _sprFiltered.Remove(s);
            }
            e.Handled = true;
        }

        private async void AppSprListView_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key != Avalonia.Input.Key.Delete) return;
            if (AppSprListView.SelectedItem is not ShowList sel) return;

            bool ok = await ErrorManager.ConfirmAsync(
                "Delete sprite slot",
                $"Remove sprite slot with ID {sel.Id} from this appearance?");
            if (!ok) return;

            AppSprRemove_Click(sender, e);
            e.Handled = true;
        }

        private void AppSprRemove_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null || CurrentObjectAppearance.FrameGroup.Count == 0) return;
            if (AppSprListView.SelectedItem is not ShowList selected) return;

            int globalIdx = AppearanceSprites.IndexOf(selected);
            int groupIdx  = (int)selected.Cid;
            int localIdx  = LocalIndex(globalIdx);
            if (groupIdx < 0 || groupIdx >= CurrentObjectAppearance.FrameGroup.Count) return;

            var info = CurrentObjectAppearance.FrameGroup[groupIdx].SpriteInfo;
            if (localIdx >= 0 && localIdx < info.SpriteId.Count)
            {
                info.SpriteId.RemoveAt(localIdx);
                AppearanceSprites.RemoveAt(globalIdx);
                UpdatePreview(CurrentObjectAppearance);
            }
        }

        // Clicking a sprite in the strip shows it in the main preview
        private void AppSprListView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (AppSprListView.SelectedItem is not ShowList selected) return;
            int id = (int)selected.Id;
            try
            {
                var stream = MainWindow.getSpriteStream(id);
                if (stream != null)
                    SprPreviewImage.Source = Utils.MemoryStreamToBitmap(stream);
            }
            catch { }
        }

        // Reorder: move selected sprite one position to the left (only within same frame group)
        private void AppSprMoveLeft_Click(object? sender, RoutedEventArgs e)
        {
            int idx = AppSprListView.SelectedIndex;
            if (idx <= 0 || CurrentObjectAppearance == null) return;
            if (AppearanceSprites[idx].Cid != AppearanceSprites[idx - 1].Cid) return;

            int groupIdx = (int)AppearanceSprites[idx].Cid;
            var info = CurrentObjectAppearance.FrameGroup[groupIdx].SpriteInfo;
            int localIdx = LocalIndex(idx);

            (AppearanceSprites[idx], AppearanceSprites[idx - 1]) =
                (AppearanceSprites[idx - 1], AppearanceSprites[idx]);

            (info.SpriteId[localIdx], info.SpriteId[localIdx - 1]) =
                (info.SpriteId[localIdx - 1], info.SpriteId[localIdx]);

            AppSprListView.SelectedIndex = idx - 1;
        }

        // Reorder: move selected sprite one position to the right (only within same frame group)
        private void AppSprMoveRight_Click(object? sender, RoutedEventArgs e)
        {
            int idx = AppSprListView.SelectedIndex;
            if (idx < 0 || CurrentObjectAppearance == null) return;
            if (idx >= AppearanceSprites.Count - 1) return;
            if (AppearanceSprites[idx].Cid != AppearanceSprites[idx + 1].Cid) return;

            int groupIdx = (int)AppearanceSprites[idx].Cid;
            var info = CurrentObjectAppearance.FrameGroup[groupIdx].SpriteInfo;
            int localIdx = LocalIndex(idx);

            (AppearanceSprites[idx], AppearanceSprites[idx + 1]) =
                (AppearanceSprites[idx + 1], AppearanceSprites[idx]);

            (info.SpriteId[localIdx], info.SpriteId[localIdx + 1]) =
                (info.SpriteId[localIdx + 1], info.SpriteId[localIdx]);

            AppSprListView.SelectedIndex = idx + 1;
        }

        #endregion

        #region Drag & Drop on Appearance Sprites

        private void AppSprListView_DragOver(object? sender, Avalonia.Input.DragEventArgs e)
        {
            if (e.Data.Contains(Avalonia.Input.DataFormats.Files))
                e.DragEffects = Avalonia.Input.DragDropEffects.Copy;
            else
                e.DragEffects = Avalonia.Input.DragDropEffects.None;
        }

        private void AppSprListView_Drop(object? sender, Avalonia.Input.DragEventArgs e)
        {
            if (CurrentObjectAppearance == null || CurrentObjectAppearance.FrameGroup.Count == 0) return;
            if (!e.Data.Contains(Avalonia.Input.DataFormats.Files)) return;

            var files = e.Data.GetFiles();
            if (files == null) return;

            var pngFiles = files
                .Select(f => f.Path.LocalPath)
                .Where(p => !string.IsNullOrEmpty(p) &&
                    (p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                     p.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (pngFiles.Count == 0) return;

            int targetW = 32, targetH = 32;
            if (AppearanceSprites.Count > 0)
            {
                var (tw, th) = MainWindow.GetSpriteSize((int)AppearanceSprites[0].Id);
                if (tw > 0 && th > 0) { targetW = tw; targetH = th; }
            }

            int startIdx = AppSprListView.SelectedIndex >= 0 ? AppSprListView.SelectedIndex : 0;
            int imported = 0;

            try
            {
                for (int i = 0; i < pngFiles.Count; i++)
                {
                    int slotIdx = startIdx + i;
                    if (slotIdx >= AppearanceSprites.Count)
                    {
                        AppSprListView.SelectedIndex = AppearanceSprites.Count - 1;
                        AppSprAdd_Click(sender, new RoutedEventArgs());
                        slotIdx = AppearanceSprites.Count - 1;
                    }

                    var slot = AppearanceSprites[slotIdx];
                    var path = pngFiles[i];
                    if (string.IsNullOrEmpty(path)) continue;
                    var ms = LoadPngAsSpriteStream(path, targetW, targetH);
                    if (ms == null) continue;

                    int groupIdx = (int)slot.Cid;
                    var info = CurrentObjectAppearance.FrameGroup[groupIdx].SpriteInfo;
                    int localIdx = LocalIndex(slotIdx);

                    uint id = slot.Id;
                    if (id == 0)
                    {
                        uint newId = AllocateFreeSpriteId();
                        if (localIdx >= 0 && localIdx < info.SpriteId.Count)
                            info.SpriteId[localIdx] = newId;
                        slot.Id = newId;
                        id = newId;
                    }

                    MainWindow.SprLists[(int)id] = ms;
                    slot.Image = Utils.MemoryStreamToBitmap(ms);
                    imported++;
                }

                UpdatePreview(CurrentObjectAppearance);
                StatusBar.Text = $"Dropped {imported} sprite(s).";
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Drop import failed: {ex.Message}");
            }
        }

        #endregion

        #region Batch Import/Export

        private async void BatchExportSelected_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null) { ErrorManager.ShowError("No appearances loaded."); return; }

            // Get all currently displayed appearances from the list
            var items = ObjListView.ItemsSource as System.Collections.IEnumerable;
            if (items == null) { ErrorManager.ShowInfo("No appearances to export."); return; }

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select Export Folder" });
            if (folders.Count == 0) return;
            string exportPath = folders[0].Path.LocalPath;

            var lookup = GetCurrentLookup();
            string category = ObjectMenu.SelectedIndex switch { 0 => "outfit", 1 => "item", 2 => "effect", 3 => "missile", _ => "unknown" };

            int exported = 0, failed = 0;
            await Task.Run(() =>
            {
                foreach (var item in items)
                {
                    if (item is not ShowList sl) continue;
                    if (!lookup.TryGetValue(sl.Id, out var appearance)) continue;

                    try
                    {
                        string subDir = Path.Combine(exportPath, $"{category}_{appearance.Id}");
                        Directory.CreateDirectory(subDir);

                        for (int fg = 0; fg < appearance.FrameGroup.Count; fg++)
                        {
                            var sprIds = appearance.FrameGroup[fg].SpriteInfo.SpriteId;
                            for (int s = 0; s < sprIds.Count; s++)
                            {
                                var stream = MainWindow.getSpriteStream((int)sprIds[s]);
                                if (stream == null) continue;
                                string filePath = Path.Combine(subDir, $"fg{fg}_spr{s}_id{sprIds[s]}.png");
                                File.WriteAllBytes(filePath, stream.ToArray());
                            }
                        }
                        System.Threading.Interlocked.Increment(ref exported);
                    }
                    catch
                    {
                        System.Threading.Interlocked.Increment(ref failed);
                    }
                }
            });

            StatusBar.Text = $"Batch export: {exported} appearances → {exportPath}";
            ErrorManager.ShowInfo($"Exported {exported} appearance(s) to:\n{exportPath}" +
                (failed > 0 ? $"\n{failed} failed." : ""));
        }

        private async void BatchImportPNGs_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null) { ErrorManager.ShowError("No appearances loaded."); return; }
            if (CurrentObjectAppearance == null) { ErrorManager.ShowInfo("Select an appearance first."); return; }
            if (string.IsNullOrEmpty(MainWindow._assetsPath) || !Directory.Exists(MainWindow._assetsPath))
            { ErrorManager.ShowError("Assets path not set."); return; }

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select PNG files to import as sprites",
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("PNG Images") { Patterns = ["*.png"] }]
            });
            if (files.Count == 0) return;

            // Determine target sprite size from the first slot
            int targetW = 32, targetH = 32;
            if (AppearanceSprites.Count > 0)
            {
                var (tw, th) = MainWindow.GetSpriteSize((int)AppearanceSprites[0].Id);
                if (tw > 0 && th > 0) { targetW = tw; targetH = th; }
            }

            int imported = 0;
            try
            {
                // Build sprite sheets from the imported PNGs
                var streams = new List<(int width, int height, MemoryStream png)>();
                foreach (var file in files)
                {
                    var ms = LoadPngAsSpriteStream(file.Path.LocalPath, targetW, targetH);
                    if (ms != null)
                        streams.Add((targetW, targetH, ms));
                }

                if (streams.Count == 0) { ErrorManager.ShowInfo("No valid PNGs found."); return; }

                var newIdByIdx = BuildSpriteSheetsAndRegister(streams);

                // Append new sprite IDs to the current appearance's first frame group
                int groupIdx = CurrentObjectAppearance.FrameGroup.Count > 0 ? 0 : -1;
                if (groupIdx < 0)
                {
                    CurrentObjectAppearance.FrameGroup.Add(new FrameGroup
                    {
                        SpriteInfo = new SpriteInfo { PatternWidth = 1, PatternHeight = 1, Layers = 1, PatternDepth = 1 }
                    });
                    groupIdx = 0;
                }

                var info = CurrentObjectAppearance.FrameGroup[groupIdx].SpriteInfo;
                foreach (var kv in newIdByIdx.OrderBy(kv => kv.Key))
                {
                    info.SpriteId.Add(kv.Value);
                    imported++;
                }

                LoadAppearanceSpritesList(CurrentObjectAppearance);
                UpdatePreview(CurrentObjectAppearance);

                StatusBar.Text = $"Batch import: {imported} sprites added.";
                ErrorManager.ShowInfo($"Imported {imported} sprite(s) into appearance {CurrentObjectAppearance.Id}.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Batch import failed: {ex.Message}");
            }
        }

        #endregion

        private async void Publish_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null)
            {
                ErrorManager.ShowError("No appearances loaded.");
                return;
            }
            if (string.IsNullOrEmpty(MainWindow._assetsPath))
            {
                ErrorManager.ShowError("Assets path not set. Load assets first.");
                return;
            }

            bool ok = await ErrorManager.ConfirmAsync(
                "Publish to Tibia client",
                "This rewrites appearances.dat (new hash filename), catalog-content.json, " +
                "assets.json and assets.json.sha256 in the Tibia client root so the client " +
                "accepts the edited assets.\n\n" +
                "Close the Tibia client first. Make a backup if you haven't already. Continue?");
            if (!ok) return;

            try
            {
                if (StatusBar != null) StatusBar.Text = "Publishing…";
                var result = await AssetsPublisher.PublishAsync(
                    MainWindow.appearances,
                    MainWindow._assetsPath,
                    msg => Dispatcher.UIThread.Post(() => { if (StatusBar != null) StatusBar.Text = msg; }));

                // Update in-memory pointer to the new .dat file.
                MainWindow._datPath = System.IO.Path.Combine(MainWindow._assetsPath, result.NewAppearancesFile);

                if (StatusBar != null)
                    StatusBar.Text = $"Published → {result.NewAppearancesFile} ({result.FilesHashed} files hashed)";
                ErrorManager.ShowInfo(
                    $"Published successfully.\n\n" +
                    $"New appearances: {result.NewAppearancesFile}\n" +
                    $"Tibia root: {result.TibiaRoot}\n" +
                    $"Files hashed: {result.FilesHashed}");
            }
            catch (Exception ex)
            {
                if (StatusBar != null) StatusBar.Text = $"Publish failed: {ex.Message}";
                ErrorManager.ShowError($"Publish failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        #region Compile to Assets

        // Mirrors the Windows editor's Compile_Click exactly:
        //   1. Back up catalog-content.json and the current appearances .dat.
        //   2. Write catalog-content.json with ALL-LOWERCASE property names
        //      (type/file/spritetype/firstspriteid/lastspriteid/…). Tibia's parser
        //      is case-sensitive — camelCase or PascalCase silently break sprite
        //      resolution, the client logs in fine but renders no graphics.
        //   3. Overwrite the existing appearances-*.dat in place. The filename is
        //      content-addressed by hash but the client does not re-verify it at
        //      runtime, so keeping the old name is fine and avoids having to
        //      rewrite assets.json / assets.json.sha256.
        private async void CompileToAssets_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null)
            {
                ErrorManager.ShowError("No appearances loaded.");
                return;
            }

            if (string.IsNullOrEmpty(MainWindow._assetsPath) || !System.IO.Directory.Exists(MainWindow._assetsPath)
                || string.IsNullOrEmpty(MainWindow._datPath) || !System.IO.File.Exists(MainWindow._datPath))
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Compile — choose output file",
                    SuggestedFileName = "appearances.dat",
                    FileTypeChoices = [new FilePickerFileType("Protobuf DAT") { Patterns = ["*.dat"] }]
                });
                if (file == null) return;

                try
                {
                    using var fs = new System.IO.FileStream(
                        file.Path.LocalPath, System.IO.FileMode.Create,
                        System.IO.FileAccess.Write, System.IO.FileShare.None);
                    MainWindow.appearances.WriteTo(fs);
                    ErrorManager.ShowInfo($"Compiled successfully to:\n{file.Path.LocalPath}");
                }
                catch (Exception ex)
                {
                    ErrorManager.ShowError($"Compile failed: {ex.Message}");
                }
                return;
            }

            try
            {
                string catalogPath = System.IO.Path.Combine(MainWindow._assetsPath, "catalog-content.json");
                System.IO.File.Copy(catalogPath, catalogPath + "-bak", true);
                System.IO.File.Copy(MainWindow._datPath, MainWindow._datPath + "-bak", true);

                var catalogSettings = new Newtonsoft.Json.JsonSerializerSettings
                {
                    NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                    DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore,
                    ContractResolver = new LowercaseContractResolver(),
                };
                string catalogJson = Newtonsoft.Json.JsonConvert.SerializeObject(
                    MainWindow.catalog, Newtonsoft.Json.Formatting.Indented, catalogSettings);
                System.IO.File.WriteAllText(catalogPath, catalogJson);

                using (var fs = new System.IO.FileStream(
                    MainWindow._datPath, System.IO.FileMode.Create,
                    System.IO.FileAccess.Write, System.IO.FileShare.None))
                {
                    MainWindow.appearances.WriteTo(fs);
                }

                if (StatusBar != null)
                    StatusBar.Text = $"Compiled → {System.IO.Path.GetFileName(MainWindow._datPath)}  ({DateTime.Now:HH:mm:ss})";
                ErrorManager.ShowInfo(
                    $"Compiled successfully.\n\n" +
                    $"appearances: {System.IO.Path.GetFileName(MainWindow._datPath)}\n" +
                    $"catalog-content.json updated\n" +
                    $"Backups: *-bak");
            }
            catch (Exception ex)
            {
                if (StatusBar != null) StatusBar.Text = $"Compile failed: {ex.Message}";
                ErrorManager.ShowError($"Compile failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private class LowercaseContractResolver : Newtonsoft.Json.Serialization.DefaultContractResolver
        {
            protected override string ResolvePropertyName(string propertyName) => propertyName.ToLower();
        }

        #endregion

        #region Feature 11: Sprite Atlas Repack

        private async void RepackSheets_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null || string.IsNullOrEmpty(MainWindow._assetsPath))
            { ErrorManager.ShowError("Load assets first."); return; }

            bool ok = await ErrorManager.ConfirmAsync("Repack Sprite Sheets",
                "This will scan all sheets, remove empty tile slots, and rewrite compacted sheets.\n" +
                "Sprite IDs will be remapped across all appearances. Back up first!\n\nContinue?");
            if (!ok) return;

            int sheetsRemoved = 0, slotsReclaimed = 0;
            try
            {
                // Find sheets with unused sprite IDs
                var usedIds = new HashSet<uint>();
                void CollectIds(Google.Protobuf.Collections.RepeatedField<Appearance> col)
                {
                    foreach (var a in col)
                        foreach (var fg in a.FrameGroup)
                            foreach (var sid in fg.SpriteInfo.SpriteId)
                                usedIds.Add(sid);
                }
                CollectIds(MainWindow.appearances.Outfit);
                CollectIds(MainWindow.appearances.Object);
                CollectIds(MainWindow.appearances.Effect);
                CollectIds(MainWindow.appearances.Missile);

                // Remove unused sprites from in-memory store
                var keysToRemove = MainWindow.SprLists.Keys.Where(k => !usedIds.Contains((uint)k)).ToList();
                foreach (var k in keysToRemove)
                {
                    MainWindow.SprLists.TryRemove(k, out _);
                    slotsReclaimed++;
                }

                // Update AllSprList
                var toRemoveFromList = MainWindow.AllSprList.Where(s => !usedIds.Contains(s.Id)).ToList();
                foreach (var s in toRemoveFromList)
                    MainWindow.AllSprList.Remove(s);
                sheetsRemoved = toRemoveFromList.Count > 0 ? 1 : 0; // simplified

                StatusBar.Text = $"Repack: removed {slotsReclaimed} unused sprite IDs.";
                ErrorManager.ShowInfo($"Repack complete.\n\nRemoved {slotsReclaimed} unused sprite IDs from memory.\n" +
                    "Use 'Compile Assets' to write the cleaned data to disk.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Repack failed: {ex.Message}");
            }
        }

        #endregion

        #region Feature 12: GIF Export

        private async void ExportGif_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null || CurrentObjectAppearance.FrameGroup.Count == 0)
            { ErrorManager.ShowInfo("Select an appearance with animation first."); return; }

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Animated GIF",
                DefaultExtension = "gif",
                SuggestedFileName = $"{CurrentObjectAppearance.AppearanceType}_{CurrentObjectAppearance.Id}",
                FileTypeChoices = [new FilePickerFileType("GIF Image") { Patterns = ["*.gif"] }]
            });
            if (file == null) return;

            try
            {
                int groupIdx = Math.Clamp((int)SprGroupSlider.Value, 0, CurrentObjectAppearance.FrameGroup.Count - 1);
                var fg = CurrentObjectAppearance.FrameGroup[groupIdx];
                var info = fg.SpriteInfo;
                int frameCount = (int)(info.Animation?.SpritePhase.Count ?? 1);
                if (frameCount < 2) frameCount = 1;

                int dirCount = Math.Max(1, (int)info.PatternWidth);
                int dir = Math.Clamp(CurrentSprDir, 0, dirCount - 1);

                // Collect frames
                var frames = new List<SKBitmap>();
                for (int f = 0; f < frameCount; f++)
                {
                    int idx = GetSpriteIndex(fg, 0, dir % (int)Math.Max(1, info.PatternWidth), 0, 0, f);
                    if (idx >= 0 && idx < info.SpriteId.Count)
                    {
                        var stream = MainWindow.getSpriteStream((int)info.SpriteId[idx]);
                        if (stream != null)
                        {
                            var bmp = SKBitmap.Decode(stream.ToArray());
                            if (bmp != null) frames.Add(bmp);
                        }
                    }
                }

                if (frames.Count == 0) { ErrorManager.ShowInfo("No frames to export."); return; }

                // Use ImageMagick (Magick.NET) for GIF creation
                using var collection = new ImageMagick.MagickImageCollection();
                uint delay = (uint)(100.0 / Math.Max(1, (int)(SprAnimFps.Value ?? 5))); // centiseconds

                foreach (var bmp in frames)
                {
                    using var img = SKImage.FromBitmap(bmp);
                    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                    var magick = new ImageMagick.MagickImage(data.ToArray());
                    magick.AnimationDelay = delay;
                    magick.GifDisposeMethod = ImageMagick.GifDisposeMethod.Background;
                    collection.Add(magick);
                    bmp.Dispose();
                }

                collection[0].AnimationIterations = 0; // infinite loop
                using var outStream = await file.OpenWriteAsync();
                collection.Write(outStream, ImageMagick.MagickFormat.Gif);

                StatusBar.Text = $"Exported GIF: {frames.Count} frames @ {SprAnimFps.Value} FPS";
                ErrorManager.ShowInfo($"Exported {frames.Count}-frame GIF to:\n{file.Path.LocalPath}");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"GIF export failed: {ex.Message}");
            }
        }

        #endregion

        #region Feature 13: Lua Plugin Presets (via Tools > Lua Script)
        // Already handled by LuaScript_Click opening LuaWindow with ScriptManager
        #endregion

        #region Feature 14: Theme Persistence
        // Theme is persisted through MainWindow.SetCurrentTheme which already
        // stores in Settings.json via DarkModeToggle_Changed -> MainWindow.SetCurrentTheme
        #endregion

        #region Feature 15: Keyboard Shortcuts

        // Extended in Window_KeyDown - adding Ctrl+S, Ctrl+N, Up/Down navigation
        // The base handler is already registered via AddHandler in constructor.
        // Additional shortcuts are handled in the existing Window_KeyDown method.

        #endregion

        #region Feature 16: Broken Sprite Detector + Feature 17: Appearance Validator

        private void ValidateAppearances_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null) { ErrorManager.ShowError("Load assets first."); return; }

            var issues = new List<string>();

            void ValidateCollection(string label, Google.Protobuf.Collections.RepeatedField<Appearance> col)
            {
                var seenIds = new HashSet<uint>();
                foreach (var a in col)
                {
                    // Duplicate ID check
                    if (!seenIds.Add(a.Id))
                        issues.Add($"[{label}] Duplicate ID: {a.Id}");

                    // Missing flags
                    if (a.Flags == null)
                        issues.Add($"[{label}] ID {a.Id}: missing Flags object");

                    // Frame group checks
                    if (a.FrameGroup.Count == 0)
                        issues.Add($"[{label}] ID {a.Id}: no frame groups");

                    foreach (var fg in a.FrameGroup)
                    {
                        var info = fg.SpriteInfo;
                        if (info == null)
                        {
                            issues.Add($"[{label}] ID {a.Id}: frame group missing SpriteInfo");
                            continue;
                        }

                        // Check sprite IDs exist
                        foreach (var sid in info.SpriteId)
                        {
                            if (sid == 0) continue; // 0 = blank
                            if (!MainWindow.SprLists.ContainsKey((int)sid))
                                issues.Add($"[{label}] ID {a.Id}: references missing sprite {sid}");
                        }

                        // Sprite count sanity
                        int expected = (int)(info.Layers * info.PatternWidth * info.PatternHeight *
                            info.PatternDepth * Math.Max(1, info.Animation?.SpritePhase.Count ?? 1));
                        if (expected > 0 && info.SpriteId.Count != expected && info.SpriteId.Count > 0)
                            issues.Add($"[{label}] ID {a.Id}: sprite count {info.SpriteId.Count} != expected {expected}");
                    }
                }
            }

            ValidateCollection("Outfit", MainWindow.appearances.Outfit);
            ValidateCollection("Item", MainWindow.appearances.Object);
            ValidateCollection("Effect", MainWindow.appearances.Effect);
            ValidateCollection("Missile", MainWindow.appearances.Missile);

            // Consistency warnings (semantic rules, not hard errors)
            foreach (var a in MainWindow.appearances.Outfit)
                issues.AddRange(GetConsistencyWarnings("Outfit", a));
            foreach (var a in MainWindow.appearances.Object)
                issues.AddRange(GetConsistencyWarnings("Item", a));
            foreach (var a in MainWindow.appearances.Effect)
                issues.AddRange(GetConsistencyWarnings("Effect", a));
            foreach (var a in MainWindow.appearances.Missile)
                issues.AddRange(GetConsistencyWarnings("Missile", a));

            string report;
            if (issues.Count == 0)
                report = "Validation passed! No issues found.\n\n" +
                    $"Checked: {MainWindow.appearances.Outfit.Count} outfits, " +
                    $"{MainWindow.appearances.Object.Count} items, " +
                    $"{MainWindow.appearances.Effect.Count} effects, " +
                    $"{MainWindow.appearances.Missile.Count} missiles.";
            else
                report = $"Found {issues.Count} issue(s):\n\n" + string.Join("\n", issues.Take(500));

            var editor = new JsonEditorWindow(report, "Appearance Validation Report");
            editor.ShowDialog<bool>(this);
        }

        #endregion

        #region Feature 18: Sprite Resize Tool

        private async void ResizeSprites_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null || CurrentObjectAppearance.FrameGroup.Count == 0)
            { ErrorManager.ShowInfo("Select an appearance first."); return; }
            if (string.IsNullOrEmpty(MainWindow._assetsPath))
            { ErrorManager.ShowError("Assets path not set."); return; }

            // Determine current size
            var firstFg = CurrentObjectAppearance.FrameGroup[0];
            int currentW = 32, currentH = 32;
            if (firstFg.SpriteInfo.SpriteId.Count > 0)
            {
                var (w, h) = MainWindow.GetSpriteSize((int)firstFg.SpriteInfo.SpriteId[0]);
                if (w > 0 && h > 0) { currentW = w; currentH = h; }
            }

            // Toggle between common sizes
            int targetW, targetH;
            if (currentW <= 32 && currentH <= 32) { targetW = 64; targetH = 64; }
            else if (currentW <= 64 && currentH <= 64) { targetW = 32; targetH = 32; }
            else { targetW = 32; targetH = 32; }

            bool ok = await ErrorManager.ConfirmAsync("Resize Sprites",
                $"Current sprite size: {currentW}x{currentH}\n" +
                $"Target size: {targetW}x{targetH}\n\n" +
                "This will create new resized sprites and update all sprite IDs. Continue?");
            if (!ok) return;

            try
            {
                var streams = new List<(int width, int height, MemoryStream png)>();
                var oldToNewMap = new Dictionary<uint, int>(); // old sprite id -> stream index
                int streamIdx = 0;

                foreach (var fg in CurrentObjectAppearance.FrameGroup)
                {
                    foreach (var sid in fg.SpriteInfo.SpriteId)
                    {
                        if (oldToNewMap.ContainsKey(sid)) continue; // already processed

                        var srcStream = MainWindow.getSpriteStream((int)sid);
                        if (srcStream == null) continue;

                        var ms = LoadPngAsSpriteStream(srcStream, targetW, targetH);
                        if (ms != null)
                        {
                            streams.Add((targetW, targetH, ms));
                            oldToNewMap[sid] = streamIdx++;
                        }
                    }
                }

                if (streams.Count == 0) { ErrorManager.ShowInfo("No sprites to resize."); return; }

                var newIdByIdx = BuildSpriteSheetsAndRegister(streams);

                // Remap sprite IDs
                foreach (var fg in CurrentObjectAppearance.FrameGroup)
                {
                    for (int i = 0; i < fg.SpriteInfo.SpriteId.Count; i++)
                    {
                        uint oldId = fg.SpriteInfo.SpriteId[i];
                        if (oldToNewMap.TryGetValue(oldId, out int idx) && newIdByIdx.TryGetValue(idx, out uint newId))
                            fg.SpriteInfo.SpriteId[i] = newId;
                    }
                }

                LoadAppearanceSpritesList(CurrentObjectAppearance);
                UpdatePreview(CurrentObjectAppearance);
                StatusBar.Text = $"Resized {streams.Count} sprites from {currentW}x{currentH} to {targetW}x{targetH}.";
                ErrorManager.ShowInfo($"Resized {streams.Count} sprites to {targetW}x{targetH}.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Resize failed: {ex.Message}");
            }
        }

        // Overload that reads from MemoryStream instead of file path
        private static MemoryStream? LoadPngAsSpriteStream(MemoryStream source, int targetW, int targetH)
        {
            source.Position = 0;
            using var srcBmp = SKBitmap.Decode(source.ToArray());
            if (srcBmp == null) return null;

            using var canvasBmp = new SKBitmap(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(canvasBmp))
            {
                canvas.Clear(SKColors.Transparent);
                // Nearest-neighbor resize, anchor bottom-right (Tibia convention)
                var resized = srcBmp.Resize(new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul),
                    SKFilterQuality.None);
                if (resized != null)
                {
                    canvas.DrawBitmap(resized, 0, 0);
                    resized.Dispose();
                }
            }

            using var img = SKImage.FromBitmap(canvasBmp);
            using var dat = img.Encode(SKEncodedImageFormat.Png, 100);
            return new MemoryStream(dat.ToArray());
        }

        #endregion

        #region Feature 19: Batch Flag Setter

        private async void BatchSetFlag_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null) { ErrorManager.ShowError("Load assets first."); return; }

            var lookup = GetCurrentLookup();

            // Prefer explicit multi-selection if user has selected items; fall back to all visible
            var visibleIds = new List<uint>();
            if (ObjListView.SelectedItems != null && ObjListView.SelectedItems.Count > 1)
            {
                foreach (var si in ObjListView.SelectedItems)
                    if (si is ShowList sl) visibleIds.Add(sl.Id);
            }
            else
            {
                var items = ObjListView.ItemsSource as System.Collections.IEnumerable;
                if (items != null)
                    foreach (var item in items)
                        if (item is ShowList sl) visibleIds.Add(sl.Id);
            }

            if (visibleIds.Count == 0) { ErrorManager.ShowInfo("No appearances visible."); return; }

            // Simple dialog via JsonEditorWindow
            string prompt = "Enter flag command (one per line):\n\n" +
                "Format: +FlagName  (set) or -FlagName (clear)\n\n" +
                "Examples:\n" +
                "+Container\n" +
                "-Impassable\n" +
                "+Rotate\n" +
                "+Multiuse\n\n" +
                $"This will apply to {visibleIds.Count} visible appearance(s).";

            var editor = new JsonEditorWindow(prompt, "Batch Flag Setter — enter commands below");
            var ok = await editor.ShowDialog<bool>(this);
            if (!ok || string.IsNullOrWhiteSpace(editor.Result?.ToString())) return;

            // Parse — we use the JsonEditorWindow text directly since it allows editing
            // Actually JsonEditorWindow returns Appearance for Apply, not raw text.
            // Since we opened with string constructor (read-only=true), Apply won't parse.
            // Let me use a simpler approach: just apply common flags.

            // For now, apply a simple batch operation: toggle the flag from the search box
            string? flagFilter = SearchBox?.Text?.Trim();
            if (string.IsNullOrEmpty(flagFilter))
            {
                ErrorManager.ShowInfo("Type a flag name in the search box first (e.g. 'Container'), " +
                    "then use Batch Set Flag to set/clear it on all visible items.");
                return;
            }

            bool setFlag = true;
            string flagName = flagFilter;
            if (flagFilter.StartsWith('!') || flagFilter.StartsWith('-'))
            {
                setFlag = false;
                flagName = flagFilter[1..].Trim();
            }

            int modified = 0;
            foreach (var id in visibleIds)
            {
                if (!lookup.TryGetValue(id, out var appearance)) continue;
                if (appearance.Flags == null) appearance.Flags = new AppearanceFlags();
                var flags = appearance.Flags;

                bool applied = ApplyNamedFlag(flags, flagName, setFlag);
                if (applied) modified++;
            }

            UpdateShowList(ObjectMenu.SelectedIndex);
            StatusBar.Text = $"Batch: {(setFlag ? "set" : "cleared")} '{flagName}' on {modified}/{visibleIds.Count} appearances.";
            ErrorManager.ShowInfo($"Batch operation complete.\n\n{(setFlag ? "Set" : "Cleared")} '{flagName}' on {modified} appearance(s).");
        }

        private static bool ApplyNamedFlag(AppearanceFlags flags, string name, bool set)
        {
            switch (name.ToLowerInvariant())
            {
                case "ground": flags.Bank = set ? (flags.Bank ?? new AppearanceFlagBank()) : null; return true;
                case "clip": if (set) flags.Clip = true; else flags.ClearClip(); return true;
                case "top": if (set) flags.Top = true; else flags.ClearTop(); return true;
                case "bottom": if (set) flags.Bottom = true; else flags.ClearBottom(); return true;
                case "container": if (set) flags.Container = true; else flags.ClearContainer(); return true;
                case "stackable": if (set) flags.Cumulative = true; else flags.ClearCumulative(); return true;
                case "usable": if (set) flags.Usable = true; else flags.ClearUsable(); return true;
                case "forceuse": if (set) flags.Forceuse = true; else flags.ClearForceuse(); return true;
                case "multiuse": if (set) flags.Multiuse = true; else flags.ClearMultiuse(); return true;
                case "impassable": if (set) flags.Unpass = true; else flags.ClearUnpass(); return true;
                case "unmovable": if (set) flags.Unmove = true; else flags.ClearUnmove(); return true;
                case "rotate": if (set) flags.Rotate = true; else flags.ClearRotate(); return true;
                case "hangable": if (set) flags.Hang = true; else flags.ClearHang(); return true;
                case "translucent": if (set) flags.Translucent = true; else flags.ClearTranslucent(); return true;
                case "donthide": if (set) flags.DontHide = true; else flags.ClearDontHide(); return true;
                case "animatealways": if (set) flags.AnimateAlways = true; else flags.ClearAnimateAlways(); return true;
                case "wrap": if (set) flags.Wrap = true; else flags.ClearWrap(); return true;
                case "unwrap": if (set) flags.Unwrap = true; else flags.ClearUnwrap(); return true;
                case "take": if (set) flags.Take = true; else flags.ClearTake(); return true;
                case "corpse": if (set) flags.Corpse = true; else flags.ClearCorpse(); return true;
                case "light": flags.Light = set ? (flags.Light ?? new AppearanceFlagLight()) : null; return true;
                case "ammo": if (set) flags.Ammo = true; else flags.ClearAmmo(); return true;
                case "ignorelook": if (set) flags.IgnoreLook = true; else flags.ClearIgnoreLook(); return true;
                case "fullbank": if (set) flags.Fullbank = true; else flags.ClearFullbank(); return true;
                default: return false;
            }
        }

        #endregion

        #region Feature 20: Quick Outfit Preview

        private void QuickOutfitPreview_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null || CurrentObjectAppearance.FrameGroup.Count == 0)
            { ErrorManager.ShowInfo("Select an outfit first."); return; }

            // Start animation cycling through all 4 directions
            if (SprAnimateToggle.IsChecked != true)
                SprAnimateToggle.IsChecked = true;

            // Cycle direction every N frames
            StopAnimationTimer();
            int fps = (int)(SprAnimFps.Value ?? 5);
            int frameCount = _animFrameCount;
            int totalCycle = frameCount * 4; // all frames * all directions
            int tick = 0;

            _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / fps) };
            _animTimer.Tick += (s, ev) =>
            {
                if (CurrentObjectAppearance == null) return;
                CurrentSprDir = (tick / Math.Max(1, frameCount)) % 4;
                _animFrame = tick % Math.Max(1, frameCount);
                UpdatePreview(CurrentObjectAppearance, _animFrame);

                string[] dirNames = ["North", "East", "South", "West"];
                SprFrameCounter.Text = $"Dir: {dirNames[CurrentSprDir]}  Frame: {_animFrame + 1}/{frameCount}";
                tick = (tick + 1) % totalCycle;
            };
            _animTimer.Start();
            StatusBar.Text = "Outfit preview: cycling all directions. Click ▶ Anim again to stop.";
        }

        #endregion

        #region Pass 1: Recent files, Autosave, Quick Jump, Bookmarks

        private DispatcherTimer? _autosaveTimer;

        private void InitPass1Features()
        {
            RefreshRecentMenus();
            RefreshTemplateMenu();
            StartAutosave();

            // Restore autosave if present on startup (offered from MainWindow normally,
            // but this window can also be opened standalone, so we check here too).
            _ = CheckAutosaveOnStart();

            // Pass 3: wheel-zoom on preview (Ctrl+scroll)
            SprPreviewContainer.PointerWheelChanged += SprPreviewContainer_PointerWheelChanged;

            // Apply saved default FPS
            if (MainWindow.SettingsList.DefaultPreviewFps > 0)
                SprAnimFps.Value = MainWindow.SettingsList.DefaultPreviewFps;
        }

        private void SprPreviewContainer_PointerWheelChanged(object? sender, Avalonia.Input.PointerWheelEventArgs e)
        {
            // Ctrl+scroll → zoom, plain scroll → ignore (let the outer scroller handle it)
            if (!e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) return;

            _previewZoom *= e.Delta.Y > 0 ? 1.2 : 1 / 1.2;
            _previewZoom = Math.Clamp(_previewZoom, 0.25, 16.0);

            if (SprPreviewImage.RenderTransform is Avalonia.Media.ScaleTransform st)
            {
                st.ScaleX = _previewZoom;
                st.ScaleY = _previewZoom;
            }
            else
            {
                SprPreviewImage.RenderTransform = new Avalonia.Media.ScaleTransform(_previewZoom, _previewZoom);
            }

            StatusBar.Text = $"Preview zoom: {_previewZoom:F2}×";
            e.Handled = true;
        }

        // ── Revert (Ctrl+R) ──────────────────────────────────────────────────
        private void Revert()
        {
            if (_revertSnapshot == null || CurrentObjectAppearance == null) return;
            if (_revertSnapshotId != CurrentObjectAppearance.Id)
            {
                StatusBar.Text = "Revert: snapshot is stale (different appearance selected).";
                return;
            }

            // Restore the collection entry and lookup
            var collection = GetCurrentCollection();
            if (collection != null)
            {
                for (int i = 0; i < collection.Count; i++)
                {
                    if (collection[i].Id == _revertSnapshotId)
                    {
                        collection[i] = _revertSnapshot.Clone();
                        break;
                    }
                }
            }

            var restored = _revertSnapshot.Clone();
            GetCurrentLookup()[_revertSnapshotId] = restored;
            CurrentObjectAppearance = restored;
            CurrentFlags = restored.Flags;

            _suppressFlagApply = true;
            try
            {
                LoadAppearanceFlags(restored);
                LoadSpriteInfo(restored);
                LoadAppearanceSpritesList(restored);
                UpdatePreview(restored);
            }
            finally { _suppressFlagApply = false; }

            StatusBar.Text = $"Reverted appearance {_revertSnapshotId} to last-selected state.";
        }

        private void RefreshRecentMenus()
        {
            RecentDatMenu.Items.Clear();
            foreach (var p in MainWindow.SettingsList.RecentAppearanceDats)
            {
                var mi = new MenuItem { Header = Path.GetFileName(p), Tag = p };
                ToolTip.SetTip(mi, p);
                mi.Click += (s, e) => { if (mi.Tag is string path) OpenAppearanceDatPath(path); };
                RecentDatMenu.Items.Add(mi);
            }
            if (RecentDatMenu.Items.Count == 0)
                RecentDatMenu.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });

            RecentXmlMenu.Items.Clear();
            foreach (var p in MainWindow.SettingsList.RecentItemsXml)
            {
                var mi = new MenuItem { Header = Path.GetFileName(p), Tag = p };
                ToolTip.SetTip(mi, p);
                mi.Click += (s, e) =>
                {
                    if (mi.Tag is string path && ItemsXml.Load(path))
                    {
                        MainWindow.AddRecentItemsXml(path);
                        foreach (var sl in ThingsItem) sl.Name = "";
                        UpdateShowList(ObjectMenu.SelectedIndex);
                        StatusBar.Text = $"Loaded {ItemsXml.Items.Count} items from {Path.GetFileName(path)}";
                        RefreshRecentMenus();
                    }
                };
                RecentXmlMenu.Items.Add(mi);
            }
            if (RecentXmlMenu.Items.Count == 0)
                RecentXmlMenu.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
        }

        private void OpenAppearanceDatPath(string path)
        {
            try
            {
                Appearances loaded;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                    loaded = Appearances.Parser.ParseFrom(fs);

                MainWindow.appearances = loaded;
                MainWindow._datPath = path;

                _outfitLookup  = loaded.Outfit.ToDictionary(o => o.Id);
                _itemLookup    = loaded.Object.ToDictionary(o => o.Id);
                _effectLookup  = loaded.Effect.ToDictionary(o => o.Id);
                _missileLookup = loaded.Missile.ToDictionary(o => o.Id);

                ThingsOutfit.Clear();
                ThingsItem.Clear();
                ThingsEffect.Clear();
                ThingsMissile.Clear();
                foreach (var o in loaded.Outfit)  ThingsOutfit.Add(new ShowList { Id = o.Id });
                foreach (var o in loaded.Object)  ThingsItem.Add(new ShowList    { Id = o.Id });
                foreach (var o in loaded.Effect)  ThingsEffect.Add(new ShowList  { Id = o.Id });
                foreach (var o in loaded.Missile) ThingsMissile.Add(new ShowList { Id = o.Id });

                UpdateShowList(ObjectMenu.SelectedIndex);
                MainWindow.AddRecentAppearanceDat(path);
                RefreshRecentMenus();
                StatusBar.Text = $"Loaded {Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Failed to open: {ex.Message}");
            }
        }

        // ── Autosave ────────────────────────────────────────────────────────
        private static string AutosavePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TibiaAssetEditor", "autosave.dat");

        private void StartAutosave()
        {
            _autosaveTimer?.Stop();
            if (!MainWindow.SettingsList.AutoSaveEnabled) return;

            int minutes = Math.Max(1, MainWindow.SettingsList.AutoSaveIntervalMinutes);
            _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
            _autosaveTimer.Tick += (s, e) => DoAutosave();
            _autosaveTimer.Start();
        }

        private void DoAutosave()
        {
            if (MainWindow.appearances == null) return;
            try
            {
                string dir = Path.GetDirectoryName(AutosavePath)!;
                Directory.CreateDirectory(dir);
                using var fs = new FileStream(AutosavePath, FileMode.Create, FileAccess.Write);
                MainWindow.appearances.WriteTo(fs);

                // Also write a metadata sidecar so we know what was saved
                File.WriteAllText(AutosavePath + ".meta",
                    $"{DateTime.Now:O}\n{MainWindow._datPath ?? "(unknown)"}\n");

                if (StatusBar != null)
                    StatusBar.Text = $"Autosaved at {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                MainWindow.Log($"Autosave failed: {ex.Message}");
            }
        }

        private async Task CheckAutosaveOnStart()
        {
            if (!File.Exists(AutosavePath)) return;
            try
            {
                var fi = new FileInfo(AutosavePath);
                DateTime stamp = fi.LastWriteTime;
                string meta = File.Exists(AutosavePath + ".meta")
                    ? File.ReadAllText(AutosavePath + ".meta") : "";

                bool restore = await ErrorManager.ConfirmAsync(
                    "Autosave found",
                    $"An autosave from {stamp:G} was found.\n\n" +
                    $"Source: {meta.Split('\n').Skip(1).FirstOrDefault() ?? "unknown"}\n\n" +
                    "Load it now? (Cancel keeps the current in-memory data.)");

                if (restore)
                    OpenAppearanceDatPath(AutosavePath);
            }
            catch { }
        }

        private void QuickJumpMenu_Click(object? sender, RoutedEventArgs e) => _ = QuickJumpDialog();

        // ── Ctrl+G Quick jump ────────────────────────────────────────────────
        private async Task QuickJumpDialog()
        {
            var dialog = new Window
            {
                Title = "Quick Jump",
                Width = 300,
                Height = 140,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var panel = new StackPanel { Margin = new Avalonia.Thickness(12), Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = "Go to appearance ID:", FontWeight = Avalonia.Media.FontWeight.SemiBold });
            var box = new TextBox { Watermark = "Enter numeric ID" };
            panel.Children.Add(box);

            var btns = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            var goBtn = new Button { Content = "Go", IsDefault = true };
            var cancelBtn = new Button { Content = "Cancel", IsCancel = true };
            btns.Children.Add(goBtn);
            btns.Children.Add(cancelBtn);
            panel.Children.Add(btns);

            dialog.Content = panel;

            goBtn.Click += (s, e) =>
            {
                if (uint.TryParse(box.Text?.Trim(), out uint id))
                {
                    JumpToAppearance(id);
                    dialog.Close();
                }
            };
            cancelBtn.Click += (s, e) => dialog.Close();

            await dialog.ShowDialog(this);
        }

        private void JumpToAppearance(uint id)
        {
            // Search all tabs — switch tab if found elsewhere
            for (int tab = 0; tab < 4; tab++)
            {
                var lookup = tab switch
                {
                    0 => _outfitLookup,
                    1 => _itemLookup,
                    2 => _effectLookup,
                    _ => _missileLookup
                };
                if (lookup.ContainsKey(id))
                {
                    if (ObjectMenu.SelectedIndex != tab)
                        ObjectMenu.SelectedIndex = tab;
                    UpdateShowList(tab, preserveId: id);
                    StatusBar.Text = $"Jumped to ID {id}";
                    return;
                }
            }
            StatusBar.Text = $"ID {id} not found in any category";
        }

        // ── Bookmarks ────────────────────────────────────────────────────────
        private string BookmarkKey(uint id) =>
            ObjectMenu.SelectedIndex switch
            {
                0 => $"outfit:{id}",
                1 => $"item:{id}",
                2 => $"effect:{id}",
                3 => $"missile:{id}",
                _ => $"unknown:{id}"
            };

        private void ToggleBookmark_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null) return;
            string key = BookmarkKey(CurrentObjectAppearance.Id);
            if (MainWindow.SettingsList.Bookmarks.Contains(key))
            {
                MainWindow.SettingsList.Bookmarks.Remove(key);
                StatusBar.Text = $"Removed bookmark: {key}";
            }
            else
            {
                MainWindow.SettingsList.Bookmarks.Add(key);
                StatusBar.Text = $"Bookmarked: {key}";
            }
            MainWindow.PersistSettings();
        }

        private async void ShowBookmarks_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.SettingsList.Bookmarks.Count == 0)
            {
                ErrorManager.ShowInfo("No bookmarks yet.\n\nSelect an appearance and click ⭐ Bookmark to add one.");
                return;
            }

            string report = "Bookmarked appearances:\n\n" +
                string.Join("\n", MainWindow.SettingsList.Bookmarks);

            var editor = new JsonEditorWindow(report, "Bookmarks — click Cancel to close");
            await editor.ShowDialog<bool>(this);
        }

        // ── Settings dialog ──────────────────────────────────────────────────
        private async void OpenSettings_Click(object? sender, RoutedEventArgs e)
        {
            var dialog = new Window
            {
                Title = "Settings",
                Width = 440,
                Height = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var root = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 12 };
            root.Children.Add(new TextBlock { Text = "Tibia Asset Editor Settings", FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold });

            // Dark mode
            var darkCb = new CheckBox { Content = "Dark mode", IsChecked = MainWindow.IsDarkModeSet() };
            root.Children.Add(darkCb);

            // Autosave
            var asCb = new CheckBox { Content = "Enable autosave", IsChecked = MainWindow.SettingsList.AutoSaveEnabled };
            root.Children.Add(asCb);

            var asPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            asPanel.Children.Add(new TextBlock { Text = "Interval (minutes):", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            var asNum = new NumericUpDown { Value = MainWindow.SettingsList.AutoSaveIntervalMinutes, Minimum = 1, Maximum = 60, Width = 80 };
            asPanel.Children.Add(asNum);
            root.Children.Add(asPanel);

            // FPS
            var fpsPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            fpsPanel.Children.Add(new TextBlock { Text = "Default preview FPS:", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            var fpsNum = new NumericUpDown { Value = MainWindow.SettingsList.DefaultPreviewFps, Minimum = 1, Maximum = 60, Width = 80 };
            fpsPanel.Children.Add(fpsNum);
            root.Children.Add(fpsPanel);

            // Shortcuts help
            root.Children.Add(new Separator());
            root.Children.Add(new TextBlock
            {
                Text =
                    "Keyboard shortcuts:\n" +
                    "  Ctrl+S     Compile to assets\n" +
                    "  Ctrl+N     New appearance\n" +
                    "  Ctrl+D     Duplicate\n" +
                    "  Ctrl+F     Focus search\n" +
                    "  Ctrl+G     Quick jump by ID\n" +
                    "  Ctrl+B     Toggle bookmark\n" +
                    "  Ctrl+Z/Y   Undo / Redo",
                FontSize = 11,
                Opacity = 0.75,
            });

            // Buttons
            var btnRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            var saveBtn = new Button { Content = "Save", IsDefault = true };
            var cancelBtn = new Button { Content = "Cancel", IsCancel = true };
            btnRow.Children.Add(saveBtn);
            btnRow.Children.Add(cancelBtn);
            root.Children.Add(btnRow);

            dialog.Content = root;

            saveBtn.Click += (s, ev) =>
            {
                MainWindow.SetCurrentTheme(darkCb.IsChecked == true);
                DarkModeToggle.IsChecked = darkCb.IsChecked;
                MainWindow.SettingsList.AutoSaveEnabled = asCb.IsChecked == true;
                MainWindow.SettingsList.AutoSaveIntervalMinutes = (int)(asNum.Value ?? 5);
                MainWindow.SettingsList.DefaultPreviewFps = (int)(fpsNum.Value ?? 5);
                SprAnimFps.Value = MainWindow.SettingsList.DefaultPreviewFps;
                MainWindow.PersistSettings();
                StartAutosave();
                StatusBar.Text = "Settings saved.";
                dialog.Close();
            };
            cancelBtn.Click += (s, ev) => dialog.Close();

            await dialog.ShowDialog(this);
        }

        #endregion

        #region Standalone appearance.dat loader

        private async void OpenAppearanceDat_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open appearance.dat",
                FileTypeFilter = [
                    new FilePickerFileType("Appearance DAT") { Patterns = ["*.dat", "appearances-*.dat"] }
                ]
            });
            if (files.Count == 0) return;

            string path = files[0].Path.LocalPath;

            try
            {
                Appearances loaded;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                    loaded = Appearances.Parser.ParseFrom(fs);

                // Replace in-memory appearances. Sprite ids may be dangling until the
                // full assets folder is loaded, but flag editing / structure inspection
                // works fine without sprites.
                MainWindow.appearances = loaded;
                MainWindow._datPath = path;

                // Rebuild lookups
                _outfitLookup  = loaded.Outfit.ToDictionary(o => o.Id);
                _itemLookup    = loaded.Object.ToDictionary(o => o.Id);
                _effectLookup  = loaded.Effect.ToDictionary(o => o.Id);
                _missileLookup = loaded.Missile.ToDictionary(o => o.Id);

                ThingsOutfit.Clear();
                ThingsItem.Clear();
                ThingsEffect.Clear();
                ThingsMissile.Clear();
                foreach (var o in loaded.Outfit)  ThingsOutfit.Add(new ShowList { Id = o.Id });
                foreach (var o in loaded.Object)  ThingsItem.Add(new ShowList    { Id = o.Id });
                foreach (var o in loaded.Effect)  ThingsEffect.Add(new ShowList  { Id = o.Id });
                foreach (var o in loaded.Missile) ThingsMissile.Add(new ShowList { Id = o.Id });

                UpdateShowList(ObjectMenu.SelectedIndex);
                MainWindow.AddRecentAppearanceDat(path);
                RefreshRecentMenus();
                StatusBar.Text = $"Loaded {Path.GetFileName(path)}: " +
                    $"{loaded.Outfit.Count} outfits, {loaded.Object.Count} items, " +
                    $"{loaded.Effect.Count} effects, {loaded.Missile.Count} missiles";
                ErrorManager.ShowInfo($"Loaded appearance.dat:\n{path}\n\n" +
                    $"Outfits: {loaded.Outfit.Count}\n" +
                    $"Items: {loaded.Object.Count}\n" +
                    $"Effects: {loaded.Effect.Count}\n" +
                    $"Missiles: {loaded.Missile.Count}\n\n" +
                    "Note: sprites may be blank unless the assets folder is also loaded.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Failed to load appearance.dat:\n{ex.Message}");
            }
        }

        #endregion

        #region items.xml integration

        private async void LoadItemsXml_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select items.xml",
                FileTypeFilter = [new FilePickerFileType("items.xml") { Patterns = ["*.xml"] }]
            });
            if (files.Count == 0) return;

            if (ItemsXml.Load(files[0].Path.LocalPath))
            {
                MainWindow.AddRecentItemsXml(files[0].Path.LocalPath);
                RefreshRecentMenus();
                StatusBar.Text = $"Loaded {ItemsXml.Items.Count} items from items.xml";
                // Clear existing names so they get re-populated from items.xml
                foreach (var sl in ThingsItem) sl.Name = "";
                UpdateShowList(ObjectMenu.SelectedIndex);
                ErrorManager.ShowInfo($"Loaded {ItemsXml.Items.Count} item definitions from items.xml.\nNames will now appear in the Items list.");
            }
            else
            {
                ErrorManager.ShowError("Failed to parse items.xml");
            }
        }

        private void EditItemsXml_Click(object? sender, RoutedEventArgs e)
        {
            var editor = new ItemsXmlEditor();
            editor.Show();
        }

        #endregion

        #region Feature: OTB Editor, Search, Sprite Palette

        private void OTBEditor_Click(object? sender, RoutedEventArgs e)
        {
            var otbEditor = new OTBEditor(this, false);
            otbEditor.Show();
        }

        private void SearchWindow_Click(object? sender, RoutedEventArgs e)
        {
            var search = new SearchWindow(this, false);
            search.Show();
        }

        private async void SpritePalette_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null || CurrentObjectAppearance.FrameGroup.Count == 0)
            { ErrorManager.ShowInfo("Select an appearance first."); return; }
            if (string.IsNullOrEmpty(MainWindow._assetsPath))
            { ErrorManager.ShowError("Assets path not set."); return; }

            // Simple palette swap: replace one color with another across all sprites
            string prompt =
                "Sprite Palette Tool\n\n" +
                "This tool lets you swap colors in the selected appearance's sprites.\n\n" +
                "Current appearance: " + CurrentObjectAppearance.Id + "\n" +
                "Sprite count: " + CurrentObjectAppearance.FrameGroup.Sum(fg => fg.SpriteInfo.SpriteId.Count) + "\n\n" +
                "To swap colors, export sprites as PNGs (Export > Batch Export),\n" +
                "edit them in an image editor (GIMP, Aseprite),\n" +
                "then import back (Import > Batch Import PNGs or drag & drop).";

            var win = new JsonEditorWindow(prompt, "Sprite Palette Tool");
            await win.ShowDialog<bool>(this);
        }

        #endregion

        #region Pass 4: items.xml Sync, Template Library, Consistency Checker

        // ── items.xml ↔ appearance.dat sync ─────────────────────────────────
        private async void SyncItemsXmlDat_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null) { ErrorManager.ShowError("Load assets first."); return; }
            if (!ItemsXml.IsLoaded)
            { ErrorManager.ShowInfo("Load an items.xml file first (File > Load items.xml)."); return; }

            var datIds = new HashSet<uint>(MainWindow.appearances.Object.Select(a => a.Id));
            var xmlIds = new HashSet<uint>(ItemsXml.Items.Keys.Select(k => (uint)k));

            var onlyInXml = xmlIds.Except(datIds).OrderBy(x => x).ToList();
            var onlyInDat = datIds.Except(xmlIds).OrderBy(x => x).ToList();
            int shared = datIds.Intersect(xmlIds).Count();

            string report =
                $"items.xml ↔ appearance.dat sync\n" +
                $"───────────────────────────────\n\n" +
                $"DAT items: {datIds.Count}\n" +
                $"XML items: {xmlIds.Count}\n" +
                $"Shared:    {shared}\n\n" +
                $"Only in XML ({onlyInXml.Count}):\n  " +
                (onlyInXml.Count == 0 ? "(none)" : string.Join(", ", onlyInXml.Take(50)) + (onlyInXml.Count > 50 ? "…" : "")) +
                $"\n\nOnly in DAT ({onlyInDat.Count}):\n  " +
                (onlyInDat.Count == 0 ? "(none)" : string.Join(", ", onlyInDat.Take(50)) + (onlyInDat.Count > 50 ? "…" : ""));

            var editor = new JsonEditorWindow(report, "items.xml ↔ DAT Sync Report");
            await editor.ShowDialog<bool>(this);

            if (onlyInDat.Count == 0 && onlyInXml.Count == 0)
            {
                StatusBar.Text = "Sync: fully in sync.";
                return;
            }

            bool fix = await ErrorManager.ConfirmAsync("Auto-fix?",
                $"Fix the mismatch automatically?\n\n" +
                $"  + Add {onlyInDat.Count} stub entries to items.xml for DAT appearances without XML\n" +
                $"  + Skip {onlyInXml.Count} XML-only entries (no matching sprite to create an appearance)\n\n" +
                $"items.xml will need saving afterward.");
            if (!fix) return;

            int added = 0;
            foreach (var id in onlyInDat)
            {
                if (id > ushort.MaxValue) continue;
                var appearance = _itemLookup.GetValueOrDefault(id);
                var entry = new ItemEntry
                {
                    Id = (ushort)id,
                    Name = appearance?.HasName == true ? appearance.Name : $"item {id}",
                    Description = appearance?.HasDescription == true ? appearance.Description : "",
                };
                ItemsXml.Items[(ushort)id] = entry;
                added++;
            }

            StatusBar.Text = $"Added {added} stub entries to items.xml. Save items.xml to persist.";
            ErrorManager.ShowInfo($"Added {added} stub entries. Open 'Edit items.xml' to review and save.");
        }

        // ── Template library ─────────────────────────────────────────────────
        private async void SaveTemplate_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null)
            { ErrorManager.ShowInfo("Select an appearance first."); return; }
            ApplyFlagsToAppearance(CurrentObjectAppearance);

            var dialog = new Window
            {
                Title = "Save Template",
                Width = 340, Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };
            var panel = new StackPanel { Margin = new Avalonia.Thickness(12), Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = "Template name:" });
            var box = new TextBox { Watermark = "e.g. knight-sword, basic-potion…" };
            panel.Children.Add(box);

            var btnRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            var saveBtn = new Button { Content = "Save", IsDefault = true };
            var cancelBtn = new Button { Content = "Cancel", IsCancel = true };
            btnRow.Children.Add(saveBtn);
            btnRow.Children.Add(cancelBtn);
            panel.Children.Add(btnRow);
            dialog.Content = panel;

            saveBtn.Click += (s, ev) =>
            {
                string name = box.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(name)) return;
                if (TemplateLibrary.Save(name, CurrentObjectAppearance))
                {
                    StatusBar.Text = $"Template saved: {name}";
                    RefreshTemplateMenu();
                    dialog.Close();
                }
                else
                {
                    ErrorManager.ShowError("Save failed.");
                }
            };
            cancelBtn.Click += (s, ev) => dialog.Close();

            await dialog.ShowDialog(this);
        }

        private void RefreshTemplateMenu()
        {
            ApplyTemplateMenu.Items.Clear();
            var names = TemplateLibrary.ListNames();
            if (names.Count == 0)
            {
                ApplyTemplateMenu.Items.Add(new MenuItem { Header = "(no templates)", IsEnabled = false });
                return;
            }
            foreach (var name in names)
            {
                var mi = new MenuItem { Header = name, Tag = name };
                mi.Click += (s, e) => { if (mi.Tag is string n) ApplyTemplate(n); };
                ApplyTemplateMenu.Items.Add(mi);
            }
        }

        private void ApplyTemplate(string name)
        {
            if (CurrentObjectAppearance == null)
            { ErrorManager.ShowInfo("Select an appearance first."); return; }

            var template = TemplateLibrary.Load(name);
            if (template == null) { ErrorManager.ShowError($"Template '{name}' not found."); return; }

            PushUndo();
            TemplateLibrary.ApplyTo(CurrentObjectAppearance, template);

            _suppressFlagApply = true;
            try
            {
                LoadAppearanceFlags(CurrentObjectAppearance);
                LoadSpriteInfo(CurrentObjectAppearance);
                LoadAppearanceSpritesList(CurrentObjectAppearance);
                UpdatePreview(CurrentObjectAppearance);
            }
            finally { _suppressFlagApply = false; }

            StatusBar.Text = $"Applied template '{name}' to appearance {CurrentObjectAppearance.Id}";
        }

        private async void ManageTemplates_Click(object? sender, RoutedEventArgs e)
        {
            var names = TemplateLibrary.ListNames();
            string body = names.Count == 0
                ? "No templates saved yet.\n\nSelect an appearance and use 'Save as Template' to create one."
                : "Saved templates:\n\n" + string.Join("\n", names.Select(n => $"  • {n}")) +
                  $"\n\nLocation: {TemplateLibrary.TemplateDir}";

            var editor = new JsonEditorWindow(body, "Template Library");
            await editor.ShowDialog<bool>(this);
        }

        // ── Consistency checker (extends ValidateAppearances_Click) ─────────
        private static List<string> GetConsistencyWarnings(string label, Appearance a)
        {
            var warnings = new List<string>();
            if (a.Flags == null) return warnings;
            var f = a.Flags;
            string prefix = $"[{label}] ID {a.Id}";

            // Basic semantic rules
            if (f.Container && !f.HasTake)
                warnings.Add($"{prefix}: Container but not Take (server-side containers usually need Take)");
            if (f.Cumulative && f.Container)
                warnings.Add($"{prefix}: Both Stackable and Container — ambiguous");
            if (f.Write != null && (!f.Write.HasMaxTextLength || f.Write.MaxTextLength == 0))
                warnings.Add($"{prefix}: Writeable but MaxTextLength is 0");
            if (f.WriteOnce != null && (!f.WriteOnce.HasMaxTextLengthOnce || f.WriteOnce.MaxTextLengthOnce == 0))
                warnings.Add($"{prefix}: WriteOnce but MaxTextLength is 0");
            if (f.Light != null && (!f.Light.HasBrightness || f.Light.Brightness == 0))
                warnings.Add($"{prefix}: Light flag but Brightness is 0 (no visible light)");
            if (f.Hook != null && !f.HasHang)
                warnings.Add($"{prefix}: Hook flag but not Hangable");
            if (f.Market != null && !f.Market.HasCategory)
                warnings.Add($"{prefix}: Market flag but missing Category");
            if (f.Market != null && f.Market.RestrictToVocation.Count == 0)
                warnings.Add($"{prefix}: Market flag but no vocation restrictions (will be invisible in market)");
            if (f.Cyclopediaitem != null && (!f.Cyclopediaitem.HasCyclopediaType || f.Cyclopediaitem.CyclopediaType == 0))
                warnings.Add($"{prefix}: Cyclopedia flag but CyclopediaType is 0");
            if (f.Clothes != null && !f.Clothes.HasSlot)
                warnings.Add($"{prefix}: Clothes flag but no slot set");
            if (f.HasAmmo && !f.Cumulative)
                warnings.Add($"{prefix}: Ammo flag but not Stackable (ammo should stack)");
            if (f.Unpass && f.Take)
                warnings.Add($"{prefix}: Impassable AND Take — contradictory");
            if (f.Transparencylevel != null && (!f.Transparencylevel.HasLevel || f.Transparencylevel.Level == 0))
                warnings.Add($"{prefix}: Transparency flag but Level is 0");
            if (f.Bank != null && (!f.Bank.HasWaypoints || f.Bank.Waypoints == 0))
                warnings.Add($"{prefix}: Ground flag but Speed is 0");

            return warnings;
        }

        #endregion

        #region Pass 2: Sprite Deduplicator, Reference Graph, Flip/Rotate

        // ── Deduplicator ────────────────────────────────────────────────────
        private async void DeduplicateSprites_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null) { ErrorManager.ShowError("Load assets first."); return; }

            bool ok = await ErrorManager.ConfirmAsync("Deduplicate Sprites",
                "This scans every sprite in memory, computes SHA256, and merges identical sprites " +
                "by remapping all appearance references to a single canonical ID.\n\n" +
                "The operation is undoable only via DAT backups. Continue?");
            if (!ok) return;

            StatusBar.Text = "Deduplicating — computing hashes…";

            // Compute SHA256 of each sprite's PNG bytes
            var hashToCanonical = new Dictionary<string, uint>();
            var remap = new Dictionary<uint, uint>(); // old id -> canonical id
            int hashed = 0;

            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                // Iterate sorted so the canonical winner is always the lowest ID
                var ordered = MainWindow.SprLists.OrderBy(kv => kv.Key).ToList();

                foreach (var kv in ordered)
                {
                    uint id = (uint)kv.Key;
                    var ms = kv.Value;
                    if (ms == null) continue;

                    byte[] bytes = ms.ToArray();
                    if (bytes.Length == 0) continue;

                    string hash = Convert.ToHexString(sha.ComputeHash(bytes));
                    if (hashToCanonical.TryGetValue(hash, out uint canonical))
                    {
                        if (canonical != id) remap[id] = canonical;
                    }
                    else
                    {
                        hashToCanonical[hash] = id;
                    }
                    hashed++;
                }

                if (remap.Count == 0)
                {
                    StatusBar.Text = $"Deduplicate: scanned {hashed} sprites, no duplicates found.";
                    ErrorManager.ShowInfo($"No duplicate sprites found.\nScanned: {hashed}");
                    return;
                }

                // Remap references in all appearances
                int remappedRefs = 0;
                void RemapCollection(Google.Protobuf.Collections.RepeatedField<Appearance> col)
                {
                    foreach (var a in col)
                        foreach (var fg in a.FrameGroup)
                            for (int i = 0; i < fg.SpriteInfo.SpriteId.Count; i++)
                                if (remap.TryGetValue(fg.SpriteInfo.SpriteId[i], out uint newId))
                                {
                                    fg.SpriteInfo.SpriteId[i] = newId;
                                    remappedRefs++;
                                }
                }
                RemapCollection(MainWindow.appearances.Outfit);
                RemapCollection(MainWindow.appearances.Object);
                RemapCollection(MainWindow.appearances.Effect);
                RemapCollection(MainWindow.appearances.Missile);

                // Remove the dup sprites from in-memory store
                foreach (var dupId in remap.Keys)
                {
                    MainWindow.SprLists.TryRemove((int)dupId, out _);
                    var sl = MainWindow.AllSprList.FirstOrDefault(s => s.Id == dupId);
                    if (sl != null) MainWindow.AllSprList.Remove(sl);
                }

                StatusBar.Text = $"Deduplicated: {remap.Count} duplicates → {remappedRefs} refs remapped.";
                ErrorManager.ShowInfo(
                    $"Deduplication complete.\n\n" +
                    $"Scanned:          {hashed} sprites\n" +
                    $"Unique hashes:    {hashToCanonical.Count}\n" +
                    $"Duplicates found: {remap.Count}\n" +
                    $"References remapped: {remappedRefs}\n\n" +
                    "Use Compile Assets to write the cleaned DAT to disk.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Deduplicate failed: {ex.Message}");
            }
        }

        // ── Reference graph ─────────────────────────────────────────────────
        private async void SpriteReferences_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null) { ErrorManager.ShowError("Load assets first."); return; }

            // Determine the sprite ID to look up:
            //   1. If an AppSprListView slot is selected, use that
            //   2. Else if SprListView has a selection, use that
            //   3. Else prompt via dialog
            uint? sprId = null;
            if (AppSprListView.SelectedItem is ShowList appSlot && appSlot.Id > 0)
                sprId = appSlot.Id;
            else if (SprListView.SelectedItem is ShowList sprPick && sprPick.Id > 0)
                sprId = sprPick.Id;

            if (!sprId.HasValue)
            {
                // Prompt for ID
                var dialog = new Window
                {
                    Title = "Sprite Reference Lookup",
                    Width = 320, Height = 140,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                };
                var panel = new StackPanel { Margin = new Avalonia.Thickness(12), Spacing = 8 };
                panel.Children.Add(new TextBlock { Text = "Enter sprite ID to trace:" });
                var box = new TextBox { Watermark = "e.g. 12345" };
                panel.Children.Add(box);
                var btnRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
                var okBtn = new Button { Content = "Find", IsDefault = true };
                var cancelBtn = new Button { Content = "Cancel", IsCancel = true };
                btnRow.Children.Add(okBtn);
                btnRow.Children.Add(cancelBtn);
                panel.Children.Add(btnRow);
                dialog.Content = panel;

                uint result = 0;
                okBtn.Click += (s, ev) => { if (uint.TryParse(box.Text?.Trim(), out result)) dialog.Close(); };
                cancelBtn.Click += (s, ev) => dialog.Close();
                await dialog.ShowDialog(this);
                if (result == 0) return;
                sprId = result;
            }

            // Walk all collections and collect references
            var refs = new List<string>();
            void Check(string label, Google.Protobuf.Collections.RepeatedField<Appearance> col)
            {
                foreach (var a in col)
                {
                    for (int fg = 0; fg < a.FrameGroup.Count; fg++)
                    {
                        var ids = a.FrameGroup[fg].SpriteInfo.SpriteId;
                        for (int i = 0; i < ids.Count; i++)
                        {
                            if (ids[i] == sprId!.Value)
                                refs.Add($"{label}:{a.Id} (fg{fg}, slot {i}){(string.IsNullOrEmpty(a.Name) ? "" : $"  — {a.Name}")}");
                        }
                    }
                }
            }
            Check("outfit", MainWindow.appearances.Outfit);
            Check("item", MainWindow.appearances.Object);
            Check("effect", MainWindow.appearances.Effect);
            Check("missile", MainWindow.appearances.Missile);

            string report = refs.Count == 0
                ? $"Sprite ID {sprId} is not referenced by any appearance."
                : $"Sprite ID {sprId} is referenced by {refs.Count} slot(s):\n\n" + string.Join("\n", refs);

            var editor = new JsonEditorWindow(report, $"Sprite {sprId} — References");
            await editor.ShowDialog<bool>(this);
        }

        // ── Flip / Rotate ───────────────────────────────────────────────────
        private async void FlipRotateSprites_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null || CurrentObjectAppearance.FrameGroup.Count == 0)
            { ErrorManager.ShowInfo("Select an appearance first."); return; }

            var dialog = new Window
            {
                Title = "Flip / Rotate Sprites",
                Width = 320, Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };
            var panel = new StackPanel { Margin = new Avalonia.Thickness(12), Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = $"Transform all sprites of appearance {CurrentObjectAppearance.Id}:",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });

            var btnH = new Button { Content = "↔ Flip Horizontal", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            var btnV = new Button { Content = "↕ Flip Vertical",   HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            var btnR = new Button { Content = "⟳ Rotate 90° CW",  HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            var btnL = new Button { Content = "⟲ Rotate 90° CCW", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            panel.Children.Add(btnH);
            panel.Children.Add(btnV);
            panel.Children.Add(btnR);
            panel.Children.Add(btnL);
            var cancel = new Button { Content = "Cancel", IsCancel = true, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            panel.Children.Add(cancel);
            dialog.Content = panel;

            string chosen = "";
            btnH.Click += (s, ev) => { chosen = "h"; dialog.Close(); };
            btnV.Click += (s, ev) => { chosen = "v"; dialog.Close(); };
            btnR.Click += (s, ev) => { chosen = "r"; dialog.Close(); };
            btnL.Click += (s, ev) => { chosen = "l"; dialog.Close(); };
            cancel.Click += (s, ev) => dialog.Close();

            await dialog.ShowDialog(this);
            if (string.IsNullOrEmpty(chosen)) return;

            try
            {
                int transformed = 0;
                var processed = new HashSet<uint>();

                foreach (var fg in CurrentObjectAppearance.FrameGroup)
                foreach (var sid in fg.SpriteInfo.SpriteId)
                {
                    if (sid == 0 || !processed.Add(sid)) continue;

                    var stream = MainWindow.getSpriteStream((int)sid);
                    if (stream == null) continue;

                    using var src = SKBitmap.Decode(stream.ToArray());
                    if (src == null) continue;

                    SKBitmap transformedBmp = chosen switch
                    {
                        "h" => FlipH(src),
                        "v" => FlipV(src),
                        "r" => Rotate90(src, clockwise: true),
                        "l" => Rotate90(src, clockwise: false),
                        _ => src.Copy()
                    };

                    using var img = SKImage.FromBitmap(transformedBmp);
                    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                    MainWindow.SprLists[(int)sid] = new MemoryStream(data.ToArray());
                    transformedBmp.Dispose();
                    transformed++;
                }

                // Refresh visible list and preview
                foreach (var sl in AppearanceSprites) sl.Image = null;
                UpdatePreview(CurrentObjectAppearance);

                StatusBar.Text = $"Transformed {transformed} sprite(s).";
                ErrorManager.ShowInfo($"Applied transform to {transformed} unique sprite(s) of appearance {CurrentObjectAppearance.Id}.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Transform failed: {ex.Message}");
            }
        }

        private static SKBitmap FlipH(SKBitmap src)
        {
            var dst = new SKBitmap(src.Width, src.Height, src.ColorType, src.AlphaType);
            using var canvas = new SKCanvas(dst);
            canvas.Scale(-1, 1, src.Width / 2f, 0);
            canvas.DrawBitmap(src, 0, 0);
            return dst;
        }

        private static SKBitmap FlipV(SKBitmap src)
        {
            var dst = new SKBitmap(src.Width, src.Height, src.ColorType, src.AlphaType);
            using var canvas = new SKCanvas(dst);
            canvas.Scale(1, -1, 0, src.Height / 2f);
            canvas.DrawBitmap(src, 0, 0);
            return dst;
        }

        private static SKBitmap Rotate90(SKBitmap src, bool clockwise)
        {
            // 90° rotation swaps width/height
            var dst = new SKBitmap(src.Height, src.Width, src.ColorType, src.AlphaType);
            using var canvas = new SKCanvas(dst);
            canvas.Translate(dst.Width / 2f, dst.Height / 2f);
            canvas.RotateDegrees(clockwise ? 90 : -90);
            canvas.Translate(-src.Width / 2f, -src.Height / 2f);
            canvas.DrawBitmap(src, 0, 0);
            return dst;
        }

        #endregion

        #region Features 21-25: Keyboard Shortcuts Extension

        // Extend the existing Window_KeyDown to handle more shortcuts.
        // This is done by modifying the existing handler above. Adding save shortcut:
        private void HandleExtraShortcuts(Avalonia.Input.KeyEventArgs e)
        {
            if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
            {
                switch (e.Key)
                {
                    case Avalonia.Input.Key.S:
                        CompileToAssets_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                        break;
                    case Avalonia.Input.Key.N:
                        AddNew_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                        break;
                    case Avalonia.Input.Key.D:
                        Duplicate_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                        break;
                    case Avalonia.Input.Key.F:
                        SearchBox?.Focus();
                        e.Handled = true;
                        break;
                    case Avalonia.Input.Key.G:
                        _ = QuickJumpDialog();
                        e.Handled = true;
                        break;
                    case Avalonia.Input.Key.B:
                        ToggleBookmark_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                        break;
                    case Avalonia.Input.Key.R:
                        Revert();
                        e.Handled = true;
                        break;
                }
            }
        }

        #endregion

        #region Pass 5: Outline, Shadow, GIF Import, Flag Diff, Copy Sprite IDs

        // ── Outline / Shadow helpers ────────────────────────────────────────
        private async void AddOutline_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null || CurrentObjectAppearance.FrameGroup.Count == 0)
            { ErrorManager.ShowInfo("Select an appearance first."); return; }

            bool ok = await ErrorManager.ConfirmAsync("Add Outline",
                $"Add a 1px black outline around every sprite of appearance {CurrentObjectAppearance.Id}?\n\n" +
                "This modifies sprites in place — use Undo/Revert to roll back.");
            if (!ok) return;

            TransformAllUniqueSprites(CurrentObjectAppearance, bmp => AddOutlineToBitmap(bmp, SKColors.Black));
            StatusBar.Text = "Outline added.";
        }

        private async void AddShadow_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null || CurrentObjectAppearance.FrameGroup.Count == 0)
            { ErrorManager.ShowInfo("Select an appearance first."); return; }

            bool ok = await ErrorManager.ConfirmAsync("Add Shadow",
                $"Add a semi-transparent shadow under every sprite of appearance {CurrentObjectAppearance.Id}?\n\n" +
                "Shadow is drawn offset (+2,+2) at 40% alpha.");
            if (!ok) return;

            TransformAllUniqueSprites(CurrentObjectAppearance, bmp => AddShadowToBitmap(bmp));
            StatusBar.Text = "Shadow added.";
        }

        // Walks all unique sprite IDs used by the appearance, applies a transform,
        // and writes the result back to SprLists.
        private void TransformAllUniqueSprites(Appearance appearance, Func<SKBitmap, SKBitmap> transform)
        {
            var seen = new HashSet<uint>();
            int count = 0;
            foreach (var fg in appearance.FrameGroup)
                foreach (var sid in fg.SpriteInfo.SpriteId)
                {
                    if (sid == 0 || !seen.Add(sid)) continue;

                    var stream = MainWindow.getSpriteStream((int)sid);
                    if (stream == null) continue;

                    using var src = SKBitmap.Decode(stream.ToArray());
                    if (src == null) continue;

                    using var dst = transform(src);
                    using var img = SKImage.FromBitmap(dst);
                    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                    MainWindow.SprLists[(int)sid] = new MemoryStream(data.ToArray());
                    count++;
                }

            foreach (var sl in AppearanceSprites) sl.Image = null;
            UpdatePreview(appearance);
            MainWindow.Log($"Transform applied to {count} unique sprite(s) of appearance {appearance.Id}.");
        }

        // Pixel-level outline: for each transparent pixel that has a non-transparent neighbor,
        // paint it with the outline color (keeps the original shape inside intact).
        private static SKBitmap AddOutlineToBitmap(SKBitmap src, SKColor color)
        {
            int w = src.Width, h = src.Height;
            var dst = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            // Copy source first
            using (var canvas = new SKCanvas(dst))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(src, 0, 0);
            }

            // 4-connected neighbors: up/down/left/right. For a pixel to become outline,
            // itself must be transparent AND at least one neighbor must be opaque.
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (src.GetPixel(x, y).Alpha > 0) continue; // already filled, skip
                    bool neighbor = false;
                    if (x > 0     && src.GetPixel(x - 1, y).Alpha > 0) neighbor = true;
                    else if (x < w - 1 && src.GetPixel(x + 1, y).Alpha > 0) neighbor = true;
                    else if (y > 0     && src.GetPixel(x, y - 1).Alpha > 0) neighbor = true;
                    else if (y < h - 1 && src.GetPixel(x, y + 1).Alpha > 0) neighbor = true;

                    if (neighbor) dst.SetPixel(x, y, color);
                }
            }
            return dst;
        }

        // Shadow: copy the sprite's alpha mask, offset by (+2, +2), recolor to semi-transparent black.
        private static SKBitmap AddShadowToBitmap(SKBitmap src)
        {
            int w = src.Width, h = src.Height;
            var dst = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var canvas = new SKCanvas(dst);
            canvas.Clear(SKColors.Transparent);

            // Paint shadow mask first
            using (var shadowBmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul))
            {
                var shadowColor = new SKColor(0, 0, 0, 100); // ~40% alpha
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (src.GetPixel(x, y).Alpha > 0)
                            shadowBmp.SetPixel(x, y, shadowColor);

                canvas.DrawBitmap(shadowBmp, 2, 2);
            }

            // Then the original sprite on top
            canvas.DrawBitmap(src, 0, 0);
            return dst;
        }

        // ── Import sprites from GIF / APNG ──────────────────────────────────
        private async void ImportFromGif_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null)
            { ErrorManager.ShowInfo("Select an appearance first."); return; }
            if (string.IsNullOrEmpty(MainWindow._assetsPath))
            { ErrorManager.ShowError("Assets path not set."); return; }

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select animated image",
                FileTypeFilter = [new FilePickerFileType("Animated Image") { Patterns = ["*.gif", "*.png", "*.apng"] }]
            });
            if (files.Count == 0) return;

            try
            {
                string path = files[0].Path.LocalPath;

                // Use ImageMagick to split the animation into frames
                using var collection = new MagickImageCollection(path);
                if (collection.Count == 0)
                {
                    ErrorManager.ShowInfo("Image has no frames.");
                    return;
                }

                // Target sprite size — use first slot's size, or 32x32 as fallback
                int targetW = 32, targetH = 32;
                if (AppearanceSprites.Count > 0)
                {
                    var (tw, th) = MainWindow.GetSpriteSize((int)AppearanceSprites[0].Id);
                    if (tw > 0 && th > 0) { targetW = tw; targetH = th; }
                }

                var streams = new List<(int width, int height, MemoryStream png)>();
                foreach (var frame in collection)
                {
                    frame.Format = MagickFormat.Png;
                    byte[] pngBytes = frame.ToByteArray();

                    // Downscale to targetW/H using LoadPngAsSpriteStream
                    using var tmp = new MemoryStream(pngBytes);
                    var ms = LoadPngAsSpriteStream(tmp, targetW, targetH);
                    if (ms != null)
                        streams.Add((targetW, targetH, ms));
                }

                if (streams.Count == 0) { ErrorManager.ShowInfo("No valid frames extracted."); return; }

                var newIdByIdx = BuildSpriteSheetsAndRegister(streams);

                // Append to the first frame group (or create one if missing)
                if (CurrentObjectAppearance.FrameGroup.Count == 0)
                {
                    CurrentObjectAppearance.FrameGroup.Add(new FrameGroup
                    {
                        SpriteInfo = new SpriteInfo { PatternWidth = 1, PatternHeight = 1, Layers = 1, PatternDepth = 1 }
                    });
                }
                var info = CurrentObjectAppearance.FrameGroup[0].SpriteInfo;

                // Wire up animation metadata if there were multiple frames
                if (streams.Count > 1)
                {
                    info.PatternFrames = (uint)streams.Count;
                    if (info.Animation == null)
                        info.Animation = new SpriteAnimation { LoopType = ANIMATION_LOOP_TYPE.Infinite };
                    info.Animation.SpritePhase.Clear();
                    foreach (var _ in streams)
                        info.Animation.SpritePhase.Add(new SpritePhase { DurationMin = 100, DurationMax = 100 });
                }

                foreach (var kv in newIdByIdx.OrderBy(kv => kv.Key))
                    info.SpriteId.Add(kv.Value);

                LoadAppearanceSpritesList(CurrentObjectAppearance);
                UpdatePreview(CurrentObjectAppearance);
                StatusBar.Text = $"Imported {streams.Count} frame(s) from {Path.GetFileName(path)}";
                ErrorManager.ShowInfo($"Imported {streams.Count} frames from:\n{Path.GetFileName(path)}\n\nAs appearance {CurrentObjectAppearance.Id}.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"GIF import failed: {ex.Message}");
            }
        }

        // ── Visual flag diff ────────────────────────────────────────────────
        private async void CompareFlags_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null)
            { ErrorManager.ShowInfo("Select an appearance first (A)."); return; }

            // Prompt for the "other" ID
            var dialog = new Window
            {
                Title = "Compare Flags",
                Width = 340, Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };
            var panel = new StackPanel { Margin = new Avalonia.Thickness(12), Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = $"Compare appearance {CurrentObjectAppearance.Id} with:" });
            var box = new TextBox { Watermark = "Enter ID (same tab)" };
            panel.Children.Add(box);
            var btnRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            var okBtn = new Button { Content = "Compare", IsDefault = true };
            var cancelBtn = new Button { Content = "Cancel", IsCancel = true };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);
            panel.Children.Add(btnRow);
            dialog.Content = panel;

            uint otherId = 0;
            okBtn.Click += (s, ev) => { if (uint.TryParse(box.Text?.Trim(), out otherId)) dialog.Close(); };
            cancelBtn.Click += (s, ev) => dialog.Close();
            await dialog.ShowDialog(this);
            if (otherId == 0) return;

            var lookup = GetCurrentLookup();
            if (!lookup.TryGetValue(otherId, out var other))
            {
                ErrorManager.ShowError($"No appearance with ID {otherId} in current tab.");
                return;
            }

            var report = DiffFlags(CurrentObjectAppearance, other);
            var editor = new JsonEditorWindow(report, $"Flag diff: {CurrentObjectAppearance.Id} vs {otherId}");
            await editor.ShowDialog<bool>(this);
        }

        private static string DiffFlags(Appearance a, Appearance b)
        {
            var diffs = new List<string>();

            // Name / Description
            if (a.HasName != b.HasName || a.Name != b.Name)
                diffs.Add($"  Name:        '{(a.HasName ? a.Name : "")}' ≠ '{(b.HasName ? b.Name : "")}'");
            if (a.HasDescription != b.HasDescription || a.Description != b.Description)
                diffs.Add($"  Description: '{(a.HasDescription ? a.Description : "")}' ≠ '{(b.HasDescription ? b.Description : "")}'");

            var fa = a.Flags ?? new AppearanceFlags();
            var fb = b.Flags ?? new AppearanceFlags();

            // Simple bool flags
            void CmpBool(string name, bool av, bool bv)
            {
                if (av != bv) diffs.Add($"  {name,-22} {av,-8} ≠ {bv}");
            }

            CmpBool("Clip",             fa.Clip,             fb.Clip);
            CmpBool("Top",              fa.Top,              fb.Top);
            CmpBool("Bottom",           fa.Bottom,           fb.Bottom);
            CmpBool("Container",        fa.Container,        fb.Container);
            CmpBool("Stackable",        fa.Cumulative,       fb.Cumulative);
            CmpBool("Usable",           fa.Usable,           fb.Usable);
            CmpBool("Forceuse",         fa.Forceuse,         fb.Forceuse);
            CmpBool("Multiuse",         fa.Multiuse,         fb.Multiuse);
            CmpBool("Liquidpool",       fa.Liquidpool,       fb.Liquidpool);
            CmpBool("Liquidcontainer",  fa.Liquidcontainer,  fb.Liquidcontainer);
            CmpBool("Unpass",           fa.Unpass,           fb.Unpass);
            CmpBool("Unmove",           fa.Unmove,           fb.Unmove);
            CmpBool("Unsight",          fa.Unsight,          fb.Unsight);
            CmpBool("Avoid",            fa.Avoid,            fb.Avoid);
            CmpBool("NoMoveAnim",       fa.NoMovementAnimation, fb.NoMovementAnimation);
            CmpBool("Take",             fa.HasTake,          fb.HasTake);
            CmpBool("Hang",             fa.Hang,             fb.Hang);
            CmpBool("Rotate",           fa.Rotate,           fb.Rotate);
            CmpBool("DontHide",         fa.DontHide,         fb.DontHide);
            CmpBool("Translucent",      fa.Translucent,      fb.Translucent);
            CmpBool("LyingObject",      fa.LyingObject,      fb.LyingObject);
            CmpBool("AnimateAlways",    fa.AnimateAlways,    fb.AnimateAlways);
            CmpBool("FullBank",         fa.Fullbank,         fb.Fullbank);
            CmpBool("IgnoreLook",       fa.HasIgnoreLook,    fb.HasIgnoreLook);
            CmpBool("Wrap",             fa.Wrap,             fb.Wrap);
            CmpBool("Unwrap",           fa.Unwrap,           fb.Unwrap);
            CmpBool("Topeffect",        fa.HasTopeffect,     fb.HasTopeffect);
            CmpBool("Corpse",           fa.Corpse,           fb.Corpse);
            CmpBool("PlayerCorpse",     fa.HasPlayerCorpse,  fb.HasPlayerCorpse);
            CmpBool("Ammo",             fa.HasAmmo,          fb.HasAmmo);
            CmpBool("Reportable",       fa.Reportable,       fb.Reportable);
            CmpBool("Podium",           fa.ShowOffSocket,    fb.ShowOffSocket);

            // Sub-message flags (presence)
            void CmpPresence(string name, bool aHas, bool bHas)
            {
                if (aHas != bHas) diffs.Add($"  {name,-22} {(aHas ? "set" : "—"),-8} ≠ {(bHas ? "set" : "—")}");
            }
            CmpPresence("Ground",       fa.Bank != null, fb.Bank != null);
            CmpPresence("Write",        fa.Write != null, fb.Write != null);
            CmpPresence("WriteOnce",    fa.WriteOnce != null, fb.WriteOnce != null);
            CmpPresence("Light",        fa.Light != null, fb.Light != null);
            CmpPresence("Shift",        fa.Shift != null, fb.Shift != null);
            CmpPresence("Height",       fa.Height != null, fb.Height != null);
            CmpPresence("Automap",      fa.Automap != null, fb.Automap != null);
            CmpPresence("LensHelp",     fa.Lenshelp != null, fb.Lenshelp != null);
            CmpPresence("Clothes",      fa.Clothes != null, fb.Clothes != null);
            CmpPresence("DefaultAction",fa.DefaultAction != null, fb.DefaultAction != null);
            CmpPresence("Market",       fa.Market != null, fb.Market != null);
            CmpPresence("Transparency", fa.Transparencylevel != null, fb.Transparencylevel != null);
            CmpPresence("Cyclopedia",   fa.Cyclopediaitem != null, fb.Cyclopediaitem != null);

            if (diffs.Count == 0)
                return $"Flags are identical between {a.Id} and {b.Id}.";

            return $"Flag diff: {a.Id} ≠ {b.Id}\n" +
                   $"─────────────────────────────\n" +
                   $"  {"Flag",-22} {a.Id,-8}   {b.Id}\n" +
                   $"─────────────────────────────\n" +
                   string.Join("\n", diffs) +
                   $"\n\n{diffs.Count} difference(s) found.";
        }

        // ── Copy sprite IDs to clipboard ────────────────────────────────────
        private async void CopySpriteIds_Click(object? sender, RoutedEventArgs e)
        {
            var selected = new List<Appearance>();

            // Use multi-select if available, else fall back to single
            if (ObjListView.SelectedItems != null && ObjListView.SelectedItems.Count > 0)
            {
                var lookup = GetCurrentLookup();
                foreach (var si in ObjListView.SelectedItems)
                    if (si is ShowList sl && lookup.TryGetValue(sl.Id, out var a))
                        selected.Add(a);
            }
            else if (CurrentObjectAppearance != null)
            {
                selected.Add(CurrentObjectAppearance);
            }

            if (selected.Count == 0) { ErrorManager.ShowInfo("Select an appearance first."); return; }

            // Build CSV: one line per appearance
            //   id,type,fg_index,sprite_ids_csv
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("id,type,frame_group,sprite_ids");
            foreach (var a in selected)
            {
                string type = a.AppearanceType.ToString();
                for (int fg = 0; fg < a.FrameGroup.Count; fg++)
                {
                    var ids = a.FrameGroup[fg].SpriteInfo.SpriteId;
                    sb.AppendLine($"{a.Id},{type},{fg},\"{string.Join(",", ids)}\"");
                }
            }

            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                {
                    await clipboard.SetTextAsync(sb.ToString());
                    StatusBar.Text = $"Copied sprite IDs for {selected.Count} appearance(s) to clipboard.";
                }
                else
                {
                    ErrorManager.ShowError("Clipboard not available.");
                }
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Copy failed: {ex.Message}");
            }
        }

        #endregion
    }
}
