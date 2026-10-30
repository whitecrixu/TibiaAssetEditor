using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SkiaSharp;
using Tibia.Protobuf.Appearances;

namespace Assets_Editor
{
    /// <summary>
    /// Editor for TFS 1.x revscript NPCs (see <see cref="NpcScript"/>): outfit, health, movement, shop lists and
    /// the plain-text messages / keyword answers, plus a shop check across all loaded NPCs.
    /// </summary>
    public partial class NpcEditor : Window
    {
        private readonly Dictionary<int, Appearance> _outfitById = new();
        private readonly Dictionary<int, Appearance> _itemById = new();
        private Dictionary<int, string>? _itemNames;
        private List<NpcListItem> _items = new();
        private NpcListItem? _current;
        private List<NpcRegistry.Finding> _findings = new();

        public NpcEditor()
        {
            InitializeComponent();
            CheckFilter.SelectedIndex = 0;
            if (MainWindow.appearances != null)
            {
                foreach (var a in MainWindow.appearances.Outfit) _outfitById[(int)a.Id] = a;
                foreach (var a in MainWindow.appearances.Object) _itemById[(int)a.Id] = a;
            }
            if (NpcRegistry.Folder != null)
                ShowLoaded();
        }

        // ── loading ───────────────────────────────────────────────────────

