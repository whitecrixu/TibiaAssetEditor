using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Google.Protobuf;
using SkiaSharp;
using Tibia.Protobuf.Appearances;

namespace Assets_Editor
{
    public partial class LegacyDatEditor : Window
    {
        public ObservableCollection<ShowList> ThingsOutfit = [];
        public ObservableCollection<ShowList> ThingsItem = [];
        public ObservableCollection<ShowList> ThingsEffect = [];
        public ObservableCollection<ShowList> ThingsMissile = [];
        public Appearance? CurrentObjectAppearance;
        public AppearanceFlags? CurrentFlags = null;
        private bool isPageLoaded = false;
        private VersionInfo? loadedVersion;
        private static Appearance? _copiedFlagsSource = null;

        // texture tab state: which frame group is shown, the selected slot (w, h) of the slot grid,
        // and a guard so programmatic control updates do not write back into the object
        private int _viewGroup;
        private (int w, int h)? _selectedSlot;
        private bool _loadingTexture;
        private DispatcherTimer? _animationTimer;
        private const int MaxSlotGridSide = 16;

        // sprite drag from the sprite list onto a slot (raw pointer events: DoDragDrop is flaky on X11)
        private ShowList? _dragSprite;
        private Point _dragStart;
        private bool _dragging;

        // the appearances as last loaded or compiled; closing with other content asks first
        private byte[]? _compiledSnapshot;
        private bool _closeConfirmed;

