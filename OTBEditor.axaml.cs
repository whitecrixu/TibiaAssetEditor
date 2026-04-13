using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Tibia.Protobuf.Appearances;
using static Assets_Editor.OTB;

namespace Assets_Editor
{
    public partial class OTBEditor : Window
    {
        private dynamic _editor;
        private bool _legacy = false;

        public OTBEditor(dynamic editor, bool legacy)
        {
            InitializeComponent();
            _editor = editor;
            _legacy = legacy;
            ItemListView.LayoutUpdated += ItemListView_LayoutUpdated;
        }

        private Dictionary<uint, Appearance> appearanceByClientId = new();
        private List<ServerItem> OTBItems = new();
        private bool OTBLoaded = false;
        private uint MajorVersion;
        private uint MinorVersion;
        private uint BuildNumber;
        private uint ClientVersion;

        private async void OpenOTBButton_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open OTB File",
                FileTypeFilter = [new FilePickerFileType("OTB Files") { Patterns = ["*.otb"] }],
                AllowMultiple = false
            });

            if (files.Count > 0)
            {
                string selectedFilePath = files[0].Path.LocalPath;
                appearanceByClientId.Clear();
                ItemListView.ItemsSource = null;
                OtbPathText.Text = selectedFilePath;

                OTBReader otbReader = new();
                OTBItems = new List<ServerItem>();
                bool stats = otbReader.Read(selectedFilePath);
                if (stats)
                {
                    OTBLoaded = true;
                    MajorVersion = otbReader.MajorVersion;
                    MinorVersion = otbReader.MinorVersion;
                    BuildNumber = otbReader.BuildNumber;
                    ClientVersion = otbReader.ClientVersion;

                    foreach (Appearance appearance in MainWindow.appearances!.Object)
                        appearanceByClientId[appearance.Id] = appearance;

                    var items = new List<ShowList>();
                    foreach (var item in otbReader.Items)
                    {
                        OTBItems.Add(item);
                        items.Add(new ShowList() { Id = item.ServerId, Cid = item.ClientId });
                    }
                    ItemListView.ItemsSource = items;
                }
            }
        }

        private void ItemListView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (ItemListView.SelectedIndex < 0 || ItemListView.SelectedIndex >= OTBItems.Count) return;

            ServerItem item = OTBItems[ItemListView.SelectedIndex];
            I_ServerId.Value = item.ServerId;
            I_ClientId.Value = item.ClientId;
            I_Name.Text = item.Name;
            I_StackOrder.SelectedIndex = (int)item.StackOrder;
            I_Type.SelectedIndex = (int)item.Type;
            I_GroundSpeed.Value = item.GroundSpeed;
            I_Unpassable.IsChecked = item.Unpassable;
            I_Movable.IsChecked = item.Movable;
            I_BlockMissiles.IsChecked = item.BlockMissiles;
            I_BlockPath.IsChecked = item.BlockPathfinder;
            I_Pickable.IsChecked = item.Pickupable;
            I_Stackable.IsChecked = item.Stackable;
            I_ForceUse.IsChecked = item.ForceUse;
            I_MultiUse.IsChecked = item.MultiUse;
            I_Rotateable.IsChecked = item.Rotatable;
            I_Hangable.IsChecked = item.Hangable;
            I_HookSouth.IsChecked = item.HookSouth;
            I_HookEast.IsChecked = item.HookEast;
            I_Elevation.IsChecked = item.HasElevation;
            I_IgnoreLook.IsChecked = item.IgnoreLook;
            I_Readable.IsChecked = item.Readable;
            I_FullGround.IsChecked = item.FullGround;
            I_MiniMapColor.Value = item.MinimapColor;
            I_ShowAs.Value = item.TradeAs;
            I_LightLevel.Value = item.LightLevel;
            I_LightColor.Value = item.LightColor;
            I_MaxReadWriteChars.Value = item.MaxReadWriteChars;
            I_MaxReadChars.Value = item.MaxReadChars;

            _editor.ObjectMenu.SelectedIndex = 1;
            foreach (ShowList showlist in _editor.ObjListView.Items)
            {
                if (showlist.Id == item.ClientId)
                {
                    _editor.ObjListView.SelectedItem = showlist;
                    break;
                }
            }
        }

        private void CreateMissingItems(object? sender, RoutedEventArgs e)
        {
            if (!OTBLoaded || MainWindow.appearances == null) return;

            int lastServerId = OTBItems.Max(i => (int)i.ServerId);
            ushort lastCid = OTBItems.Max(i => i.ClientId);
            uint maxCid = MainWindow.appearances.Object.Last().Id;
            lastServerId++;

            var newItems = new List<ShowList>();
            for (ushort i = 100; i <= maxCid; i++)
            {
                if (!OTBItems.Any(x => x.ClientId == i) && appearanceByClientId.ContainsKey(i))
                {
                    ServerItem item = new ServerItem(appearanceByClientId[i]);
                    item.ClientId = i;
                    item.ServerId = (ushort)lastServerId;
                    OTBItems.Add(item);
                    newItems.Add(new ShowList() { Id = item.ServerId, Cid = item.ClientId });
                    lastServerId++;
                }
            }
            NewItemListView.ItemsSource = newItems;
        }

        private void CheckMisMatcheditems(object? sender, RoutedEventArgs e)
        {
            if (!OTBLoaded) return;

            var mismatched = new List<ShowList>();
            foreach (ServerItem item in OTBItems)
            {
                if (!appearanceByClientId.ContainsKey(item.ClientId)) continue;
                ServerItem citem = new ServerItem(appearanceByClientId[item.ClientId]);
                if (!Utils.ByteArrayCompare(item.SpriteHash, citem.SpriteHash))
                    mismatched.Add(new ShowList() { Id = item.ServerId, Cid = item.ClientId });
            }
            NewItemListView.ItemsSource = mismatched;
        }

        private void ReloadItemsAttr(object? sender, RoutedEventArgs e)
        {
            if (ItemListView.SelectedItems == null) return;
            foreach (ShowList selectedItem in ItemListView.SelectedItems.Cast<ShowList>())
            {
                int idx = 0;
                for (int i = 0; i < OTBItems.Count; i++)
                {
                    if (OTBItems[i].ServerId == selectedItem.Id) { idx = i; break; }
                }
                ServerItem item = OTBItems[idx];
                if (appearanceByClientId.ContainsKey(item.ClientId))
                {
                    ServerItem itemAttr = new ServerItem(appearanceByClientId[item.ClientId]);
                    itemAttr.ServerId = item.ServerId;
                    itemAttr.ClientId = item.ClientId;
                    OTBItems[idx] = itemAttr;
                }
            }
        }

        private async void SaveAs_Click(object? sender, RoutedEventArgs e)
        {
            if (!OTBLoaded) return;

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save OTB File",
                DefaultExtension = "otb",
                FileTypeChoices = [new FilePickerFileType("OTB files") { Patterns = ["*.otb"] }]
            });

            if (file != null)
            {
                try
                {
                    OTBWriter writer = new OTBWriter(OTBItems);
                    writer.MajorVersion = MajorVersion;
                    writer.MinorVersion = MinorVersion;
                    writer.BuildNumber = BuildNumber;
                    writer.ClientVersion = ClientVersion;
                    if (writer.Write(file.Path.LocalPath))
                        Debug.WriteLine("OTB saved");
                }
                catch (UnauthorizedAccessException exception)
                {
                    ErrorManager.ShowError(exception.Message);
                }
            }
        }

        private void SaveItemButton_Click(object? sender, RoutedEventArgs e)
        {
            if (ItemListView.SelectedIndex < 0 || ItemListView.SelectedIndex >= OTBItems.Count) return;

            ServerItem item = OTBItems[ItemListView.SelectedIndex];
            item.ServerId = (ushort)(I_ServerId.Value ?? 0);
            item.ClientId = (ushort)(I_ClientId.Value ?? 0);
            item.Name = I_Name.Text ?? "";
            item.StackOrder = (TileStackOrder)I_StackOrder.SelectedIndex;
            item.Type = (ServerItemType)I_Type.SelectedIndex;
            item.GroundSpeed = (ushort)(I_GroundSpeed.Value ?? 0);
            item.Unpassable = I_Unpassable.IsChecked ?? false;
            item.Movable = I_Movable.IsChecked ?? false;
            item.BlockMissiles = I_BlockMissiles.IsChecked ?? false;
            item.BlockPathfinder = I_BlockPath.IsChecked ?? false;
            item.Pickupable = I_Pickable.IsChecked ?? false;
            item.Stackable = I_Stackable.IsChecked ?? false;
            item.ForceUse = I_ForceUse.IsChecked ?? false;
            item.MultiUse = I_MultiUse.IsChecked ?? false;
            item.Rotatable = I_Rotateable.IsChecked ?? false;
            item.Hangable = I_Hangable.IsChecked ?? false;
            item.HookSouth = I_HookSouth.IsChecked ?? false;
            item.HookEast = I_HookEast.IsChecked ?? false;
            item.HasElevation = I_Elevation.IsChecked ?? false;
            item.IgnoreLook = I_IgnoreLook.IsChecked ?? false;
            item.Readable = I_Readable.IsChecked ?? false;
            item.FullGround = I_FullGround.IsChecked ?? false;
            item.MinimapColor = (ushort)(I_MiniMapColor.Value ?? 0);
            item.TradeAs = (ushort)(I_ShowAs.Value ?? 0);
            item.LightLevel = (ushort)(I_LightLevel.Value ?? 0);
            item.LightColor = (ushort)(I_LightColor.Value ?? 0);
            item.MaxReadWriteChars = (ushort)(I_MaxReadWriteChars.Value ?? 0);
            item.MaxReadChars = (ushort)(I_MaxReadChars.Value ?? 0);
        }

        #region Scroll-based image loading

        private bool _itemScrollHooked;

        private void ItemListView_LayoutUpdated(object? sender, EventArgs e)
        {
            if (_itemScrollHooked) return;
            var sv = FindScrollViewer(ItemListView);
            if (sv != null)
            {
                sv.ScrollChanged += ItemListView_ScrollChanged;
                _itemScrollHooked = true;
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

        private void ItemListView_ScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (!OTBLoaded || ItemListView.ItemCount == 0) return;
            LoadVisibleItemImages();
        }

        private void LoadVisibleItemImages()
        {
            var sv = FindScrollViewer(ItemListView);
            if (sv == null) return;

            int itemCount = ItemListView.ItemCount;
            if (itemCount == 0) return;

            double itemHeight = 36;
            int offset = Math.Max(0, (int)(sv.Offset.Y / itemHeight));
            int visibleCount = (int)(sv.Viewport.Height / itemHeight) + 2;

            for (int i = 0; i < itemCount; i++)
            {
                if (ItemListView.Items[i] is not ShowList item) continue;

                if (i >= offset && i < Math.Min(offset + visibleCount, itemCount))
                {
                    if (item.Image == null && appearanceByClientId.ContainsKey(item.Cid))
                    {
                        try
                        {
                            if (_legacy)
                            {
                                MemoryStream? stream = LegacyAppearance.GetObjectImage(
                                    appearanceByClientId[item.Cid], MainWindow.MainSprStorage);
                                item.Image = Utils.MemoryStreamToBitmap(stream);
                            }
                            else
                            {
                                var appearance = appearanceByClientId[item.Cid];
                                if (appearance.FrameGroup.Count > 0)
                                {
                                    var sprId = appearance.FrameGroup[0].SpriteInfo.SpriteId[0];
                                    var stream = MainWindow.getSpriteStream((int)sprId);
                                    item.Image = Utils.MemoryStreamToBitmap(stream);
                                }
                            }
                        }
                        catch
                        {
                            item.Image = null;
                        }
                    }
                }
                else
                {
                    item.Image = null;
                }
            }
        }

        #endregion
    }
}