        private async void LoadFolder_Click(object? sender, RoutedEventArgs e)
        {
            if (Unsaved() > 0 && !await ErrorManager.ConfirmAsync("Unsaved changes", $"{Unsaved()} NPCs have unsaved changes. Load another folder and drop them?"))
                return;
            IStorageFolder? start = null;
            string? last = NpcRegistry.Folder ?? MainWindow.SettingsList.LastNpcFolder;
            if (!string.IsNullOrEmpty(last) && Directory.Exists(last))
                start = await StorageProvider.TryGetFolderFromPathAsync(last);
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "NPC scripts folder (e.g. data/scripts/npcs)",
                SuggestedStartLocation = start,
            });
            if (folders.Count == 0) return;
            LoadFolder(folders[0].Path.LocalPath);
        }

        private async void Reload_Click(object? sender, RoutedEventArgs e)
        {
            if (NpcRegistry.Folder == null) return;
            if (Unsaved() > 0 && !await ErrorManager.ConfirmAsync("Unsaved changes", $"{Unsaved()} NPCs have unsaved changes. Reload and drop them?"))
                return;
            LoadFolder(NpcRegistry.Folder);
        }

        public void LoadFolder(string folder)
        {
            int count = NpcRegistry.LoadFolder(folder);
            MainWindow.SettingsList.LastNpcFolder = folder;
            MainWindow.PersistSettings();
            ShowLoaded();
            StatusText.Text = $"Loaded {count} NPCs ({NpcRegistry.Scripts.Count(s => s.HasShop)} with a shop) from {folder}";
        }

        private void ShowLoaded()
        {
            string folder = NpcRegistry.Folder!;
            PathText.Text = folder;
            _itemNames = NpcRegistry.LoadItemNames(folder, out string? xml);
            if (xml != null)
                MainWindow.Log($"NPC editor: item names from {xml}");
            _items = NpcRegistry.Scripts.Select(s => new NpcListItem(s, folder)).ToList();
            _findings.Clear();
            CheckGrid.ItemsSource = null;
            RefreshList();
        }

        private int Unsaved() => NpcRegistry.Scripts.Count(s => s.Modified);

        // ── list ──────────────────────────────────────────────────────────

        private void RefreshList()
        {
            string filter = SearchBox.Text?.Trim() ?? "";
            bool shopsOnly = ShopsOnly.IsChecked == true;
            NpcList.ItemsSource = _items.Where(i =>
                (!shopsOnly || i.Script.HasShop) &&
                (filter.Length == 0 || i.Matches(filter))).ToList();
        }

        private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => RefreshList();

        private void ShopsOnly_Changed(object? sender, RoutedEventArgs e) => RefreshList();

        private void NpcList_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        {
            if (e.Container.DataContext is NpcListItem item && item.Image == null)
                item.Image = OutfitImage(item.Script);
        }

        private void NpcList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            _current = NpcList.SelectedItem as NpcListItem;
            ShowCurrent();
        }

        private void ShowCurrent()
        {
            var npc = _current?.Script;
            if (npc == null)
            {
                HeaderText.Text = "No selection";
                HeaderImage.Source = null;
                OutfitFields.ItemsSource = StatFields.ItemsSource = MessageFields.ItemsSource = DialogFields.ItemsSource = null;
                BuyableGrid.ItemsSource = SellableGrid.ItemsSource = null;
                return;
            }

            HeaderText.Text = npc.Name;
            HeaderHint.Text = Path.GetRelativePath(NpcRegistry.Folder!, npc.Path)
                              + (npc.HasShop ? $" · sells {npc.Buyable?.Items.Count ?? 0}, buys {npc.Sellable?.Items.Count ?? 0} items" : "");
            HeaderImage.Source = OutfitImage(npc);

            OutfitFields.ItemsSource = npc.Outfit.Select(t => new NumberField(t.Key.Replace("look", ""), t, OutfitChanged)).ToList();
            var stats = new List<NumberField>();
            if (npc.HealthNow != null) stats.Add(new NumberField("Health", npc.HealthNow, Changed));
            if (npc.HealthMax != null) stats.Add(new NumberField("Max health", npc.HealthMax, Changed));
            if (npc.WalkInterval != null) stats.Add(new NumberField("Walk interval", npc.WalkInterval, Changed));
            if (npc.WalkRadius != null) stats.Add(new NumberField("Walk radius", npc.WalkRadius, Changed));
            StatFields.ItemsSource = stats;

            MessageFields.ItemsSource = npc.Messages.Select(m => new TextField(MessageLabel(m.Kind), "", m.Token, Changed)).ToList();
            DialogFields.ItemsSource = npc.Dialogs.Select(d => new TextField(string.Join(", ", d.Keywords), $"line {d.Line}", d.Token, Changed)).ToList();

            ShopNoteText.Text = npc.ShopNote ?? "";
            ShopNoteText.IsVisible = npc.ShopNote != null;
            BuyableGrid.ItemsSource = npc.Buyable == null ? null : Rows(npc.Buyable);
            SellableGrid.ItemsSource = npc.Sellable == null ? null : Rows(npc.Sellable);
            BuyableTitle.Text = "NPC sells (the player buys)" + (npc.Buyable?.TableName is { } b ? $" · table {b}" : npc.Buyable == null ? " · none" : "");
            SellableTitle.Text = "NPC buys (the player sells)" + (npc.Sellable?.TableName is { } s ? $" · table {s}" : npc.Sellable == null ? " · none" : "");
        }

        private static string MessageLabel(string kind) => kind.Replace("MESSAGE_", "") switch
        {
            "GREET" => "Greeting",
            "FAREWELL" => "Farewell",
            "WALKAWAY" => "Walk away",
            "SENDTRADE" => "Trade window",
            var other => other.ToLowerInvariant(),
        };

        private ObservableCollection<ShopRow> Rows(NpcShop shop) =>
            new(shop.Items.Select(i => NewRow(i)));

        private ShopRow NewRow(ShopItem item)
        {
            var row = new ShopRow(item);
            RefreshRow(row);
            return row;
        }

        private void RefreshRow(ShopRow row)
        {
            row.Image = ItemImage(row.ItemId);
            string? xmlName = _itemNames != null && _itemNames.TryGetValue(row.ItemId, out var n) ? n : null;
            row.Check = _itemById.Count > 0 && !_itemById.ContainsKey(row.ItemId) ? "⚠ id not in the loaded client"
                      : _itemNames == null ? ""
                      : xmlName == null ? "⚠ no items.xml entry"
                      : string.Equals(xmlName, row.Name.Trim(), StringComparison.OrdinalIgnoreCase) ? "✓ " + xmlName
                      : "≠ " + xmlName;
        }

        // ── edits ─────────────────────────────────────────────────────────

        private void Changed() => UpdateStatus();

        private void OutfitChanged()
        {
            if (_current == null) return;
            _current.Image = OutfitImage(_current.Script);
            HeaderImage.Source = _current.Image;
            UpdateStatus();
        }

        private void ShopGrid_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
        {
            if (e.Row.DataContext is ShopRow row)
                RefreshRow(row);
            UpdateStatus();
        }

        private NpcShop? ShopFor(object? sender) =>
            (sender as Control)?.Tag as string == "buyable" ? _current?.Script.Buyable : _current?.Script.Sellable;

        private DataGrid GridFor(object? sender) =>
            (sender as Control)?.Tag as string == "buyable" ? BuyableGrid : SellableGrid;

        private void AddShopRow_Click(object? sender, RoutedEventArgs e)
        {
            NpcShop? shop = ShopFor(sender);
            if (shop == null)
            {
                StatusText.Text = _current == null ? "Select an NPC first."
                    : "This NPC has no such shop list in its script; add the parameter in the .lua file first.";
                return;
            }
            var item = new ShopItem { Name = "new item", ItemId = 0, Price = 0 };
            shop.Items.Add(item);
            var grid = GridFor(sender);
            var row = NewRow(item);
            ((ObservableCollection<ShopRow>)grid.ItemsSource!).Add(row);
            grid.SelectedItem = row;
            grid.ScrollIntoView(row, null);
            UpdateStatus();
        }

        private void RemoveShopRow_Click(object? sender, RoutedEventArgs e)
        {
            NpcShop? shop = ShopFor(sender);
            var grid = GridFor(sender);
            if (shop == null || grid.SelectedItem is not ShopRow row) return;
            shop.Items.Remove(row.Item);
            ((ObservableCollection<ShopRow>)grid.ItemsSource!).Remove(row);
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            int unsaved = Unsaved();
            StatusText.Text = unsaved == 0 ? "No unsaved changes" : $"{unsaved} NPC{(unsaved == 1 ? "" : "s")} with unsaved changes";
        }

        // ── saving ────────────────────────────────────────────────────────

        private void SaveCurrent_Click(object? sender, RoutedEventArgs e)
        {
            if (_current == null) { StatusText.Text = "Select an NPC first."; return; }
            if (!_current.Script.Modified) { StatusText.Text = $"{_current.Name}: nothing to save."; return; }
            Save(new[] { _current.Script });
        }

        private void SaveAll_Click(object? sender, RoutedEventArgs e)
        {
            var changed = NpcRegistry.Scripts.Where(s => s.Modified).ToList();
            if (changed.Count == 0) { StatusText.Text = "Nothing to save."; return; }
            Save(changed);
        }

        private void Save(IReadOnlyList<NpcScript> scripts)
        {
            var failed = new List<string>();
            foreach (var script in scripts)
            {
                try
                {
                    script.Save();
                    MainWindow.Log($"NPC {script.Name}: saved {script.Path} (previous version kept as -bak)");
                }
                catch (Exception ex)
                {
                    failed.Add($"{script.Name}: {ex.Message}");
                    MainWindow.Log($"NPC {script.Name}: save failed: {ex.Message}", "Error");
                }
            }
            ShowCurrent(); // spans moved after the save
            int saved = scripts.Count - failed.Count;
            StatusText.Text = $"Saved {saved} NPC{(saved == 1 ? "" : "s")} (previous versions kept as .lua-bak)" + (failed.Count > 0 ? $"; {failed.Count} failed" : "");
            if (failed.Count > 0)
                ErrorManager.ShowWarning("Not saved:\n" + string.Join("\n", failed));
        }

        // ── shop check ────────────────────────────────────────────────────

        private void CheckShops_Click(object? sender, RoutedEventArgs e)
        {
            if (NpcRegistry.Scripts.Count == 0) { CheckSummary.Text = "Load the NPC folder first."; return; }
            _findings = NpcRegistry.CheckShops(NpcRegistry.Scripts,
                _itemById.Count > 0 ? id => _itemById.ContainsKey(id) : null,
                _itemNames != null ? id => _itemNames.TryGetValue(id, out var n) ? n : null : null);
            var counts = _findings.GroupBy(f => f.Category).Select(g => $"{g.Key}: {g.Count()}");
            CheckSummary.Text = _findings.Count == 0 ? "No problems found."
                : string.Join(" · ", counts)
                  + (_itemById.Count == 0 ? " · load a client in the main window to check item ids" : "")
                  + (_itemNames == null ? " · no items.xml found above the NPC folder" : "");
            ApplyCheckFilter();
        }

        private void CheckFilter_Changed(object? sender, SelectionChangedEventArgs e) => ApplyCheckFilter();

        private void ApplyCheckFilter()
        {
            if (CheckGrid == null) return;
            string? category = CheckFilter.SelectedIndex > 0 ? (CheckFilter.SelectedItem as ComboBoxItem)?.Content as string : null;
            CheckGrid.ItemsSource = _findings.Where(f => category == null || f.Category == category).ToList();
        }

        private void CheckGrid_DoubleTapped(object? sender, TappedEventArgs e)
        {
            if (CheckGrid.SelectedItem is not NpcRegistry.Finding finding) return;
            string first = finding.Npcs.Split(" (")[0];
            var item = _items.FirstOrDefault(i => i.Name == first);
            if (item == null) return;
            SearchBox.Text = "";
            ShopsOnly.IsChecked = false;
            RefreshList();
            NpcList.SelectedItem = item;
            NpcList.ScrollIntoView(item);
            DetailTabs.SelectedIndex = 1;
        }

        // ── pictures ──────────────────────────────────────────────────────

        private Bitmap? OutfitImage(NpcScript npc)
        {
            if (npc.OutfitValue("lookType") is > 0 and var type && _outfitById.TryGetValue(type, out var outfit))
                return ColouredOutfit(outfit, npc) ?? FirstSprite(outfit);
            if (npc.OutfitValue("lookTypeEx") is > 0 and var ex && _itemById.TryGetValue(ex, out var item))
                return FirstSprite(item);
            return null;
        }

        // legacy clients: the whole figure facing south, its colour mask (layer 1) painted in the NPC's colours
        private static Bitmap? ColouredOutfit(Appearance outfit, NpcScript npc)
        {
            if (!MainWindow.LegacyClient || MainWindow.MainSprStorage == null || outfit.FrameGroup.Count == 0)
                return null;
            try
            {
                var info = outfit.FrameGroup[0].SpriteInfo;
                // legacy sprite info: PatternX = directions, PatternLayers = base + colour mask
                int south = info.PatternX > 0 ? (int)(2 % info.PatternX) : 0;
                using var figure = SKBitmap.Decode(LegacyAppearance.GetObjectImage(outfit, MainWindow.MainSprStorage, 0, south, 0, 0, 0, 0).ToArray());
                if (figure == null)
                    return null;
                if (info.PatternLayers > 1)
                {
                    using var mask = SKBitmap.Decode(LegacyAppearance.GetObjectImage(outfit, MainWindow.MainSprStorage, 0, south, 0, 0, 0, 1).ToArray());
                    if (mask != null)
                        Utils.ColorizeOutfit(mask, figure,
                            Utils.GetOutfitColor(npc.OutfitValue("lookHead")), Utils.GetOutfitColor(npc.OutfitValue("lookBody")),
                            Utils.GetOutfitColor(npc.OutfitValue("lookLegs")), Utils.GetOutfitColor(npc.OutfitValue("lookFeet")));
                }
                using var image = SKImage.FromBitmap(figure);
                using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                return new Bitmap(png.AsStream());
            }
            catch
            {
                return null;
            }
        }

        private Bitmap? ItemImage(int id) => _itemById.TryGetValue(id, out var a) ? FirstSprite(a) : null;

        private static Bitmap? FirstSprite(Appearance? a)
        {
            try
            {
                if (a == null || a.FrameGroup.Count == 0 || a.FrameGroup[0].SpriteInfo.SpriteId.Count == 0) return null;
                return Utils.MemoryStreamToBitmap(MainWindow.getSpriteStream((int)a.FrameGroup[0].SpriteInfo.SpriteId[0]));
            }
            catch
            {
                return null;
            }
        }

        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (_closeConfirmed || Unsaved() == 0) return;
            e.Cancel = true;
            if (await ErrorManager.ConfirmAsync("Unsaved changes", $"{Unsaved()} NPCs have unsaved changes. Close and drop them?"))
            {
                // the edits live in the shared registry; reload so a reopened editor starts clean
                if (NpcRegistry.Folder != null) NpcRegistry.LoadFolder(NpcRegistry.Folder);
                _closeConfirmed = true;
                Close();
            }
        }

        private bool _closeConfirmed;
    }

    public class NpcListItem : INotifyPropertyChanged
    {
        private Bitmap? _image;

        public NpcListItem(NpcScript script, string folder)
        {
            Script = script;
            string dir = Path.GetDirectoryName(Path.GetRelativePath(folder, script.Path)) ?? "";
            int sells = script.Buyable?.Items.Count ?? 0, buys = script.Sellable?.Items.Count ?? 0;
            Hint = (dir.Length > 0 ? dir + " · " : "") + (script.HasShop ? $"shop {sells} / {buys}" : script.ShopNote != null ? "shop by code" : "no shop");
        }

        public NpcScript Script { get; }
        public string Name => Script.Name;
        public string Hint { get; }

        public Bitmap? Image
        {
            get => _image;
            set { _image = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image))); }
        }

        public bool Matches(string filter) =>
            Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(Script.Path).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (Script.Buyable?.Items ?? []).Concat(Script.Sellable?.Items ?? []).Any(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class NumberField
    {
        private readonly LuaToken _token;
        private readonly Action _changed;

        public NumberField(string label, LuaToken token, Action changed)
        {
            Label = label;
            _token = token;
            _changed = changed;
        }

        public string Label { get; }

        public decimal? Value
        {
            get => _token.IntValue;
            set
            {
                int v = (int)(value ?? 0);
                if (v == _token.IntValue) return;
                _token.IntValue = v;
                _changed();
            }
        }
    }

    public class TextField
    {
        private readonly LuaToken _token;
        private readonly Action _changed;

        public TextField(string label, string tip, LuaToken token, Action changed)
        {
            Label = label;
            Tip = tip;
            _token = token;
            _changed = changed;
        }

        public string Label { get; }
        public string Tip { get; }

        public string Text
        {
            get => _token.StringValue;
            set
            {
                if (value == _token.StringValue) return;
                _token.StringValue = value;
                _changed();
            }
        }
    }

    public class ShopRow : INotifyPropertyChanged
    {
        private Bitmap? _image;
        private string _check = "";

        public ShopRow(ShopItem item) => Item = item;

        public ShopItem Item { get; }

        public string Name { get => Item.Name; set { Item.Name = value.Replace(";", "").Replace(",", ""); On(nameof(Name)); } }
        public int ItemId { get => Item.ItemId; set { Item.ItemId = Math.Max(0, value); On(nameof(ItemId)); } }
        public int Price { get => Item.Price; set { Item.Price = Math.Max(0, value); On(nameof(Price)); } }
        public string Extra { get => Item.Extra; set { Item.Extra = (value ?? "").Replace(";", "").Trim(); On(nameof(Extra)); } }

        public Bitmap? Image { get => _image; set { _image = value; On(nameof(Image)); } }
        public string Check { get => _check; set { _check = value; On(nameof(Check)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void On(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