        private bool HasUncompiledChanges()
        {
            var storage = MainWindow.MainSprStorage;
            lock (storage.Modified)
                if (storage.Modified.Count > 0 || storage.SpriteCount != storage.FileSpriteCount)
                    return true;
            return MainWindow.appearances != null && _compiledSnapshot != null
                && !MainWindow.appearances.ToByteArray().AsSpan().SequenceEqual(_compiledSnapshot);
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (_closeConfirmed)
                return;
            if (_compiling)
            {
                e.Cancel = true; // the files are being written
                return;
            }
            if (!HasUncompiledChanges())
                return;

            // closing this window ends the app: ask before throwing edits away
            e.Cancel = true;
            Dispatcher.UIThread.Post(async () =>
            {
                if (await ErrorManager.ConfirmAsync("Unsaved changes",
                        "There are edits that were not compiled into the client files yet. Close and discard them?"))
                {
                    _closeConfirmed = true;
                    Close();
                }
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            StopAnimation();
            base.OnClosed(e);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }

        public LegacyDatEditor()
        {
            InitializeComponent();
            DarkModeToggle.IsChecked = MainWindow.IsDarkModeSet();

            ObjListView.LayoutUpdated += ObjListView_LayoutUpdated;
            SprListView.LayoutUpdated += SprListView_LayoutUpdated;

            SprListView.AddHandler(PointerPressedEvent, SprListView_PointerPressed, RoutingStrategies.Tunnel, true);
            AddHandler(PointerMovedEvent, Window_PointerMoved, RoutingStrategies.Tunnel, true);
            AddHandler(PointerReleasedEvent, Window_PointerReleased, RoutingStrategies.Tunnel, true);
        }

        public LegacyDatEditor(Appearances appearances, VersionInfo versionInfo) : this()
        {
            foreach (var outfit in appearances.Outfit)
                ThingsOutfit.Add(new ShowList() { Id = outfit.Id });
            foreach (var item in appearances.Object)
                ThingsItem.Add(new ShowList() { Id = item.Id });
            foreach (var effect in appearances.Effect)
                ThingsEffect.Add(new ShowList() { Id = effect.Id });
            foreach (var missile in appearances.Missile)
                ThingsMissile.Add(new ShowList() { Id = missile.Id });

            SprListView.ItemsSource = MainWindow.AllSprList;
            loadedVersion = versionInfo;
            _compiledSnapshot = appearances.ToByteArray();
            isPageLoaded = true;
            UpdateShowList(ObjectMenu.SelectedIndex);

            Dispatcher.UIThread.Post(() =>
            {
                LoadVisibleObjectImages();
                LoadVisibleSpriteImages();
            }, DispatcherPriority.Loaded);
        }

        public bool IsCompiling => _compiling;

        public void UpdateShowList(int selection)
        {
            if (ObjListView == null) return;

            // drop the thumbnails of the list being left; its visible range must never be applied to
            // the next list (it may be shorter: ArgumentOutOfRange on the unload loop)
            if (ObjListView.ItemsSource is IList<ShowList> previous)
            {
                for (int i = Math.Max(0, _lastObjStart); i < _lastObjEnd && i < previous.Count; i++)
                    previous[i].Image = null;
            }
            _lastObjStart = -1;
            _lastObjEnd = -1;

            if (selection == 0)
                ObjListView.ItemsSource = ThingsOutfit;
            else if (selection == 1)
                ObjListView.ItemsSource = ThingsItem;
            else if (selection == 2)
                ObjListView.ItemsSource = ThingsEffect;
            else if (selection == 3)
                ObjListView.ItemsSource = ThingsMissile;

            if (ObjListView.ItemCount > 0)
            {
                ObjListView.SelectedIndex = 0;
                ObjListView.ScrollIntoView(0);
            }

            if (isPageLoaded)
                Dispatcher.UIThread.Post(LoadVisibleObjectImages, DispatcherPriority.Background);
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

            var collection = GetCurrentCollection();
            Appearance? appearance = collection != null ? FindAppearance(collection, ObjListView.SelectedIndex, selected.Id) : null;
            if (appearance == null) return;

            // AppearanceType is not stored in the dat; the category decides it
            CurrentObjectAppearance = appearance.Clone();
            CurrentObjectAppearance.AppearanceType = CategoryType(ObjectMenu.SelectedIndex);
            LoadCurrentObjectAppearances();
        }

        // Appearance shown in row `index` of a category list. Ids are positional, so the row (or the id
        // relative to the first id) is the index; the linear search is only a fallback.
        private static Appearance? FindAppearance(Google.Protobuf.Collections.RepeatedField<Appearance> collection, int index, uint id)
        {
            int found = FindAppearanceIndex(collection, index, id);
            return found >= 0 ? collection[found] : null;
        }

        private static int FindAppearanceIndex(Google.Protobuf.Collections.RepeatedField<Appearance> collection, int index, uint id)
        {
            if (index >= 0 && index < collection.Count && collection[index].Id == id)
                return index;
            if (collection.Count > 0)
            {
                long guess = (long)id - collection[0].Id;
                if (guess >= 0 && guess < collection.Count && collection[(int)guess].Id == id)
                    return (int)guess;
            }
            for (int i = 0; i < collection.Count; i++)
                if (collection[i].Id == id)
                    return i;
            return -1;
        }

        private static int FindShowListIndex(IList<ShowList> list, uint id)
        {
            if (list.Count > 0)
            {
                long guess = (long)id - list[0].Id;
                if (guess >= 0 && guess < list.Count && list[(int)guess].Id == id)
                    return (int)guess;
            }
            for (int i = 0; i < list.Count; i++)
                if (list[i].Id == id)
                    return i;
            return -1;
        }

        private static APPEARANCE_TYPE CategoryType(int category) => category switch
        {
            0 => APPEARANCE_TYPE.AppearanceOutfit,
            2 => APPEARANCE_TYPE.AppearanceEffect,
            3 => APPEARANCE_TYPE.AppearanceMissile,
            _ => APPEARANCE_TYPE.AppearanceObject,
        };

        private static Google.Protobuf.Collections.RepeatedField<Appearance>? GetCollection(int category)
        {
            if (MainWindow.appearances == null) return null;
            return category switch
            {
                0 => MainWindow.appearances.Outfit,
                1 => MainWindow.appearances.Object,
                2 => MainWindow.appearances.Effect,
                3 => MainWindow.appearances.Missile,
                _ => null
            };
        }

        private ObservableCollection<ShowList> GetShowList(int category)
        {
            return category switch
            {
                0 => ThingsOutfit,
                1 => ThingsItem,
                2 => ThingsEffect,
                3 => ThingsMissile,
                _ => ThingsOutfit
            };
        }

        private Google.Protobuf.Collections.RepeatedField<Appearance>? GetCurrentCollection() => GetCollection(ObjectMenu.SelectedIndex);

        private ObservableCollection<ShowList> GetCurrentShowList() => GetShowList(ObjectMenu.SelectedIndex);

        // first id of a category: items start at 100, everything else at 1
        private static uint NextFreeId(Google.Protobuf.Collections.RepeatedField<Appearance> collection, int category)
        {
            return collection.Count > 0 ? collection.Max(a => a.Id) + 1 : (category == 1 ? 100u : 1u);
        }

        // Selects (and scrolls to) an object of a category, switching the category if needed.
        public void SelectObject(int category, uint id)
        {
            if (category < 0 || category > 3) return;
            if (ObjectMenu.SelectedIndex != category)
                ObjectMenu.SelectedIndex = category;

            int index = FindShowListIndex(GetShowList(category), id);
            if (index < 0) return;
            ObjListView.SelectedIndex = index;
            ObjListView.ScrollIntoView(index);
        }

        // Adds an object imported from elsewhere under the next free id of its category and shows it.
        public uint AddImportedAppearance(Appearance appearance, int category)
        {
            var collection = GetCollection(category) ?? throw new InvalidOperationException("No dat loaded.");
            uint newId = NextFreeId(collection, category);
            appearance.Id = newId;
            appearance.AppearanceType = CategoryType(category);
            collection.Add(appearance);
            GetShowList(category).Add(new ShowList { Id = newId });
            SelectObject(category, newId);
            return newId;
        }

        // Appends a sprite to the loaded spr (compressed in its layout, null = empty) and to
        // MainWindow.AllSprList, which is indexed by sprite id. Call RefreshSpriteList afterwards.
        public static uint AddSprite(byte[]? compressedPixels)
        {
            uint id = MainWindow.MainSprStorage.AddSprite(compressedPixels);
            while (MainWindow.AllSprList.Count <= id)
                MainWindow.AllSprList.Add(new ShowList { Id = (uint)MainWindow.AllSprList.Count });
            return id;
        }

        // AllSprList is a plain List: the ListBox only sees new entries after ItemsSource is reset
        public void RefreshSpriteList(uint? focusId = null)
        {
            _lastSprStart = -1;
            _lastSprEnd = -1;
            SprListView.ItemsSource = null;
            SprListView.ItemsSource = MainWindow.AllSprList;
            if (focusId is uint id && id < MainWindow.AllSprList.Count)
            {
                SprListView.SelectedIndex = (int)id;
                SprListView.ScrollIntoView((int)id);
            }
            Dispatcher.UIThread.Post(LoadVisibleSpriteImages, DispatcherPriority.Background);
        }

        // lens help ids in the dat start at 1100 (ladders); the combo box lists them in order
        private const int LensHelpBaseId = 1100;

        private static int ComboIndexOrNone(ComboBox combo, int index)
        {
            return index >= 0 && index < combo.ItemCount ? index : -1;
        }

        private void LoadCurrentObjectAppearances()
        {
            if (CurrentObjectAppearance == null) return;
            var flags = CurrentObjectAppearance.Flags;

            A_FlagGround.IsChecked = flags.Bank != null;
            A_FlagGroundSpeed.Value = flags.Bank != null ? (int)flags.Bank.Waypoints : 0;
            A_FlagClip.IsChecked = flags.Clip;
            A_FlagBottom.IsChecked = flags.Bottom;
            A_FlagTop.IsChecked = flags.Top;
            A_FlagContainer.IsChecked = flags.Container;
            A_FlagCumulative.IsChecked = flags.Cumulative;
            A_FlagUsable.IsChecked = flags.Usable;
            A_FlagForceuse.IsChecked = flags.Forceuse;
            A_FlagMultiuse.IsChecked = flags.Multiuse;
            A_FlagWrite.IsChecked = flags.Write != null;
            A_FlagMaxTextLength.Value = (int)(flags.Write?.MaxTextLength ?? 0);
            A_FlagWriteOnce.IsChecked = flags.WriteOnce != null;
            A_FlagMaxTextLengthOnce.Value = (int)(flags.WriteOnce?.MaxTextLengthOnce ?? 0);
            A_FlagLiquidpool.IsChecked = flags.HasLiquidpool;
            A_FlagUnpass.IsChecked = flags.HasUnpass;
            A_FlagUnmove.IsChecked = flags.HasUnmove;
            A_FlagUnsight.IsChecked = flags.HasUnsight;
            A_FlagAvoid.IsChecked = flags.HasAvoid;
            A_FlagNoMoveAnimation.IsChecked = flags.HasNoMovementAnimation;
            A_FlagTake.IsChecked = flags.HasTake;
            A_FlagLiquidcontainer.IsChecked = flags.HasLiquidcontainer;
            A_FlagHang.IsChecked = flags.HasHang;
            A_FlagHookSouth.IsChecked = flags.HookSouth;
            A_FlagHookEast.IsChecked = flags.HookEast;
            A_FlagRotate.IsChecked = flags.HasRotate;
            A_FlagLight.IsChecked = flags.Light != null;
            A_FlagLightBrightness.Value = (int)(flags.Light?.Brightness ?? 0);
            A_FlagLightColor.Value = (int)(flags.Light?.Color ?? 0);
            A_FlagDontHide.IsChecked = flags.HasDontHide;
            A_FlagTranslucent.IsChecked = flags.HasTranslucent;
            A_FlagShift.IsChecked = flags.Shift != null;
            A_FlagShiftX.Value = flags.Shift?.X ?? 0;
            A_FlagShiftY.Value = flags.Shift?.Y ?? 0;
            A_FlagHeight.IsChecked = flags.Height != null;
            A_FlagElevation.Value = (int)(flags.Height?.Elevation ?? 0);
            A_FlagLyingObject.IsChecked = flags.HasLyingObject;
            A_FlagAnimateAlways.IsChecked = flags.HasAnimateAlways;
            A_FlagAutomap.IsChecked = flags.Automap != null;
            A_FlagAutomapColor.Value = (int)(flags.Automap?.Color ?? 0);
            A_FlagLenshelp.IsChecked = flags.Lenshelp != null;
            A_FlagLenshelpId.SelectedIndex = flags.Lenshelp != null ? ComboIndexOrNone(A_FlagLenshelpId, (int)flags.Lenshelp.Id - LensHelpBaseId) : -1;
            A_FlagFullGround.IsChecked = flags.HasFullbank;
            A_FlagIgnoreLook.IsChecked = flags.HasIgnoreLook;
            A_FlagClothes.IsChecked = flags.Clothes != null;
            A_FlagClothesSlot.SelectedIndex = flags.Clothes != null ? ComboIndexOrNone(A_FlagClothesSlot, (int)flags.Clothes.Slot) : -1;
            A_FlagDefaultAction.IsChecked = flags.DefaultAction != null;
            A_FlagDefaultActionType.SelectedIndex = flags.DefaultAction != null ? ComboIndexOrNone(A_FlagDefaultActionType, (int)flags.DefaultAction.Action) : -1;
            A_FlagMarket.IsChecked = flags.Market != null;
            A_FlagMarketCategory.SelectedIndex = flags.Market != null ? ComboIndexOrNone(A_FlagMarketCategory, (int)flags.Market.Category - 1) : -1;
            A_FlagWrap.IsChecked = flags.HasWrap;
            A_FlagUnwrap.IsChecked = flags.HasUnwrap;
            A_FlagTopeffect.IsChecked = flags.HasTopeffect;
            A_FlagWearout.IsChecked = flags.HasWearout;
            A_FlagName.Text = CurrentObjectAppearance.Name ?? "";

            LoadTextureView(true);

            A_FullInfo.Text = CurrentObjectAppearance.ToString();
            UpdateObjectHeader();
        }

        // the header above the tabs: what is selected and its shape
        private void UpdateObjectHeader()
        {
            var a = CurrentObjectAppearance;
            if (a == null)
            {
                ObjectTitle.Text = "No object selected";
                ObjectSubtitle.Text = "Pick an object on the left.";
                ObjectThumbnail.Source = null;
                return;
            }

            string category = ObjectMenu.SelectedIndex switch { 0 => "Outfit", 1 => "Item", 2 => "Effect", 3 => "Missile", _ => "Object" };
            ObjectTitle.Text = string.IsNullOrWhiteSpace(a.Name) ? $"{category} #{a.Id}" : $"{category} #{a.Id} — {a.Name}";
            if (a.FrameGroup.Count == 0)
            {
                ObjectSubtitle.Text = "No sprites";
                return;
            }
            var info = a.FrameGroup[0].SpriteInfo;
            string groups = a.FrameGroup.Count > 1 ? $", {a.FrameGroup.Count} frame groups" : "";
            ObjectSubtitle.Text = $"{info.PatternWidth}×{info.PatternHeight} tiles, {info.PatternLayers} layer(s), " +
                                  $"patterns {info.PatternX}×{info.PatternY}×{info.PatternZ}, {info.PatternFrames} frame(s){groups}";
        }

        private void ApplyCurrentFlagsFromUI()
        {
            if (CurrentObjectAppearance == null) return;
            var flags = CurrentObjectAppearance.Flags;

            flags.Bank = A_FlagGround.IsChecked == true
                ? new AppearanceFlagBank { Waypoints = (uint)(A_FlagGroundSpeed.Value ?? 0) }
                : null;

            if (A_FlagClip.IsChecked == true) flags.Clip = true; else flags.ClearClip();
            if (A_FlagBottom.IsChecked == true) flags.Bottom = true; else flags.ClearBottom();
            if (A_FlagTop.IsChecked == true) flags.Top = true; else flags.ClearTop();
            if (A_FlagContainer.IsChecked == true) flags.Container = true; else flags.ClearContainer();
            if (A_FlagCumulative.IsChecked == true) flags.Cumulative = true; else flags.ClearCumulative();
            if (A_FlagUsable.IsChecked == true) flags.Usable = true; else flags.ClearUsable();
            if (A_FlagForceuse.IsChecked == true) flags.Forceuse = true; else flags.ClearForceuse();
            if (A_FlagMultiuse.IsChecked == true) flags.Multiuse = true; else flags.ClearMultiuse();

            flags.Write = A_FlagWrite.IsChecked == true
                ? new AppearanceFlagWrite { MaxTextLength = (uint)(A_FlagMaxTextLength.Value ?? 0) }
                : null;
            flags.WriteOnce = A_FlagWriteOnce.IsChecked == true
                ? new AppearanceFlagWriteOnce { MaxTextLengthOnce = (uint)(A_FlagMaxTextLengthOnce.Value ?? 0) }
                : null;

            if (A_FlagLiquidpool.IsChecked == true) flags.Liquidpool = true; else flags.ClearLiquidpool();
            if (A_FlagUnpass.IsChecked == true) flags.Unpass = true; else flags.ClearUnpass();
            if (A_FlagUnmove.IsChecked == true) flags.Unmove = true; else flags.ClearUnmove();
            if (A_FlagUnsight.IsChecked == true) flags.Unsight = true; else flags.ClearUnsight();
            if (A_FlagAvoid.IsChecked == true) flags.Avoid = true; else flags.ClearAvoid();
            if (A_FlagNoMoveAnimation.IsChecked == true) flags.NoMovementAnimation = true; else flags.ClearNoMovementAnimation();
            if (A_FlagTake.IsChecked == true) flags.Take = true; else flags.ClearTake();
            if (A_FlagLiquidcontainer.IsChecked == true) flags.Liquidcontainer = true; else flags.ClearLiquidcontainer();
            if (A_FlagHang.IsChecked == true) flags.Hang = true; else flags.ClearHang();
            if (A_FlagHookSouth.IsChecked == true) flags.HookSouth = true; else flags.ClearHookSouth();
            if (A_FlagHookEast.IsChecked == true) flags.HookEast = true; else flags.ClearHookEast();
            if (A_FlagRotate.IsChecked == true) flags.Rotate = true; else flags.ClearRotate();

            flags.Light = A_FlagLight.IsChecked == true
                ? new AppearanceFlagLight
                {
                    Brightness = (uint)(A_FlagLightBrightness.Value ?? 0),
                    Color = (uint)(A_FlagLightColor.Value ?? 0)
                }
                : null;

            if (A_FlagDontHide.IsChecked == true) flags.DontHide = true; else flags.ClearDontHide();
            if (A_FlagTranslucent.IsChecked == true) flags.Translucent = true; else flags.ClearTranslucent();

            flags.Shift = A_FlagShift.IsChecked == true
                ? new AppearanceFlagShift
                {
                    X = (int)(A_FlagShiftX.Value ?? 0),
                    Y = (int)(A_FlagShiftY.Value ?? 0)
                }
                : null;
            flags.Height = A_FlagHeight.IsChecked == true
                ? new AppearanceFlagHeight { Elevation = (uint)(A_FlagElevation.Value ?? 0) }
                : null;

            if (A_FlagLyingObject.IsChecked == true) flags.LyingObject = true; else flags.ClearLyingObject();
            if (A_FlagAnimateAlways.IsChecked == true) flags.AnimateAlways = true; else flags.ClearAnimateAlways();

            flags.Automap = A_FlagAutomap.IsChecked == true
                ? new AppearanceFlagAutomap { Color = (uint)(A_FlagAutomapColor.Value ?? 0) }
                : null;
            // a value the combo box has no entry for is kept as it was instead of dropping the flag
            flags.Lenshelp = A_FlagLenshelp.IsChecked != true ? null
                : A_FlagLenshelpId.SelectedIndex >= 0 ? new AppearanceFlagLenshelp { Id = (uint)(A_FlagLenshelpId.SelectedIndex + LensHelpBaseId) }
                : flags.Lenshelp ?? new AppearanceFlagLenshelp { Id = (uint)LensHelpBaseId };

            if (A_FlagFullGround.IsChecked == true) flags.Fullbank = true; else flags.ClearFullbank();
            if (A_FlagIgnoreLook.IsChecked == true) flags.IgnoreLook = true; else flags.ClearIgnoreLook();

            flags.Clothes = A_FlagClothes.IsChecked != true ? null
                : A_FlagClothesSlot.SelectedIndex >= 0 ? new AppearanceFlagClothes { Slot = (uint)A_FlagClothesSlot.SelectedIndex }
                : flags.Clothes ?? new AppearanceFlagClothes { Slot = 0 };
            flags.DefaultAction = A_FlagDefaultAction.IsChecked != true ? null
                : A_FlagDefaultActionType.SelectedIndex >= 0 ? new AppearanceFlagDefaultAction { Action = (PLAYER_ACTION)A_FlagDefaultActionType.SelectedIndex }
                : flags.DefaultAction ?? new AppearanceFlagDefaultAction { Action = PLAYER_ACTION.None };

            // the UI only edits the category: trade-as / show-as / vocation / level must survive a save
            // (the client's market and the item's "show as" sprite depend on them)
            if (A_FlagMarket.IsChecked != true) {
                flags.Market = null;
            } else {
                var market = flags.Market?.Clone() ?? new AppearanceFlagMarket {
                    TradeAsObjectId = CurrentObjectAppearance.Id,
                    ShowAsObjectId = CurrentObjectAppearance.Id,
                };
                if (A_FlagMarketCategory.SelectedIndex >= 0)
                    market.Category = (ITEM_CATEGORY)(A_FlagMarketCategory.SelectedIndex + 1);
                else if (!market.HasCategory)
                    market.Category = ITEM_CATEGORY.Others;
                flags.Market = market;
            }

            if (A_FlagWrap.IsChecked == true) flags.Wrap = true; else flags.ClearWrap();
            if (A_FlagUnwrap.IsChecked == true) flags.Unwrap = true; else flags.ClearUnwrap();
            if (A_FlagTopeffect.IsChecked == true) flags.Topeffect = true; else flags.ClearTopeffect();
            if (A_FlagWearout.IsChecked == true) flags.Wearout = true; else flags.ClearWearout();

            CurrentObjectAppearance.Name = A_FlagName.Text ?? "";
        }

        #region Texture tab

        private bool IsOutfitCategory => ObjectMenu.SelectedIndex == 0;

        private FrameGroup? CurrentGroup
        {
            get
            {
                if (CurrentObjectAppearance == null || CurrentObjectAppearance.FrameGroup.Count == 0)
                    return null;
                return CurrentObjectAppearance.FrameGroup[Math.Clamp(_viewGroup, 0, CurrentObjectAppearance.FrameGroup.Count - 1)];
            }
        }

        private int ViewLayer => (int)(SlotLayer.Value ?? 0);
        private int ViewPatternX => (int)(SlotPatternX.Value ?? 0);
        private int ViewPatternY => (int)(SlotPatternY.Value ?? 0);
        private int ViewPatternZ => (int)(SlotPatternZ.Value ?? 0);
        private int ViewFrame => (int)SprFramesSlider.Value;

        private static string FrameGroupName(FIXED_FRAME_GROUP group) => group switch
        {
            FIXED_FRAME_GROUP.OutfitIdle => "Idle",
            FIXED_FRAME_GROUP.OutfitMoving => "Moving",
            FIXED_FRAME_GROUP.ObjectInitial => "Initial",
            _ => ((int)group).ToString()
        };

        // Fills the texture tab from CurrentObjectAppearance. resetView puts the frame group, slot
        // selectors and frame back to the start (a different object was selected).
        private void LoadTextureView(bool resetView)
        {
            _loadingTexture = true;
            try
            {
                var appearance = CurrentObjectAppearance;
                int groupCount = appearance?.FrameGroup.Count ?? 0;
                if (resetView)
                {
                    _viewGroup = 0;
                    _selectedSlot = null;
                }
                _viewGroup = Math.Clamp(_viewGroup, 0, Math.Max(0, groupCount - 1));

                FrameGroupSelector.ItemsSource = appearance?.FrameGroup
                    .Select((g, i) => $"{i}: {FrameGroupName(g.FixedFrameGroup)}").ToList() ?? [];
                FrameGroupSelector.SelectedIndex = groupCount > 0 ? _viewGroup : -1;
                FrameGroupSelector.IsEnabled = groupCount > 1;

                SpriteInfo? info = CurrentGroup?.SpriteInfo;
                if (info != null)
                {
                    LoadDimensionControls(info);
                    SlotLayer.Maximum = Math.Max(0, (int)info.PatternLayers - 1);
                    SlotPatternX.Maximum = Math.Max(0, (int)info.PatternX - 1);
                    SlotPatternY.Maximum = Math.Max(0, (int)info.PatternY - 1);
                    SlotPatternZ.Maximum = Math.Max(0, (int)info.PatternZ - 1);
                    SprFramesSlider.Maximum = Math.Max(0, (int)info.PatternFrames - 1);
                    if (resetView)
                    {
                        SlotLayer.Value = 0;
                        // outfits face south (pattern x 2) like the list thumbnail
                        SlotPatternX.Value = IsOutfitCategory && info.PatternX > 2 ? 2 : 0;
                        SlotPatternY.Value = 0;
                        SlotPatternZ.Value = 0;
                        SprFramesSlider.Value = 0;
                    }
                    if (_selectedSlot is (int w, int h) && (w >= info.PatternWidth || h >= info.PatternHeight))
                        _selectedSlot = null;
                }
                LoadAnimationControls();
            }
            finally
            {
                _loadingTexture = false;
            }

            UpdatePreview();
            BuildSlotGrid();
        }

        private void LoadDimensionControls(SpriteInfo info)
        {
            bool wasLoading = _loadingTexture;
            _loadingTexture = true;
            A_SprWidth.Value = info.PatternWidth;
            A_SprHeight.Value = info.PatternHeight;
            A_SprLayers.Value = info.PatternLayers;
            A_SprPaternX.Value = info.PatternX;
            A_SprPaternY.Value = info.PatternY;
            A_SprPaternZ.Value = info.PatternZ;
            A_SprAnimation.Value = info.PatternFrames;
            _loadingTexture = wasLoading;
        }

        private void LoadAnimationControls()
        {
            bool wasLoading = _loadingTexture;
            _loadingTexture = true;
            try
            {
                SpriteInfo? info = CurrentGroup?.SpriteInfo;
                bool frameDurations = MainWindow.GetCurrentPreset()?.FrameDurations ?? false;
                SpriteAnimation? animation = info != null && info.PatternFrames > 1 ? info.Animation : null;
                bool animated = animation != null;

                AnimationPanel.IsEnabled = animated && frameDurations;
                AnimationPanelTitle.Text = !frameDurations ? "Animation (this client stores no frame durations)"
                    : animated ? "Animation" : "Animation (single frame)";

                if (animation != null)
                {
                    AnimModeSelector.SelectedIndex = animation.AnimationMode == ANIMATION_ANIMATION_MODE.AnimationSynchronized ? 1 : 0;
                    AnimLoopCount.Value = animation.LoopCount;
                    AnimStartPhase.Value = Math.Min(animation.DefaultStartPhase, 255u);
                }
                else
                {
                    AnimModeSelector.SelectedIndex = -1;
                    AnimLoopCount.Value = 0;
                    AnimStartPhase.Value = 0;
                }
                LoadPhaseControls();
            }
            finally
            {
                _loadingTexture = wasLoading;
            }
        }

        private void LoadPhaseControls()
        {
            bool wasLoading = _loadingTexture;
            _loadingTexture = true;
            int frame = ViewFrame;
            SpritePhase? phase = CurrentGroup?.SpriteInfo?.Animation?.SpritePhase.ElementAtOrDefault(frame);
            PhaseLabel.Text = $"Phase {frame + 1} (ms):";
            PhaseDurationMin.Value = phase?.DurationMin ?? 0;
            PhaseDurationMax.Value = phase?.DurationMax ?? 0;
            _loadingTexture = wasLoading;
        }

        private void UpdatePreview()
        {
            if (CurrentObjectAppearance == null || CurrentObjectAppearance.FrameGroup.Count == 0)
            {
                SpriteViewerImge.Source = null;
                SprFrameLabel.Text = "";
                return;
            }

            try
            {
                SpriteInfo info = CurrentGroup!.SpriteInfo;
                // outfits show one layer (layer 1 is the colour mask); items stack all their layers
                MemoryStream stream = LegacyAppearance.GetObjectImage(CurrentObjectAppearance, MainWindow.MainSprStorage,
                    _viewGroup, ViewPatternX, ViewPatternY, ViewPatternZ, ViewFrame, IsOutfitCategory ? ViewLayer : -1);
                SpriteViewerImge.Source = Utils.MemoryStreamToBitmap(stream);
                ObjectThumbnail.Source = SpriteViewerImge.Source;
                SprFrameLabel.Text = $"{ViewFrame + 1}/{Math.Max(1u, info.PatternFrames)}";
            }
            catch (Exception ex)
            {
                SpriteViewerImge.Source = null;
                StatusBar.Text = $"Preview failed: {ex.Message}";
            }
        }

        // arrows pick the direction (pattern x: 0 north, 1 east, 2 south, 3 west) for outfits; for other
        // objects they step through pattern x the same way
        private void ChangeDirection(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out int dir)) return;
            SpriteInfo? info = CurrentGroup?.SpriteInfo;
            if (info == null || info.PatternX == 0) return;
            SlotPatternX.Value = dir % (int)info.PatternX;
        }

