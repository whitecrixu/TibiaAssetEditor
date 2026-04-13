using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        private int CurrentSprDir = 2;
        private bool isPageLoaded = false;
        private VersionInfo? loadedVersion;
        private static Appearance? _copiedFlagsSource = null;

        protected override void OnClosed(EventArgs e)
        {
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
            UpdateShowList(ObjectMenu.SelectedIndex);
            loadedVersion = versionInfo;
            isPageLoaded = true;

            Dispatcher.UIThread.Post(() =>
            {
                LoadVisibleObjectImages();
                LoadVisibleSpriteImages();
            }, DispatcherPriority.Loaded);
        }

        public void UpdateShowList(int selection)
        {
            if (ObjListView == null) return;

            if (selection == 0)
                ObjListView.ItemsSource = ThingsOutfit;
            else if (selection == 1)
                ObjListView.ItemsSource = ThingsItem;
            else if (selection == 2)
                ObjListView.ItemsSource = ThingsEffect;
            else if (selection == 3)
                ObjListView.ItemsSource = ThingsMissile;

            if (ObjListView.ItemCount > 0)
                ObjListView.SelectedIndex = 0;
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

            Appearance? appearance = ObjectMenu.SelectedIndex switch
            {
                0 => MainWindow.appearances?.Outfit.ElementAtOrDefault(ObjListView.SelectedIndex),
                1 => MainWindow.appearances?.Object.ElementAtOrDefault(ObjListView.SelectedIndex),
                2 => MainWindow.appearances?.Effect.ElementAtOrDefault(ObjListView.SelectedIndex),
                3 => MainWindow.appearances?.Missile.ElementAtOrDefault(ObjListView.SelectedIndex),
                _ => null
            };

            if (appearance == null) return;
            CurrentObjectAppearance = appearance.Clone();
            LoadCurrentObjectAppearances();
        }

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

        private ObservableCollection<ShowList> GetCurrentShowList()
        {
            return ObjectMenu.SelectedIndex switch
            {
                0 => ThingsOutfit,
                1 => ThingsItem,
                2 => ThingsEffect,
                3 => ThingsMissile,
                _ => ThingsOutfit
            };
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
            A_FlagLenshelpId.SelectedIndex = flags.Lenshelp != null ? Math.Max(0, (int)flags.Lenshelp.Id - 1) : -1;
            A_FlagFullGround.IsChecked = flags.HasFullbank;
            A_FlagIgnoreLook.IsChecked = flags.HasIgnoreLook;
            A_FlagClothes.IsChecked = flags.Clothes != null;
            A_FlagClothesSlot.SelectedIndex = flags.Clothes != null ? (int)flags.Clothes.Slot : -1;
            A_FlagDefaultAction.IsChecked = flags.DefaultAction != null;
            A_FlagDefaultActionType.SelectedIndex = flags.DefaultAction != null ? (int)flags.DefaultAction.Action : -1;
            A_FlagMarket.IsChecked = flags.Market != null;
            A_FlagMarketCategory.SelectedIndex = flags.Market != null ? Math.Max(0, (int)flags.Market.Category - 1) : -1;
            A_FlagWrap.IsChecked = flags.HasWrap;
            A_FlagUnwrap.IsChecked = flags.HasUnwrap;
            A_FlagTopeffect.IsChecked = flags.HasTopeffect;
            A_FlagWearout.IsChecked = flags.HasWearout;
            A_FlagName.Text = CurrentObjectAppearance.Name ?? "";

            // Load sprite info
            if (CurrentObjectAppearance.FrameGroup.Count > 0)
            {
                var spriteInfo = CurrentObjectAppearance.FrameGroup[0].SpriteInfo;
                A_SprWidth.Value = spriteInfo.PatternWidth;
                A_SprHeight.Value = spriteInfo.PatternHeight;
                A_SprLayers.Value = spriteInfo.PatternLayers;
                A_SprPaternX.Value = spriteInfo.PatternX;
                A_SprPaternY.Value = spriteInfo.PatternY;
                A_SprPaternZ.Value = spriteInfo.PatternZ;
                A_SprAnimation.Value = spriteInfo.PatternFrames;

                UpdatePreview();
            }

            A_FullInfo.Text = CurrentObjectAppearance.ToString();
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
            flags.Lenshelp = A_FlagLenshelp.IsChecked == true && A_FlagLenshelpId.SelectedIndex >= 0
                ? new AppearanceFlagLenshelp { Id = (uint)(A_FlagLenshelpId.SelectedIndex + 1) }
                : null;

            if (A_FlagFullGround.IsChecked == true) flags.Fullbank = true; else flags.ClearFullbank();
            if (A_FlagIgnoreLook.IsChecked == true) flags.IgnoreLook = true; else flags.ClearIgnoreLook();

            flags.Clothes = A_FlagClothes.IsChecked == true && A_FlagClothesSlot.SelectedIndex >= 0
                ? new AppearanceFlagClothes { Slot = (uint)A_FlagClothesSlot.SelectedIndex }
                : null;
            flags.DefaultAction = A_FlagDefaultAction.IsChecked == true && A_FlagDefaultActionType.SelectedIndex >= 0
                ? new AppearanceFlagDefaultAction { Action = (PLAYER_ACTION)A_FlagDefaultActionType.SelectedIndex }
                : null;
            flags.Market = A_FlagMarket.IsChecked == true && A_FlagMarketCategory.SelectedIndex >= 0
                ? new AppearanceFlagMarket { Category = (ITEM_CATEGORY)(A_FlagMarketCategory.SelectedIndex + 1) }
                : null;

            if (A_FlagWrap.IsChecked == true) flags.Wrap = true; else flags.ClearWrap();
            if (A_FlagUnwrap.IsChecked == true) flags.Unwrap = true; else flags.ClearUnwrap();
            if (A_FlagTopeffect.IsChecked == true) flags.Topeffect = true; else flags.ClearTopeffect();
            if (A_FlagWearout.IsChecked == true) flags.Wearout = true; else flags.ClearWearout();

            CurrentObjectAppearance.Name = A_FlagName.Text ?? "";
        }

        private void UpdatePreview()
        {
            if (CurrentObjectAppearance == null || CurrentObjectAppearance.FrameGroup.Count == 0) return;

            try
            {
                var frameGroup = CurrentObjectAppearance.FrameGroup[0];
                int dir = Math.Min(CurrentSprDir, (int)frameGroup.SpriteInfo.PatternX - 1);
                int spriteIndex = DatEditor.GetSpriteIndex(frameGroup, 0, dir, 0, 0, 0);

                if (spriteIndex < frameGroup.SpriteInfo.SpriteId.Count)
                {
                    MemoryStream? stream = LegacyAppearance.GetObjectImage(CurrentObjectAppearance, MainWindow.MainSprStorage);
                    if (stream != null)
                        SpriteViewerImge.Source = Utils.MemoryStreamToBitmap(stream);
                }
            }
            catch { }
        }

        private void ChangeDirection(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string dir)
            {
                CurrentSprDir = int.Parse(dir);
                UpdatePreview();
            }
        }

        private void DarkModeToggle_Changed(object? sender, RoutedEventArgs e)
        {
            MainWindow.SetCurrentTheme(DarkModeToggle.IsChecked ?? false);
        }

        private void Exit_Click(object? sender, RoutedEventArgs e) => Close();

        private void Compile_Click(object? sender, RoutedEventArgs e)
        {
            ErrorManager.ShowInfo("Compile functionality - coming in future update");
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
                var collection = GetCurrentCollection();
                if (collection != null)
                {
                    for (int i = 0; i < collection.Count; i++)
                    {
                        if (collection[i].Id == CurrentObjectAppearance.Id)
                        {
                            collection[i] = CurrentObjectAppearance.Clone();
                            break;
                        }
                    }
                }
                A_FullInfo.Text = CurrentObjectAppearance.ToString();
                ErrorManager.ShowInfo($"Object {CurrentObjectAppearance.Id} saved.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Save failed: {ex.Message}");
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

                uint newId = collection.Count > 0 ? collection.Max(a => a.Id) + 1 : 1;
                var clone = CurrentObjectAppearance.Clone();
                clone.Id = newId;
                collection.Add(clone);
                showList.Add(new ShowList { Id = newId });
                ObjListView.SelectedIndex = ObjListView.ItemCount - 1;
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
                CurrentObjectAppearance.Flags.MergeFrom(_copiedFlagsSource.Flags);
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

                uint newId = collection.Count > 0 ? collection.Max(a => a.Id) + 1 : 100;
                var newAppearance = new Appearance
                {
                    Id = newId,
                    AppearanceType = ObjectMenu.SelectedIndex switch
                    {
                        0 => APPEARANCE_TYPE.AppearanceOutfit,
                        1 => APPEARANCE_TYPE.AppearanceObject,
                        2 => APPEARANCE_TYPE.AppearanceEffect,
                        3 => APPEARANCE_TYPE.AppearanceMissile,
                        _ => APPEARANCE_TYPE.AppearanceObject
                    }
                };

                var frameGroup = new FrameGroup
                {
                    SpriteInfo = new SpriteInfo
                    {
                        PatternWidth = 1,
                        PatternHeight = 1,
                        PatternLayers = 1,
                        PatternX = 1,
                        PatternY = 1,
                        PatternZ = 1,
                        PatternFrames = 1
                    }
                };
                frameGroup.SpriteInfo.SpriteId.Add(0);
                newAppearance.FrameGroup.Add(frameGroup);

                collection.Add(newAppearance);
                showList.Add(new ShowList { Id = newId });
                ObjListView.SelectedIndex = ObjListView.ItemCount - 1;
                ErrorManager.ShowInfo($"New object created with ID {newId}.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"New object failed: {ex.Message}");
            }
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
                var toRemove = collection.FirstOrDefault(a => a.Id == CurrentObjectAppearance.Id);
                if (toRemove != null)
                    collection.Remove(toRemove);

                if (selectedIndex < showList.Count)
                    showList.RemoveAt(Math.Max(0, selectedIndex));

                if (ObjListView.ItemCount > 0)
                    ObjListView.SelectedIndex = Math.Min(selectedIndex, ObjListView.ItemCount - 1);

                ErrorManager.ShowInfo("Object deleted.");
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

        private async void ImportSprite_Click(object? sender, RoutedEventArgs e)
        {
            if (SprListView.SelectedItem is not ShowList selected) return;
            await ReplaceSelectedSprite(selected.Id);
        }

        private async void ReplaceSprite_Click(object? sender, RoutedEventArgs e)
        {
            if (SprListView.SelectedItem is not ShowList selected) return;
            await ReplaceSelectedSprite(selected.Id);
        }

        private async Task ReplaceSelectedSprite(uint spriteId)
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select Sprite Image",
                    AllowMultiple = false,
                    FileTypeFilter = [
                        new FilePickerFileType("Image files") { Patterns = ["*.png", "*.bmp", "*.jpg", "*.jpeg"] }
                    ]
                });

                if (files.Count == 0) return;

                using var fs = await files[0].OpenReadAsync();
                using var ms = new MemoryStream();
                await fs.CopyToAsync(ms);
                ms.Position = 0;

                using var skBmp = SKBitmap.Decode(ms);
                if (skBmp == null) { ErrorManager.ShowError("Failed to load image."); return; }

                using var resized = skBmp.Resize(
                    new SKImageInfo(Sprite.DefaultSize, Sprite.DefaultSize, SKColorType.Bgra8888, SKAlphaType.Unpremul),
                    SKFilterQuality.High);
                if (resized == null) { ErrorManager.ShowError("Failed to resize image."); return; }

                var compressed = Sprite.CompressBitmap(resized, MainWindow.MainSprStorage.Transparency);
                Sprite newSprite = new Sprite
                {
                    ID = spriteId,
                    Transparent = MainWindow.MainSprStorage.Transparency,
                    CompressedPixels = compressed,
                    Size = (uint)compressed.Length
                };

                using var bmp = newSprite.GetBitmap();
                using var img = SKImage.FromBitmap(bmp);
                using var dat = img.Encode(SKEncodedImageFormat.Png, 100);
                newSprite.MemoryStream = new MemoryStream(dat.ToArray());

                MainWindow.MainSprStorage.Sprites[spriteId] = newSprite;
                MainWindow.MainSprStorage.SprLists[(int)spriteId] = newSprite.MemoryStream;

                if ((int)spriteId < MainWindow.AllSprList.Count)
                    MainWindow.AllSprList[(int)spriteId].Image = Utils.MemoryStreamToBitmap(newSprite.MemoryStream);

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
                // Find next free ID
                uint newId = (uint)(MainWindow.AllSprList.Count > 0
                    ? MainWindow.AllSprList.Max(s => s.Id) + 1
                    : 1);

                // Create blank sprite
                Sprite blankSprite = new Sprite { ID = newId, CompressedPixels = [] };
                using var bmp = blankSprite.GetBitmap();
                using var img = SKImage.FromBitmap(bmp);
                using var dat = img.Encode(SKEncodedImageFormat.Png, 100);
                blankSprite.MemoryStream = new MemoryStream(dat.ToArray());

                MainWindow.MainSprStorage.Sprites[newId] = blankSprite;
                MainWindow.MainSprStorage.SprLists[(int)newId] = blankSprite.MemoryStream;

                var entry = new ShowList { Id = newId };
                MainWindow.AllSprList.Add(entry);

                // Scroll to the new sprite
                SprListView.ScrollIntoView(entry);
                SprListView.SelectedItem = entry;

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
            try
            {
                uint spriteId = selected.Id;
                Sprite blankSprite = new Sprite { ID = spriteId, CompressedPixels = [] };
                using var bmp = blankSprite.GetBitmap();
                using var img = SKImage.FromBitmap(bmp);
                using var dat = img.Encode(SKEncodedImageFormat.Png, 100);
                blankSprite.MemoryStream = new MemoryStream(dat.ToArray());

                MainWindow.MainSprStorage.Sprites[spriteId] = blankSprite;
                MainWindow.MainSprStorage.SprLists[(int)spriteId] = blankSprite.MemoryStream;

                if ((int)spriteId < MainWindow.AllSprList.Count)
                    MainWindow.AllSprList[(int)spriteId].Image = null;

                ErrorManager.ShowInfo($"Sprite {spriteId} deleted.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Delete failed: {ex.Message}");
            }
        }

        private void ObjListViewSelectedIndex_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
        {
            if (!isPageLoaded) return;
            int idx = (int)(ObjListViewSelectedIndex.Value ?? 0);
            if (idx >= 0 && idx < ObjListView.ItemCount)
            {
                ObjListView.SelectedIndex = idx;
                ObjListView.ScrollIntoView(ObjListView.Items[idx]!);
            }
        }

        private void SprListViewSelectedIndex_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
        {
            if (!isPageLoaded) return;
            int idx = (int)(SprListViewSelectedIndex.Value ?? 0);
            if (idx >= 0 && idx < SprListView.ItemCount)
            {
                SprListView.SelectedIndex = idx;
                SprListView.ScrollIntoView(SprListView.Items[idx]!);
            }
        }

        #region Scroll-based image loading

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

            int itemCount = ObjListView.ItemCount;
            if (itemCount == 0) return;

            // Items have 64px images + ~12px Avalonia ListBoxItem padding = ~76px total
            double itemHeight = 76;
            int offset = Math.Max(0, (int)(sv.Offset.Y / itemHeight));
            int visibleCount = (int)(sv.Viewport.Height / itemHeight) + 2;
            int start = offset;
            int end = Math.Min(offset + visibleCount, itemCount);

            // Unload items that scrolled out of view
            if (_lastObjStart >= 0)
            {
                for (int i = _lastObjStart; i < _lastObjEnd; i++)
                {
                    if (i < start || i >= end)
                    {
                        if (ObjListView.Items[i] is ShowList item)
                            item.Image = null;
                    }
                }
            }

            // Load newly visible items
            for (int i = start; i < end; i++)
            {
                if (ObjListView.Items[i] is not ShowList visible) continue;
                if (visible.Image != null) continue;
                try
                {
                    Appearance? appearance = ObjectMenu.SelectedIndex switch
                    {
                        0 => MainWindow.appearances?.Outfit.FirstOrDefault(o => o.Id == visible.Id),
                        1 => MainWindow.appearances?.Object.FirstOrDefault(o => o.Id == visible.Id),
                        2 => MainWindow.appearances?.Effect.FirstOrDefault(o => o.Id == visible.Id),
                        3 => MainWindow.appearances?.Missile.FirstOrDefault(o => o.Id == visible.Id),
                        _ => null
                    };
                    if (appearance != null)
                    {
                        MemoryStream? stream = LegacyAppearance.GetObjectImage(appearance, MainWindow.MainSprStorage);
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

            int itemCount = SprListView.ItemCount;
            if (itemCount == 0) return;

            // Items have 64px images + ~12px Avalonia ListBoxItem padding = ~76px total
            double itemHeight = 76;
            int offset = Math.Max(0, (int)(sv.Offset.Y / itemHeight));
            int visibleCount = (int)(sv.Viewport.Height / itemHeight) + 2;
            int start = offset;
            int end = Math.Min(offset + visibleCount, itemCount);

            // Unload items that scrolled out of view
            if (_lastSprStart >= 0)
            {
                for (int i = _lastSprStart; i < _lastSprEnd; i++)
                {
                    if (i < start || i >= end)
                        MainWindow.AllSprList[i].Image = null;
                }
            }

            // Load newly visible items
            for (int i = start; i < end; i++)
            {
                if (MainWindow.AllSprList[i].Image != null) continue;
                try
                {
                    MainWindow.AllSprList[i].Image = Utils.MemoryStreamToBitmap(
                        MainWindow.MainSprStorage.getSpriteStream((uint)i));
                }
                catch { }
            }

            _lastSprStart = start;
            _lastSprEnd = end;
        }

        #endregion
    }
}
