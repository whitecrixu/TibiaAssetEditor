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
using Tibia.Protobuf.Appearances;

namespace Assets_Editor
{
    /// <summary>
    /// Editor for revscript NPCs, TFS 1.x and Canary / Crystal Server (see <see cref="NpcScript"/>): outfit, health,
    /// movement, shop lists and
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
                BuyableGrid.ItemsSource = SellableGrid.ItemsSource = CanaryGrid.ItemsSource = null;
                return;
            }

            HeaderText.Text = npc.Name;
            HeaderHint.Text = Path.GetRelativePath(NpcRegistry.Folder!, npc.Path)
                              + (npc.Format == NpcFormat.Canary ? " · Canary / Crystal format" : " · TFS 1.x format")
                              + (npc.HasShop ? $" · sells {npc.SoldItems().Count()}, buys {npc.BoughtItems().Count()} items" : "");
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
            bool canary = npc.Format == NpcFormat.Canary;
            CanaryShopPanel.IsVisible = canary;
            BuyablePanel.IsVisible = SellablePanel.IsVisible = !canary;
            CanaryGrid.ItemsSource = npc.CanaryShop == null ? null
                : new ObservableCollection<CanaryRow>(npc.CanaryShop.Items.Select(i => { var r = new CanaryRow(i); RefreshRow(r); return r; }));
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
            "VOICE" => "Voice",
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

        // Canary / Crystal shops use the ids of 13.x+ clients: an 8.60 client loaded in the main window has other
        // things under those ids, so its pictures and id checks would be wrong
        private bool ClientMatches(NpcScript? npc) =>
            _itemById.Count > 0 && !(npc?.Format == NpcFormat.Canary && MainWindow.LegacyClient);

        private void RefreshRow(IShopRow row)
        {
            bool client = ClientMatches(_current?.Script);
            row.Image = client ? ItemImage(row.ItemId) : null;
            string? xmlName = _itemNames != null && _itemNames.TryGetValue(row.ItemId, out var n) ? n : null;
            row.Check = client && !_itemById.ContainsKey(row.ItemId) ? "⚠ id not in the loaded client"
                      : _itemNames == null ? ""
                      : xmlName == null ? "⚠ no items.xml entry"
                      : row.Name.Trim().Length == 0 ? "uses its name: " + xmlName
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
            if (e.Row.DataContext is IShopRow row)
                RefreshRow(row);
            UpdateStatus();
        }

        private NpcShop? ShopFor(object? sender) =>
            (sender as Control)?.Tag as string == "buyable" ? _current?.Script.Buyable : _current?.Script.Sellable;

        private DataGrid GridFor(object? sender) =>
            (sender as Control)?.Tag as string == "buyable" ? BuyableGrid : SellableGrid;

        private void AddShopRow_Click(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag as string == "canary")
            {
                if (_current?.Script.CanaryShop is not { } canaryShop)
                {
                    StatusText.Text = _current == null ? "Select an NPC first." : "This NPC has no npcConfig.shop table; add it in the .lua file first.";
                    return;
                }
                var added = new CanaryShopItem { Name = "new item", Buy = 0 };
                canaryShop.Items.Add(added);
                var canaryRow = new CanaryRow(added);
                RefreshRow(canaryRow);
                ((ObservableCollection<CanaryRow>)CanaryGrid.ItemsSource!).Add(canaryRow);
                CanaryGrid.SelectedItem = canaryRow;
                CanaryGrid.ScrollIntoView(canaryRow, null);
                UpdateStatus();
                return;
            }
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
            if ((sender as Control)?.Tag as string == "canary")
            {
                if (_current?.Script.CanaryShop is { } canaryShop && CanaryGrid.SelectedItem is CanaryRow canaryRow)
                {
                    canaryShop.Items.Remove(canaryRow.Item);
                    ((ObservableCollection<CanaryRow>)CanaryGrid.ItemsSource!).Remove(canaryRow);
                    UpdateStatus();
                }
                return;
            }
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
                ClientMatches(NpcRegistry.Scripts.FirstOrDefault()) ? id => _itemById.ContainsKey(id) : null,
                _itemNames != null ? id => _itemNames.TryGetValue(id, out var n) ? n : null : null);
            var counts = _findings.GroupBy(f => f.Category).Select(g => $"{g.Key}: {g.Count()}");
            CheckSummary.Text = _findings.Count == 0 ? "No problems found."
                : string.Join(" · ", counts)
                  + (_itemById.Count == 0 ? " · load a client in the main window to check item ids"
                     : !ClientMatches(NpcRegistry.Scripts.FirstOrDefault()) ? " · item ids not checked: the loaded client is a legacy one, these NPCs use 13.x+ ids" : "")
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
            if (!ClientMatches(npc))
                return null;
            if (npc.OutfitValue("lookType") is > 0 and var type && _outfitById.TryGetValue(type, out var outfit))
                return OutfitPreview.Outfit(outfit, npc.OutfitValue("lookHead"), npc.OutfitValue("lookBody"),
                    npc.OutfitValue("lookLegs"), npc.OutfitValue("lookFeet"));
            if (npc.OutfitValue("lookTypeEx") is > 0 and var ex && _itemById.TryGetValue(ex, out var item))
                return OutfitPreview.Item(item);
            return null;
        }

        private Bitmap? ItemImage(int id) => _itemById.TryGetValue(id, out var a) ? OutfitPreview.FirstSprite(a) : null;

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
            int sells = script.SoldItems().Count(), buys = script.BoughtItems().Count();
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
            || Script.SoldItems().Concat(Script.BoughtItems()).Any(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

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

    public interface IShopRow
    {
        string Name { get; }
        int ItemId { get; }
        Bitmap? Image { get; set; }
        string Check { get; set; }
    }

    /// <summary>A Canary / Crystal shop row; empty Buy / Sell means the item does not go that way.</summary>
    public class CanaryRow : IShopRow, INotifyPropertyChanged
    {
        private Bitmap? _image;
        private string _check = "";

        public CanaryRow(CanaryShopItem item) => Item = item;

        public CanaryShopItem Item { get; }

        public string Name { get => Item.Name; set { Item.Name = value ?? ""; On(nameof(Name)); } }
        public int ItemId { get => Item.ClientId; set { Item.ClientId = Math.Max(0, value); On(nameof(ItemId)); } }
        public string Buy { get => Item.Buy?.ToString() ?? ""; set { Item.Buy = Price(value); On(nameof(Buy)); } }
        public string Sell { get => Item.Sell?.ToString() ?? ""; set { Item.Sell = Price(value); On(nameof(Sell)); } }
        public string Extra { get => Item.Extra; set { Item.Extra = (value ?? "").Trim().Trim(','); On(nameof(Extra)); } }

        public Bitmap? Image { get => _image; set { _image = value; On(nameof(Image)); } }
        public string Check { get => _check; set { _check = value; On(nameof(Check)); } }

        private static int? Price(string? text) =>
            int.TryParse(text?.Trim(), out int v) && v >= 0 ? v : null;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void On(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public class ShopRow : IShopRow, INotifyPropertyChanged
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