        private void SprFramesSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_loadingTexture || !isPageLoaded) return;
            UpdatePreview();
            BuildSlotGrid();
            LoadPhaseControls();
        }

        private void SlotView_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
        {
            if (_loadingTexture || !isPageLoaded) return;
            UpdatePreview();
            BuildSlotGrid();
        }

        private void FrameGroupSelector_Changed(object? sender, SelectionChangedEventArgs e)
        {
            if (_loadingTexture || !isPageLoaded || CurrentObjectAppearance == null) return;
            int selected = FrameGroupSelector.SelectedIndex;
            if (selected < 0 || selected == _viewGroup) return;

            // keep size changes typed for the group being left
            TryApplyTextureSize();
            _viewGroup = selected;
            _selectedSlot = null;
            LoadTextureView(false);
        }

        private void ApplyTextureSize_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentGroup == null) return;
            if (TryApplyTextureSize())
            {
                LoadTextureView(false);
                StatusBar.Text = "Size applied (not saved yet).";
            }
        }

        // Rebuilds the sprite list of the shown frame group from the size controls; existing sprites
        // keep their (w, h, layer, x, y, z, frame) slot.
        private bool TryApplyTextureSize()
        {
            SpriteInfo? info = CurrentGroup?.SpriteInfo;
            if (info == null) return true;
            try
            {
                LegacyAppearance.ResizeSpriteInfo(info,
                    (uint)(A_SprWidth.Value ?? 1), (uint)(A_SprHeight.Value ?? 1), (uint)(A_SprLayers.Value ?? 1),
                    (uint)(A_SprPaternX.Value ?? 1), (uint)(A_SprPaternY.Value ?? 1), (uint)(A_SprPaternZ.Value ?? 1),
                    (uint)(A_SprAnimation.Value ?? 1));
                return true;
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Invalid size: {ex.Message}");
                LoadDimensionControls(info);
                return false;
            }
        }

        // Applies the texture tab to CurrentObjectAppearance before a save.
        private bool ApplyTextureFromUI()
        {
            if (CurrentObjectAppearance == null) return false;
            if (!TryApplyTextureSize()) return false;

            // every group must carry one phase per frame (the writer needs them with frame durations)
            foreach (FrameGroup group in CurrentObjectAppearance.FrameGroup)
            {
                SpriteInfo info = group.SpriteInfo;
                LegacyAppearance.ResizeSpriteInfo(info, info.PatternWidth, info.PatternHeight, info.PatternLayers,
                    info.PatternX, info.PatternY, info.PatternZ, info.PatternFrames);
                if (info.Animation != null)
                    foreach (SpritePhase phase in info.Animation.SpritePhase)
                        if (phase.DurationMax < phase.DurationMin)
                            phase.DurationMax = phase.DurationMin;
            }
            return true;
        }

        private void AnimationField_Changed(object? sender, SelectionChangedEventArgs e) => StoreAnimationFromUI();

        private void AnimationValue_Changed(object? sender, NumericUpDownValueChangedEventArgs e) => StoreAnimationFromUI();

        private void StoreAnimationFromUI()
        {
            if (_loadingTexture || !isPageLoaded) return;
            SpriteAnimation? animation = CurrentGroup?.SpriteInfo?.Animation;
            if (animation == null) return;

            if (AnimModeSelector.SelectedIndex >= 0)
                animation.AnimationMode = AnimModeSelector.SelectedIndex == 1 ? ANIMATION_ANIMATION_MODE.AnimationSynchronized : ANIMATION_ANIMATION_MODE.AnimationAsynchronized;
            animation.LoopCount = (uint)(AnimLoopCount.Value ?? 0);
            animation.DefaultStartPhase = (uint)(AnimStartPhase.Value ?? 0);

            int frame = ViewFrame;
            if (frame < animation.SpritePhase.Count)
            {
                animation.SpritePhase[frame].DurationMin = (uint)(PhaseDurationMin.Value ?? 0);
                animation.SpritePhase[frame].DurationMax = (uint)(PhaseDurationMax.Value ?? 0);
            }
        }

        private void ApplyDurationToAllPhases_Click(object? sender, RoutedEventArgs e)
        {
            SpriteAnimation? animation = CurrentGroup?.SpriteInfo?.Animation;
            if (animation == null) return;
            uint min = (uint)(PhaseDurationMin.Value ?? 0);
            uint max = Math.Max(min, (uint)(PhaseDurationMax.Value ?? 0));
            foreach (SpritePhase phase in animation.SpritePhase)
            {
                phase.DurationMin = min;
                phase.DurationMax = max;
            }
            LoadPhaseControls();
            StatusBar.Text = $"All {animation.SpritePhase.Count} phases set to {min}-{max} ms (not saved yet).";
        }

        private void PlayAnimation_Click(object? sender, RoutedEventArgs e)
        {
            if (_animationTimer != null)
            {
                StopAnimation();
                return;
            }

            SpriteInfo? info = CurrentGroup?.SpriteInfo;
            if (info == null || info.PatternFrames <= 1)
            {
                StatusBar.Text = "This frame group has a single frame.";
                return;
            }

            _animationTimer = new DispatcherTimer { Interval = PhaseDuration(ViewFrame) };
            _animationTimer.Tick += AnimationTimer_Tick;
            _animationTimer.Start();
            PlayAnimationButton.Content = "■ Stop";
        }

        private void StopAnimation()
        {
            if (_animationTimer == null) return;
            _animationTimer.Stop();
            _animationTimer.Tick -= AnimationTimer_Tick;
            _animationTimer = null;
            PlayAnimationButton.Content = "▶ Play";
        }

        private void AnimationTimer_Tick(object? sender, EventArgs e)
        {
            SpriteInfo? info = CurrentGroup?.SpriteInfo;
            if (info == null || info.PatternFrames <= 1 || _animationTimer == null)
            {
                StopAnimation();
                return;
            }
            int next = (ViewFrame + 1) % (int)info.PatternFrames;
            SprFramesSlider.Value = next;
            _animationTimer.Interval = PhaseDuration(next);
        }

        // duration of an animation phase (random between min and max, like the client), 100 ms default
        private TimeSpan PhaseDuration(int frame)
        {
            SpritePhase? phase = CurrentGroup?.SpriteInfo?.Animation?.SpritePhase.ElementAtOrDefault(frame);
            long ms = 100;
            if (phase != null)
            {
                uint min = phase.DurationMin;
                uint max = Math.Max(min, phase.DurationMax);
                ms = min == max ? min : Random.Shared.NextInt64(min, (long)max + 1);
            }
            return TimeSpan.FromMilliseconds(Math.Clamp(ms, 20, 5000));
        }

        // index into SpriteId of slot (w, h) of the shown layer / pattern / frame, or -1
        private int SlotIndex(int w, int h)
        {
            FrameGroup? group = CurrentGroup;
            if (group == null) return -1;
            SpriteInfo info = group.SpriteInfo;
            if (w >= info.PatternWidth || h >= info.PatternHeight || info.PatternFrames == 0) return -1;
            int index = LegacyAppearance.GetSpriteIndex(group, w, h,
                Math.Min(ViewLayer, (int)info.PatternLayers - 1), Math.Min(ViewPatternX, (int)info.PatternX - 1),
                Math.Min(ViewPatternY, (int)info.PatternY - 1), Math.Min(ViewPatternZ, (int)info.PatternZ - 1),
                Math.Min(ViewFrame, (int)info.PatternFrames - 1));
            return index >= 0 && index < info.SpriteId.Count ? index : -1;
        }

        // One cell per tile of the shown view, laid out like the image: tile (w, h) sits at column
        // width-1-w, row height-1-h (the client draws tiles from the bottom-right).
        private void BuildSlotGrid()
        {
            SlotGrid.Children.Clear();
            SlotGrid.RowDefinitions.Clear();
            SlotGrid.ColumnDefinitions.Clear();

            SpriteInfo? info = CurrentGroup?.SpriteInfo;
            if (info == null)
            {
                SlotInfo.Text = "This object has no sprites.";
                return;
            }

            int width = (int)info.PatternWidth;
            int height = (int)info.PatternHeight;
            if (width > MaxSlotGridSide || height > MaxSlotGridSide)
            {
                SlotInfo.Text = $"{width}x{height} tiles is too large to show here.";
                return;
            }

            for (int r = 0; r < height; r++)
                SlotGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < width; c++)
                SlotGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            for (int h = 0; h < height; h++)
            {
                for (int w = 0; w < width; w++)
                {
                    int index = SlotIndex(w, h);
                    uint spriteId = index >= 0 ? info.SpriteId[index] : 0;

                    var image = new Image { Width = 32, Height = 32, Stretch = Stretch.None };
                    if (spriteId != 0)
                    {
                        try { image.Source = Utils.MemoryStreamToBitmap(MainWindow.MainSprStorage.getSpriteStream(spriteId)); }
                        catch { }
                    }

                    var cell = new Border
                    {
                        Width = 56,
                        Margin = new Thickness(1),
                        Padding = new Thickness(2),
                        BorderThickness = new Thickness(2),
                        Background = Brushes.Transparent,
                        Tag = (w, h),
                        Child = new StackPanel
                        {
                            Children =
                            {
                                image,
                                new TextBlock { Text = spriteId.ToString(), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center }
                            }
                        }
                    };
                    ToolTip.SetTip(cell, $"w {w}, h {h}: sprite {spriteId}");
                    cell.PointerPressed += SlotCell_PointerPressed;
                    Grid.SetRow(cell, height - 1 - h);
                    Grid.SetColumn(cell, width - 1 - w);
                    SlotGrid.Children.Add(cell);
                }
            }
            UpdateSlotSelectionVisual();
        }

        private void UpdateSlotSelectionVisual()
        {
            foreach (var child in SlotGrid.Children)
            {
                if (child is Border { Tag: ValueTuple<int, int> slot } cell)
                    cell.BorderBrush = _selectedSlot == slot ? Brushes.DodgerBlue : Brushes.Transparent;
            }

            if (_selectedSlot is (int w, int h))
            {
                int index = SlotIndex(w, h);
                uint id = index >= 0 ? CurrentGroup!.SpriteInfo.SpriteId[index] : 0;
                SlotInfo.Text = $"Slot w {w}, h {h} (index {index}) = sprite {id}. Double-click a sprite in the sprite list or drag it here to assign.";
            }
            else
            {
                SlotInfo.Text = "Select a slot, then double-click a sprite in the sprite list (or drag it onto the slot).";
            }
        }

        private void SlotCell_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border { Tag: ValueTuple<int, int> slot }) return;
            _selectedSlot = slot;
            UpdateSlotSelectionVisual();
        }

        private void AssignSpriteToSlot(uint spriteId)
        {
            if (_selectedSlot is not (int w, int h))
            {
                ErrorManager.ShowInfo("Select a slot in the Texture tab first.");
                return;
            }
            if (spriteId > MainWindow.MainSprStorage.SpriteCount)
            {
                ErrorManager.ShowError($"Sprite {spriteId} does not exist.");
                return;
            }
            int index = SlotIndex(w, h);
            if (index < 0) return;

            CurrentGroup!.SpriteInfo.SpriteId[index] = spriteId;
            UpdatePreview();
            BuildSlotGrid();
            StatusBar.Text = $"Slot w {w}, h {h} = sprite {spriteId} (not saved yet).";
        }

        private void AssignSpriteToSlot_Click(object? sender, RoutedEventArgs e)
        {
            if (SprListView.SelectedItem is not ShowList selected)
            {
                ErrorManager.ShowInfo("Select a sprite in the sprite list first.");
                return;
            }
            AssignSpriteToSlot(selected.Id);
        }

        private void ClearSlot_Click(object? sender, RoutedEventArgs e) => AssignSpriteToSlot(0);

        private void SprListView_DoubleTapped(object? sender, TappedEventArgs e)
        {
            if (SprListView.SelectedItem is ShowList selected && _selectedSlot != null)
                AssignSpriteToSlot(selected.Id);
        }

        private void SprListView_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            _dragging = false;
            _dragSprite = null;
            if (!e.GetCurrentPoint(SprListView).Properties.IsLeftButtonPressed) return;
            _dragSprite = (e.Source as StyledElement)?.DataContext as ShowList;
            _dragStart = e.GetPosition(this);
        }

        private void Window_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (_dragSprite == null || _dragging) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _dragSprite = null;
                return;
            }
            Point p = e.GetPosition(this);
            if (Math.Abs(p.X - _dragStart.X) + Math.Abs(p.Y - _dragStart.Y) > 8)
            {
                _dragging = true;
                Cursor = new Cursor(StandardCursorType.DragCopy);
            }
        }

        private void Window_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            ShowList? sprite = _dragSprite;
            bool dragging = _dragging;
            _dragSprite = null;
            _dragging = false;
            if (!dragging || sprite == null) return;
            Cursor = Cursor.Default;

            foreach (var child in SlotGrid.Children)
            {
                if (child is not Border { Tag: ValueTuple<int, int> slot } cell || !cell.IsEffectivelyVisible) continue;
                Point p = e.GetPosition(cell);
                if (p.X >= 0 && p.Y >= 0 && p.X < cell.Bounds.Width && p.Y < cell.Bounds.Height)
                {
                    _selectedSlot = slot;
                    AssignSpriteToSlot(sprite.Id);
                    return;
                }
            }
        }

        // Splits an image into 32x32 tiles, stores them as new sprites and puts them into the slots of
        // the shown layer / pattern / frame. Fully transparent tiles become sprite 0.
        private async void ImportImageToObject_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null || CurrentGroup == null) return;
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Import Image Into Object",
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("Image files") { Patterns = ["*.png", "*.bmp", "*.jpg", "*.jpeg", "*.gif"] }]
                });
                if (files.Count == 0) return;

                using SKBitmap? image = await DecodeImageFile(files[0]);
                if (image == null) { ErrorManager.ShowError("Failed to load image."); return; }
                if (image.Width % Sprite.DefaultSize != 0 || image.Height % Sprite.DefaultSize != 0)
                {
                    ErrorManager.ShowError($"The image is {image.Width}x{image.Height}; both sides must be multiples of {Sprite.DefaultSize}.");
                    return;
                }

                var tiles = LegacyAppearance.SplitIntoTiles(image, MainWindow.MainSprStorage.Transparency, out int tilesWide, out int tilesHigh);
                SpriteInfo info = CurrentGroup.SpriteInfo;
                if (tilesWide != info.PatternWidth || tilesHigh != info.PatternHeight)
                {
                    if (!await ErrorManager.ConfirmAsync("Import image",
                            $"The image is {tilesWide}x{tilesHigh} tiles but the object is {info.PatternWidth}x{info.PatternHeight}.\n\nResize the object to {tilesWide}x{tilesHigh}?"))
                        return;
                    LegacyAppearance.ResizeSpriteInfo(info, (uint)tilesWide, (uint)tilesHigh, info.PatternLayers,
                        info.PatternX, info.PatternY, info.PatternZ, info.PatternFrames);
                    LoadTextureView(false);
                }

                uint firstNew = 0, lastNew = 0;
                for (int h = 0; h < tilesHigh; h++)
                {
                    for (int w = 0; w < tilesWide; w++)
                    {
                        byte[]? tile = tiles[h * tilesWide + w];
                        uint id = 0;
                        if (tile != null)
                        {
                            id = AddSprite(tile);
                            if (firstNew == 0) firstNew = id;
                            lastNew = id;
                        }
                        int index = SlotIndex(w, h);
                        if (index >= 0)
                            info.SpriteId[index] = id;
                    }
                }

                RefreshSpriteList(lastNew != 0 ? lastNew : null);
                UpdatePreview();
                BuildSlotGrid();
                StatusBar.Text = firstNew != 0
                    ? $"Imported {tilesWide}x{tilesHigh} tiles as sprites {firstNew}-{lastNew} (object not saved yet)."
                    : "The image is fully transparent; slots cleared (object not saved yet).";
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Import image failed: {ex.Message}");
            }
        }

        private static async Task<SKBitmap?> DecodeImageFile(IStorageFile file)
        {
            await using var fs = await file.OpenReadAsync();
            using var ms = new MemoryStream();
            await fs.CopyToAsync(ms);
            // decode from bytes: SKBitmap.Decode(Stream) disposes the stream
            return SKBitmap.Decode(ms.ToArray());
        }

        #endregion

        private void DarkModeToggle_Changed(object? sender, RoutedEventArgs e)
        {
            MainWindow.SetCurrentTheme(DarkModeToggle.IsChecked ?? false);
        }

        private void Exit_Click(object? sender, RoutedEventArgs e) => Close();

        private bool _compiling;

        // Writes the edited dat (and the spr, when sprites changed) back over the client's files.
        // The first compile keeps the untouched originals as "<file>-bak".
        private async void Compile_Click(object? sender, RoutedEventArgs e)
        {
            if (_compiling) return;
            if (MainWindow.appearances == null || loadedVersion == null)
            {
                ErrorManager.ShowWarning("No dat loaded.");
                return;
            }

            string datPath = MainWindow._datPath;
            string sprPath = MainWindow._sprPath;
            PresetSettings preset = MainWindow.GetCurrentPreset() ?? new();
            SpriteStorage storage = MainWindow.MainSprStorage;
            bool sprChanged;
            lock (storage.Modified)
                sprChanged = storage.Modified.Count > 0 || storage.SpriteCount != storage.FileSpriteCount;

            if (!await ErrorManager.ConfirmAsync("Compile",
                    $"Write {Path.GetFileName(datPath)}" + (sprChanged ? $" and {Path.GetFileName(sprPath)}" : "") +
                    $" in\n{Path.GetDirectoryName(datPath)}?\n\nThe running client must be restarted to see the changes."))
                return;

            // nothing may edit the appearances or sprites while they are written from the worker thread
            _compiling = true;
            StopAnimation();
            EditorRoot.IsEnabled = false;
            string previousStatus = StatusBar.Text ?? "";
            StatusBar.Text = "Compiling...";
            try
            {
                string? error = null;
                string diffSummary = "";
                await Task.Run(() =>
                {
                    try
                    {
                        BackupOnce(datPath);
                        if (!LegacyAppearance.WriteLegacyDat(datPath, MainWindow.DatSignature, MainWindow.appearances, loadedVersion.Structure))
                        {
                            error = "dat";
                            return;
                        }

                        if (sprChanged)
                        {
                            BackupOnce(sprPath);
                            storage.Compile(sprPath, MainWindow.SprSignature, storage.Transparency, preset.Extended, null);
                        }

                        // what changed against the untouched original, for the follow-up steps (server otb, minimap, site)
                        if (File.Exists(datPath + "-bak"))
                        {
                            try
                            {
                                var diff = ThingsDiff.Compute(MainWindow.appearances, datPath + "-bak", loadedVersion.Structure, preset);
                                diffSummary = diff.HasChanges
                                    ? $"\n\nChanges against the original ({Path.GetFileName(datPath)}-bak):\n{diff.Summary()}\n\nNewEra Tools > Things diff lists the follow-up steps."
                                    : "";
                            }
                            catch (Exception ex)
                            {
                                MainWindow.Log($"Things diff after compile failed: {ex.Message}", "Warning");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                    }
                });

                if (error == "dat")
                    return; // WriteLegacyDat already reported it
                if (error != null)
                {
                    ErrorManager.ShowError($"Compile failed: {error}");
                    return;
                }

                MainWindow.Log($"Compiled {datPath}" + (sprChanged ? $" and {sprPath}" : ""));
                ErrorManager.ShowInfo("Compiled successfully." + (sprChanged ? "" : " (no sprite changes - spr untouched)") + diffSummary);
                _compiledSnapshot = MainWindow.appearances.ToByteArray();
            }
            finally
            {
                _compiling = false;
                EditorRoot.IsEnabled = true;
                StatusBar.Text = previousStatus;
            }
        }

        private static void BackupOnce(string path)
        {
            string backup = path + "-bak";
            if (File.Exists(path) && !File.Exists(backup))
                File.Copy(path, backup);
        }

        private void OTBEditor_Click(object? sender, RoutedEventArgs e)
        {
            var otb = new OTBEditor(this, true);
            otb.Show();
        }

        private void Import_Click(object? sender, RoutedEventArgs e)
        {
            var importManager = new ImportManager(this);
            importManager.Show();
        }

        private void SearchItem_Click(object? sender, RoutedEventArgs e)
        {
            var search = new SearchWindow(this, true);
            search.Show();
        }

        private void ShowLogger(object? sender, RoutedEventArgs e)
        {
            MainWindow.logView.Show();
        }

        private void OpenLuaWindow_Click(object? sender, RoutedEventArgs e)
        {
            var lua = new LuaWindow();
            lua.Show();
        }

        private void About_Click(object? sender, RoutedEventArgs e)
        {
            var aboutWin = new AboutWindow();
            aboutWin.Show();
        }

        private void ObjectSave_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null) return;
            try
            {
                ApplyCurrentFlagsFromUI();
                if (!ApplyTextureFromUI()) return;

                var collection = GetCurrentCollection();
                if (collection != null)
                {
                    int index = FindAppearanceIndex(collection, ObjListView.SelectedIndex, CurrentObjectAppearance.Id);
                    if (index >= 0)
                        collection[index] = CurrentObjectAppearance.Clone();
                }
                LoadTextureView(false);
                RefreshObjectThumbnail(CurrentObjectAppearance);
                A_FullInfo.Text = CurrentObjectAppearance.ToString();
                ErrorManager.ShowInfo($"Object {CurrentObjectAppearance.Id} saved.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Save failed: {ex.Message}");
            }
        }

        // redraws the list thumbnail of one object of the current category
        private void RefreshObjectThumbnail(Appearance appearance)
        {
            var showList = GetCurrentShowList();
            int index = FindShowListIndex(showList, appearance.Id);
            if (index < 0) return;
            try
            {
                showList[index].Image = Utils.MemoryStreamToBitmap(LegacyAppearance.GetObjectImage(appearance, MainWindow.MainSprStorage));
            }
            catch
            {
                showList[index].Image = null;
            }
        }

        private void ObjectClone_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null) return;
            try
            {
                var collection = GetCurrentCollection();
                var showList = GetCurrentShowList();
                if (collection == null) return;

                uint newId = NextFreeId(collection, ObjectMenu.SelectedIndex);
                var clone = CurrentObjectAppearance.Clone();
                clone.Id = newId;
                collection.Add(clone);
                showList.Add(new ShowList { Id = newId });
                SelectObject(ObjectMenu.SelectedIndex, newId);
                ErrorManager.ShowInfo($"Object cloned as ID {newId}.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Clone failed: {ex.Message}");
            }
        }

        private void CopyFlags_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null) return;
            _copiedFlagsSource = CurrentObjectAppearance.Clone();
            ErrorManager.ShowInfo($"Flags copied from object {CurrentObjectAppearance.Id}.");
        }

        private void PasteFlags_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null || _copiedFlagsSource == null)
            {
                ErrorManager.ShowInfo("No flags copied. Use Copy Flags first.");
                return;
            }
            try
            {
                AppearanceFlags flags = _copiedFlagsSource.Flags.Clone();

                // market trade-as / show-as point at the object itself: keep the target's own values
                // (or its own id when it had no market) instead of the source object's id
                if (flags.Market != null)
                {
                    AppearanceFlagMarket? own = CurrentObjectAppearance.Flags?.Market;
                    if (own != null && own.HasTradeAsObjectId) flags.Market.TradeAsObjectId = own.TradeAsObjectId;
                    else if (own != null) flags.Market.ClearTradeAsObjectId();
                    else flags.Market.TradeAsObjectId = CurrentObjectAppearance.Id;

                    if (own != null && own.HasShowAsObjectId) flags.Market.ShowAsObjectId = own.ShowAsObjectId;
                    else if (own != null) flags.Market.ClearShowAsObjectId();
                    else flags.Market.ShowAsObjectId = CurrentObjectAppearance.Id;
                }

                CurrentObjectAppearance.Flags = flags;
                LoadCurrentObjectAppearances();
                ErrorManager.ShowInfo("Flags pasted.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Paste failed: {ex.Message}");
            }
        }

        private async void ExportObject_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null) return;
            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export Object Image",
                    DefaultExtension = "png",
                    FileTypeChoices = [new FilePickerFileType("PNG Image") { Patterns = ["*.png"] }],
                    SuggestedFileName = $"object_{CurrentObjectAppearance.Id}"
                });

                if (file != null)
                {
                    var stream = LegacyAppearance.GetObjectImage(CurrentObjectAppearance, MainWindow.MainSprStorage);
                    stream.Position = 0;
                    using var fs = await file.OpenWriteAsync();
                    await stream.CopyToAsync(fs);
                    ErrorManager.ShowInfo("Object exported successfully.");
                }
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Export failed: {ex.Message}");
            }
        }

        private void NewObject_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var collection = GetCurrentCollection();
                var showList = GetCurrentShowList();
                if (collection == null) return;

                int category = ObjectMenu.SelectedIndex;
                uint newId = NextFreeId(collection, category);
                var newAppearance = CreateEmptyAppearance(newId, CategoryType(category));

                collection.Add(newAppearance);
                showList.Add(new ShowList { Id = newId });
                SelectObject(category, newId);
                ErrorManager.ShowInfo($"New object created with ID {newId}.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"New object failed: {ex.Message}");
            }
        }

        private static Appearance CreateEmptyAppearance(uint id, APPEARANCE_TYPE type)
        {
            var appearance = new Appearance
            {
                Id = id,
                AppearanceType = type,
                Flags = new AppearanceFlags()
            };

            var frameGroup = new FrameGroup
            {
                FixedFrameGroup = FIXED_FRAME_GROUP.OutfitIdle,
                SpriteInfo = new SpriteInfo
                {
                    PatternWidth = 1,
                    PatternHeight = 1,
                    PatternSize = 32,
                    PatternLayers = 1,
                    PatternX = 1,
                    PatternY = 1,
                    PatternZ = 1,
                    PatternFrames = 1
                }
            };
            frameGroup.SpriteInfo.SpriteId.Add(0);
            appearance.FrameGroup.Add(frameGroup);
            return appearance;
        }

        private void DeleteObject_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentObjectAppearance == null) return;
            try
            {
                var collection = GetCurrentCollection();
                var showList = GetCurrentShowList();
                if (collection == null) return;

                int selectedIndex = ObjListView.SelectedIndex;
                int collectionIndex = FindAppearanceIndex(collection, selectedIndex, CurrentObjectAppearance.Id);
                if (collectionIndex < 0) return;
                Appearance toRemove = collection[collectionIndex];

                // the dat has no ids - an object's id is its position. Removing one from the middle would
                // renumber every later object and break items.otb, the map and all server scripts, so only
                // the last object is really removed; any other one is cleared to an empty object.
                if (toRemove.Id == collection.Max(a => a.Id))
                {
                    collection.RemoveAt(collectionIndex);
                    int showIndex = FindShowListIndex(showList, toRemove.Id);
                    if (showIndex >= 0)
                        showList.RemoveAt(showIndex);
                    if (ObjListView.ItemCount > 0)
                        ObjListView.SelectedIndex = Math.Min(selectedIndex, ObjListView.ItemCount - 1);
                    ErrorManager.ShowInfo($"Object {toRemove.Id} deleted.");
                }
                else
                {
                    collection[collectionIndex] = CreateEmptyAppearance(toRemove.Id, CategoryType(ObjectMenu.SelectedIndex));
                    CurrentObjectAppearance = collection[collectionIndex].Clone();
                    LoadCurrentObjectAppearances();
                    RefreshObjectThumbnail(CurrentObjectAppearance);
                    ErrorManager.ShowInfo($"Object {toRemove.Id} cleared (ids are positional, so only the last object can be removed).");
                }
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Delete failed: {ex.Message}");
            }
        }

        private async void SpriteExport_Click(object? sender, RoutedEventArgs e)
        {
            if (SprListView.SelectedItem is not ShowList selected) return;
            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export Sprite",
                    DefaultExtension = "png",
                    FileTypeChoices = [new FilePickerFileType("PNG Image") { Patterns = ["*.png"] }],
                    SuggestedFileName = $"sprite_{selected.Id}"
                });

                if (file != null)
                {
                    var stream = MainWindow.MainSprStorage.getSpriteStream(selected.Id);
                    stream.Position = 0;
                    using var fs = await file.OpenWriteAsync();
                    await stream.CopyToAsync(fs);
                    ErrorManager.ShowInfo($"Sprite {selected.Id} exported.");
                }
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Export failed: {ex.Message}");
            }
        }

        // Adds images as new sprites at the end of the spr. A 32x32 image is one sprite; larger images
        // (sides multiple of 32) are split into tiles, left to right and top to bottom, skipping fully
        // transparent tiles.
        private async void ImportSprite_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Import Sprites",
                    AllowMultiple = true,
                    FileTypeFilter = [new FilePickerFileType("Image files") { Patterns = ["*.png", "*.bmp", "*.jpg", "*.jpeg", "*.gif"] }]
                });
                if (files.Count == 0) return;

                bool transparent = MainWindow.MainSprStorage.Transparency;
                uint firstNew = 0, lastNew = 0;
                var skipped = new List<string>();
                foreach (var file in files)
                {
                    using SKBitmap? image = await DecodeImageFile(file);
                    if (image == null) { skipped.Add($"{file.Name} (not an image)"); continue; }

                    if (image.Width <= Sprite.DefaultSize && image.Height <= Sprite.DefaultSize)
                    {
                        using SKBitmap tile = PadToSprite(image);
                        var single = LegacyAppearance.SplitIntoTiles(tile, transparent, out _, out _);
                        lastNew = AddSprite(single[0]);
                        if (firstNew == 0) firstNew = lastNew;
                        continue;
                    }

                    if (image.Width % Sprite.DefaultSize != 0 || image.Height % Sprite.DefaultSize != 0)
                    {
                        skipped.Add($"{file.Name} ({image.Width}x{image.Height} is not a multiple of {Sprite.DefaultSize})");
                        continue;
                    }

                    var tiles = LegacyAppearance.SplitIntoTiles(image, transparent, out int tilesWide, out int tilesHigh);
                    for (int row = 0; row < tilesHigh; row++)
                    {
                        for (int col = 0; col < tilesWide; col++)
                        {
                            byte[]? pixels = tiles[(tilesHigh - 1 - row) * tilesWide + (tilesWide - 1 - col)];
                            if (pixels == null) continue;
                            lastNew = AddSprite(pixels);
                            if (firstNew == 0) firstNew = lastNew;
                        }
                    }
                }

                if (firstNew != 0)
                    RefreshSpriteList(lastNew);
                string message = firstNew != 0 ? $"Added sprites {firstNew}-{lastNew}." : "No sprites added.";
                if (skipped.Count > 0)
                    message += "\n\nSkipped:\n" + string.Join("\n", skipped);
                ErrorManager.ShowInfo(message);
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Import failed: {ex.Message}");
            }
        }

        private async void ReplaceSprite_Click(object? sender, RoutedEventArgs e)
        {
            if (SprListView.SelectedItem is not ShowList selected) return;
            await ReplaceSelectedSprite(selected.Id);
        }

        // a smaller image is placed top-left on a transparent 32x32 sprite
        private static SKBitmap PadToSprite(SKBitmap image)
        {
            var tile = new SKBitmap(Sprite.DefaultSize, Sprite.DefaultSize, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(tile);
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(image, 0, 0);
            return tile;
        }

        private async Task ReplaceSelectedSprite(uint spriteId)
        {
            if (spriteId == 0)
            {
                ErrorManager.ShowWarning("Sprite 0 is the reserved empty sprite and cannot be replaced.");
                return;
            }
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select Sprite Image",
                    AllowMultiple = false,
                    FileTypeFilter = [
                        new FilePickerFileType("Image files") { Patterns = ["*.png", "*.bmp", "*.jpg", "*.jpeg", "*.gif"] }
                    ]
                });

                if (files.Count == 0) return;

                using SKBitmap? image = await DecodeImageFile(files[0]);
                if (image == null) { ErrorManager.ShowError("Failed to load image."); return; }

                // never squash a bigger picture into one sprite
                if (image.Width > Sprite.DefaultSize || image.Height > Sprite.DefaultSize)
                {
                    ErrorManager.ShowWarning($"The image is {image.Width}x{image.Height}; a sprite is {Sprite.DefaultSize}x{Sprite.DefaultSize}.\n\n" +
                                             "Use \"Import image...\" on the Texture tab to split a larger image into sprites of an object, " +
                                             "or \"Import Sprite\" to add its tiles as new sprites.");
                    return;
                }

                using SKBitmap tile = PadToSprite(image);
                var pixels = LegacyAppearance.SplitIntoTiles(tile, MainWindow.MainSprStorage.Transparency, out _, out _)[0];
                MainWindow.MainSprStorage.SetSprite(spriteId, pixels);

                if ((int)spriteId < MainWindow.AllSprList.Count)
                    MainWindow.AllSprList[(int)spriteId].Image = Utils.MemoryStreamToBitmap(MainWindow.MainSprStorage.getSpriteStream(spriteId));
                RefreshAfterSpriteChange();

                ErrorManager.ShowInfo($"Sprite {spriteId} replaced.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Replace failed: {ex.Message}");
            }
        }

        private void AddSprite_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                uint newId = AddSprite(null);
                RefreshSpriteList(newId);
                ErrorManager.ShowInfo($"Sprite {newId} added. Use Replace to set its image.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Add sprite failed: {ex.Message}");
            }
        }

        private void DeleteSprite_Click(object? sender, RoutedEventArgs e)
        {
            if (SprListView.SelectedItem is not ShowList selected) return;
            if (selected.Id == 0) return;
            try
            {
                // sprite ids are positional too: the sprite is emptied, not removed
                uint spriteId = selected.Id;
                MainWindow.MainSprStorage.SetSprite(spriteId, null);

                if ((int)spriteId < MainWindow.AllSprList.Count)
                    MainWindow.AllSprList[(int)spriteId].Image = Utils.MemoryStreamToBitmap(MainWindow.MainSprStorage.getSpriteStream(spriteId));
                RefreshAfterSpriteChange();

                ErrorManager.ShowInfo($"Sprite {spriteId} deleted.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Delete failed: {ex.Message}");
            }
        }

        // pixels of a sprite changed: redraw everything that may show it
        private void RefreshAfterSpriteChange()
        {
            UpdatePreview();
            BuildSlotGrid();
            if (ObjListView.ItemsSource is IList<ShowList> list)
            {
                for (int i = Math.Max(0, _lastObjStart); i < _lastObjEnd && i < list.Count; i++)
                    list[i].Image = null;
            }
            LoadVisibleObjectImages();
        }

        private void ObjListViewSelectedIndex_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
        {
            if (!isPageLoaded) return;
            int idx = (int)(ObjListViewSelectedIndex.Value ?? 0);
            if (idx >= 0 && idx < ObjListView.ItemCount)
            {
                ObjListView.SelectedIndex = idx;
                ObjListView.ScrollIntoView(idx);
            }
        }

        private void SprListViewSelectedIndex_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
        {
            if (!isPageLoaded) return;
            int idx = (int)(SprListViewSelectedIndex.Value ?? 0);
            if (idx >= 0 && idx < SprListView.ItemCount)
            {
                SprListView.SelectedIndex = idx;
                SprListView.ScrollIntoView(idx);
            }
        }

        #region Scroll-based image loading

        // row height of both lists: 64px image + ~12px ListBoxItem padding. Must match the rendered
        // rows or images stop loading while scrolling.
        private const double ItemHeight = 76;

        private bool _objScrollHooked;
        private bool _sprScrollHooked;
        private int _lastObjStart = -1;
        private int _lastObjEnd = -1;
        private int _lastSprStart = -1;
        private int _lastSprEnd = -1;

        private void ObjListView_LayoutUpdated(object? sender, EventArgs e)
        {
            if (_objScrollHooked) return;
            var sv = FindScrollViewer(ObjListView);
            if (sv != null)
            {
                sv.ScrollChanged += ObjListView_ScrollChanged;
                _objScrollHooked = true;
            }
        }

        private void SprListView_LayoutUpdated(object? sender, EventArgs e)
        {
            if (_sprScrollHooked) return;
            var sv = FindScrollViewer(SprListView);
            if (sv != null)
            {
                sv.ScrollChanged += SprListView_ScrollChanged;
                _sprScrollHooked = true;
            }
        }

        private static ScrollViewer? FindScrollViewer(Control control)
        {
            if (control is ScrollViewer sv) return sv;
            if (control is not Visual visual) return null;
            foreach (var child in visual.GetVisualChildren())
            {
                if (child is ScrollViewer found) return found;
                if (child is Control c)
                {
                    var nested = FindScrollViewer(c);
                    if (nested != null) return nested;
                }
            }
            return null;
        }

        private void ObjListView_ScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (!isPageLoaded || ObjListView.ItemCount == 0) return;
            LoadVisibleObjectImages();
        }

        private void SprListView_ScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (!isPageLoaded || SprListView.ItemCount == 0) return;
            LoadVisibleSpriteImages();
        }

        private void LoadVisibleObjectImages()
        {
            var sv = FindScrollViewer(ObjListView);
            if (sv == null) return;
            if (ObjListView.ItemsSource is not IList<ShowList> items || items.Count == 0) return;
            var collection = GetCurrentCollection();
            if (collection == null) return;

            int itemCount = items.Count;
            int offset = Math.Max(0, (int)(sv.Offset.Y / ItemHeight));
            int visibleCount = (int)(sv.Viewport.Height / ItemHeight) + 2;
            int start = Math.Min(offset, itemCount);
            int end = Math.Min(offset + visibleCount, itemCount);

            // Unload items that scrolled out of view (the list may have shrunk since)
            if (_lastObjStart >= 0)
            {
                for (int i = _lastObjStart; i < _lastObjEnd && i < itemCount; i++)
                {
                    if (i < start || i >= end)
                        items[i].Image = null;
                }
            }

            // Load newly visible items
            for (int i = start; i < end; i++)
            {
                ShowList visible = items[i];
                if (visible.Image != null) continue;
                try
                {
                    Appearance? appearance = FindAppearance(collection, i, visible.Id);
                    if (appearance != null)
                    {
                        MemoryStream stream = LegacyAppearance.GetObjectImage(appearance, MainWindow.MainSprStorage);
                        visible.Image = Utils.MemoryStreamToBitmap(stream);
                    }
                }
                catch { }
            }

            _lastObjStart = start;
            _lastObjEnd = end;
        }

        private void LoadVisibleSpriteImages()
        {
            var sv = FindScrollViewer(SprListView);
            if (sv == null) return;

            var sprites = MainWindow.AllSprList;
            int itemCount = Math.Min(SprListView.ItemCount, sprites.Count);
            if (itemCount == 0) return;

            int offset = Math.Max(0, (int)(sv.Offset.Y / ItemHeight));
            int visibleCount = (int)(sv.Viewport.Height / ItemHeight) + 2;
            int start = Math.Min(offset, itemCount);
            int end = Math.Min(offset + visibleCount, itemCount);

            // Unload items that scrolled out of view
            if (_lastSprStart >= 0)
            {
                for (int i = _lastSprStart; i < _lastSprEnd && i < itemCount; i++)
                {
                    if (i < start || i >= end)
                        sprites[i].Image = null;
                }
            }

            // Load newly visible items
            for (int i = start; i < end; i++)
            {
                if (sprites[i].Image != null) continue;
                try
                {
                    sprites[i].Image = Utils.MemoryStreamToBitmap(
                        MainWindow.MainSprStorage.getSpriteStream(sprites[i].Id));
                }
                catch { }
            }

            _lastSprStart = start;
            _lastSprEnd = end;
        }

        #endregion
    }
}
