using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Tibia.Protobuf.Appearances;

namespace Assets_Editor
{
    public partial class SearchWindow : Window
    {
        private dynamic _editor;
        // category (0 outfits, 1 items, 2 effects, 3 missiles) the shown results belong to
        private int _resultCategory = 1;

        public SearchWindow(dynamic editor, bool legacy)
        {
            InitializeComponent();
            _editor = editor;
        }

        private void ItemListView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (ItemListView.SelectedItem is not ShowList showList) return;

            if (_editor is LegacyDatEditor legacy)
            {
                legacy.SelectObject(_resultCategory, showList.Id);
                return;
            }

            if ((int)_editor.ObjectMenu.SelectedIndex != _resultCategory)
                _editor.ObjectMenu.SelectedIndex = _resultCategory;
            int index = 0;
            foreach (object? entry in (IEnumerable)_editor.ObjListView.Items)
            {
                if (entry is ShowList item && item.Id == showList.Id)
                {
                    _editor.ObjListView.SelectedItem = item;
                    _editor.ObjListView.ScrollIntoView(index);
                    break;
                }
                index++;
            }
        }

        private void SearchItem_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null) return;

            // search the category the editor shows (other entries of its menu, e.g. monsters, fall back to items)
            int category = 1;
            try { category = (int)_editor.ObjectMenu.SelectedIndex; } catch { }
            if (category < 0 || category > 3) category = 1;

            IEnumerable<Appearance> source = category switch
            {
                0 => MainWindow.appearances.Outfit,
                2 => MainWindow.appearances.Effect,
                3 => MainWindow.appearances.Missile,
                _ => MainWindow.appearances.Object,
            };

            var results = new List<ShowList>();

            foreach (Appearance item in source)
            {
                var flags = item.Flags ?? new AppearanceFlags();
                if (A_FlagGround.IsChecked == true && flags.Bank == null) continue;
                if (A_FlagClip.IsChecked == true && flags.Clip != true) continue;
                if (A_FlagBottom.IsChecked == true && flags.Bottom != true) continue;
                if (A_FlagTop.IsChecked == true && flags.Top != true) continue;
                if (A_FlagContainer.IsChecked == true && flags.Container != true) continue;
                if (A_FlagCumulative.IsChecked == true && flags.Cumulative != true) continue;
                if (A_FlagUsable.IsChecked == true && flags.Usable != true) continue;
                if (A_FlagForceuse.IsChecked == true && flags.Forceuse != true) continue;
                if (A_FlagMultiuse.IsChecked == true && flags.Multiuse != true) continue;
                if (A_FlagWrite.IsChecked == true && flags.Write == null) continue;
                if (A_FlagWriteOnce.IsChecked == true && flags.WriteOnce == null) continue;
                if (A_FlagLiquidpool.IsChecked == true && flags.HasLiquidpool != true) continue;
                if (A_FlagUnpass.IsChecked == true && flags.HasUnpass != true) continue;
                if (A_FlagUnmove.IsChecked == true && flags.HasUnmove != true) continue;
                if (A_FlagUnsight.IsChecked == true && flags.HasUnsight != true) continue;
                if (A_FlagAvoid.IsChecked == true && flags.HasAvoid != true) continue;
                if (A_FlagNoMoveAnimation.IsChecked == true && flags.HasNoMovementAnimation != true) continue;
                if (A_FlagTake.IsChecked == true && flags.HasTake != true) continue;
                if (A_FlagLiquidcontainer.IsChecked == true && flags.HasLiquidcontainer != true) continue;
                if (A_FlagHang.IsChecked == true && flags.HasHang != true) continue;
                if (A_FlagHookSouth.IsChecked == true && flags.HookSouth != true) continue;
                if (A_FlagHookEast.IsChecked == true && flags.HookEast != true) continue;
                if (A_FlagRotate.IsChecked == true && flags.HasRotate != true) continue;
                if (A_FlagLight.IsChecked == true && flags.Light == null) continue;
                if (A_FlagDontHide.IsChecked == true && flags.HasDontHide != true) continue;
                if (A_FlagTranslucent.IsChecked == true && flags.HasTranslucent != true) continue;
                if (A_FlagShift.IsChecked == true && flags.Shift == null) continue;
                if (A_FlagHeight.IsChecked == true && flags.Height == null) continue;
                if (A_FlagLyingObject.IsChecked == true && flags.HasLyingObject != true) continue;
                if (A_FlagAutomap.IsChecked == true && flags.Automap == null) continue;
                if (A_FlagLenshelp.IsChecked == true && flags.Lenshelp == null) continue;
                if (A_FlagFullGround.IsChecked == true && flags.HasFullbank != true) continue;
                if (A_FlagIgnoreLook.IsChecked == true && flags.HasIgnoreLook != true) continue;
                if (A_FlagClothes.IsChecked == true && flags.Clothes == null) continue;
                if (A_FlagDefaultAction.IsChecked == true && flags.DefaultAction == null) continue;
                if (A_FlagMarket.IsChecked == true && flags.Market == null) continue;
                if (A_FlagWrap.IsChecked == true && flags.HasWrap != true) continue;
                if (A_FlagUnwrap.IsChecked == true && flags.HasUnwrap != true) continue;
                if (A_FlagTopeffect.IsChecked == true && flags.HasTopeffect != true) continue;
                if (A_FlagWearout.IsChecked == true && flags.HasWearout != true) continue;
                if (A_FlagAnimated.IsChecked == true && !item.FrameGroup.Any(g => g.SpriteInfo != null && g.SpriteInfo.PatternFrames > 1)) continue;
                if (A_FlagWingsOffset.IsChecked == true && flags.WingsOffset == null) continue;

                results.Add(new ShowList() { Id = item.Id });
            }

            _resultCategory = category;
            ItemListView.ItemsSource = results;
            Title = $"Search Window - {results.Count} result(s)";
        }
    }
}
