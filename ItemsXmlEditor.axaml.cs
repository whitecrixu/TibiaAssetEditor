using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Tibia.Protobuf.Appearances;

namespace Assets_Editor
{
    public partial class ItemsXmlEditor : Window
    {
        public class AttrKv
        {
            public string Key { get; set; } = "";
            public string Value { get; set; } = "";
        }

        private ObservableCollection<ItemEntry> _filtered = new();
        private ObservableCollection<AttrKv> _currentAttrs = new();
        private ItemEntry? _currentEntry;

        // Cache of appearance lookup keyed by server-side id
        // (for items, the Tibia client id matches the items.xml id)
        private Dictionary<uint, Appearance> _appearanceById = new();

        public ItemsXmlEditor()
        {
            InitializeComponent();
            AttrsGrid.ItemsSource = _currentAttrs;

            BuildAppearanceLookup();

            if (ItemsXml.IsLoaded)
            {
                PathText.Text = ItemsXml.LoadedPath ?? "";
                RefreshList();
            }
        }

        private void BuildAppearanceLookup()
        {
            _appearanceById.Clear();
            if (MainWindow.appearances == null) return;
            foreach (var a in MainWindow.appearances.Object)
                _appearanceById[a.Id] = a;
        }

        private void RefreshList()
        {
            string? filter = SearchBox.Text?.Trim();
            IEnumerable<ItemEntry> query = ItemsXml.Items.Values.OrderBy(e => e.Id);
            if (!string.IsNullOrEmpty(filter))
            {
                query = query.Where(e =>
                    e.Id.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    (e.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
            }
            _filtered = new ObservableCollection<ItemEntry>(query);
            ItemList.ItemsSource = _filtered;
        }

        // ── Image loading via container virtualization ─────────────────────
        private void ItemList_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        {
            if (e.Container.DataContext is ItemEntry entry)
                LoadImageFor(entry);
        }

        private void ItemList_ContainerClearing(object? sender, ContainerClearingEventArgs e)
        {
            if (e.Container.DataContext is ItemEntry entry)
                entry.Image = null;
        }

        private void LoadImageFor(ItemEntry entry)
        {
            if (entry.Image != null) return;
            if (!_appearanceById.TryGetValue(entry.Id, out var appearance)) return;
            if (appearance.FrameGroup.Count == 0) return;

            try
            {
                var info = appearance.FrameGroup[0].SpriteInfo;
                if (info.SpriteId.Count == 0) return;
                var stream = MainWindow.getSpriteStream((int)info.SpriteId[0]);
                if (stream != null)
                    entry.Image = Utils.MemoryStreamToBitmap(stream);
            }
            catch { /* image is best-effort */ }
        }

        // ── Toolbar actions ─────────────────────────────────────────────────
        private async void Open_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open items.xml",
                FileTypeFilter = [new FilePickerFileType("items.xml") { Patterns = ["*.xml"] }]
            });
            if (files.Count == 0) return;

            if (ItemsXml.Load(files[0].Path.LocalPath))
            {
                PathText.Text = files[0].Path.LocalPath;
                StatusText.Text = $"Loaded {ItemsXml.Items.Count} items.";
                BuildAppearanceLookup();
                RefreshList();
            }
            else
            {
                ErrorManager.ShowError("Failed to parse items.xml");
            }
        }

        private void Save_Click(object? sender, RoutedEventArgs e)
        {
            ApplyChanges_Click(sender, e);
            if (ItemsXml.Save())
                StatusText.Text = $"Saved to {ItemsXml.LoadedPath} ({ItemsXml.Items.Count} items)";
            else
                ErrorManager.ShowError("Save failed.");
        }

        private async void SaveAs_Click(object? sender, RoutedEventArgs e)
        {
            ApplyChanges_Click(sender, e);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save items.xml",
                DefaultExtension = "xml",
                SuggestedFileName = "items.xml",
                FileTypeChoices = [new FilePickerFileType("items.xml") { Patterns = ["*.xml"] }]
            });
            if (file == null) return;

            if (ItemsXml.Save(file.Path.LocalPath))
            {
                PathText.Text = file.Path.LocalPath;
                StatusText.Text = $"Saved to {file.Path.LocalPath}";
            }
            else
            {
                ErrorManager.ShowError("Save failed.");
            }
        }

        private void AddNew_Click(object? sender, RoutedEventArgs e)
        {
            ushort newId = ItemsXml.Items.Count > 0
                ? (ushort)(ItemsXml.Items.Keys.Max() + 1) : (ushort)100;
            var entry = new ItemEntry { Id = newId, Name = $"new item {newId}" };
            ItemsXml.Items[newId] = entry;
            LoadImageFor(entry);
            RefreshList();
            ItemList.SelectedItem = entry;
            StatusText.Text = $"Added entry {newId}";
        }

        private void DeleteSelected_Click(object? sender, RoutedEventArgs e)
        {
            if (ItemList.SelectedItem is ItemEntry entry)
            {
                ItemsXml.Items.Remove(entry.Id);
                RefreshList();
                StatusText.Text = $"Deleted entry {entry.Id}";
            }
        }

        private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => RefreshList();

        // ── Selection → populate form ───────────────────────────────────────
        private void ItemList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (ItemList.SelectedItem is not ItemEntry entry)
            {
                _currentEntry = null;
                _currentAttrs.Clear();
                HeaderIdText.Text = "No selection";
                HeaderNameText.Text = "";
                HeaderTypeText.Text = "";
                HeaderImage.Source = null;
                return;
            }

            _currentEntry = entry;
            LoadImageFor(entry);

            // Header
            HeaderIdText.Text = $"ID {entry.Id}";
            HeaderNameText.Text = string.IsNullOrEmpty(entry.Name) ? "(unnamed)" : entry.Name;
            HeaderTypeText.Text = BuildTypeHint(entry);
            HeaderImage.Source = entry.Image;

            // Basic
            IdBox.Value = entry.Id;
            ArticleBox.Text = entry.Article;
            NameBox.Text = entry.Name;
            PluralBox.Text = entry.Plural;
            DescriptionBox.Text = entry.Description;
            TypeBox.Text = entry.Type;
            WeightBox.Value = entry.Weight;

            // Combat
            SlotTypeBox.Text = entry.SlotType;
            WeaponTypeBox.Text = entry.WeaponType;
            AttackBox.Value = entry.Attack;
            DefenseBox.Value = entry.Defense;
            ExtraDefBox.Value = entry.ExtraDef;
            ArmorBox.Value = entry.Armor;
            RangeBox.Value = entry.Range;
            BreakChanceBox.Value = entry.BreakChance;
            ShootTypeBox.Text = entry.ShootType;
            MaxHitChanceBox.Value = entry.MaxHitChance;
            ReqLevelBox.Value = entry.RequiredLevel;
            ReqMagBox.Value = entry.RequiredMagLevel;
            ChargesBox.Value = entry.Charges;
            DurationBox.Value = entry.Duration;

            // Properties
            DecayToBox.Value = entry.DecayTo;
            RotateToBox.Value = entry.RotateTo;
            ContainerSizeBox.Value = entry.ContainerSize;
            SpeedBox.Value = entry.Speed;
            MaxTextLenBox.Value = entry.MaxTextLen;
            WriteOnceIdBox.Value = entry.WriteOnceItemId;
            CorpseTypeBox.Text = entry.CorpseType;
            FluidSourceBox.Text = entry.FluidSource;
            EffectBox.Text = entry.Effect;
            WrapableBox.Value = entry.Wrapable;
            UnwrapableBox.Value = entry.Unwrapable;

            SkillSwordBox.Value = entry.SkillSword;
            SkillAxeBox.Value = entry.SkillAxe;
            SkillClubBox.Value = entry.SkillClub;
            SkillDistBox.Value = entry.SkillDist;
            SkillShieldBox.Value = entry.SkillShield;
            SkillFishBox.Value = entry.SkillFish;
            MagicPointsBox.Value = entry.MagicPoints;

            // Flags
            StopDurationCb.IsChecked = entry.StopDuration;
            ShowCountCb.IsChecked = entry.ShowCount;
            ShowAttributesCb.IsChecked = entry.ShowAttributes;
            ReadableCb.IsChecked = entry.Readable;
            WriteableCb.IsChecked = entry.Writeable;
            AllowDistReadCb.IsChecked = entry.AllowDistRead;
            ReplaceAbleCb.IsChecked = entry.ReplaceAble;
            BlockProjectileCb.IsChecked = entry.BlockProjectile;
            MoveAbleCb.IsChecked = entry.MoveAble;
            PickupAbleCb.IsChecked = entry.PickupAble;

            // Custom
            _currentAttrs.Clear();
            foreach (var kv in entry.CustomAttributes)
                _currentAttrs.Add(new AttrKv { Key = kv.Key, Value = kv.Value });
        }

        private static string BuildTypeHint(ItemEntry e)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(e.Type))       parts.Add(e.Type);
            if (!string.IsNullOrEmpty(e.SlotType))   parts.Add(e.SlotType);
            if (!string.IsNullOrEmpty(e.WeaponType)) parts.Add(e.WeaponType);
            if (e.Weight > 0)                        parts.Add($"{e.Weight / 100.0:0.00}kg");
            return parts.Count == 0 ? "" : string.Join("  •  ", parts);
        }

        // ── Save form back to model ─────────────────────────────────────────
        private void ApplyChanges_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentEntry == null) return;

            ushort newId = (ushort)(IdBox.Value ?? _currentEntry.Id);
            if (newId != _currentEntry.Id && ItemsXml.Items.ContainsKey(newId))
            {
                ErrorManager.ShowError($"Item {newId} already exists in items.xml; pick a free id.");
                IdBox.Value = _currentEntry.Id;
                return;
            }
            if (newId != _currentEntry.Id)
            {
                ItemsXml.Items.Remove(_currentEntry.Id);
                _currentEntry.Id = newId;
                ItemsXml.Items[newId] = _currentEntry;
            }

            _currentEntry.Article      = ArticleBox.Text ?? "";
            _currentEntry.Name         = NameBox.Text ?? "";
            _currentEntry.Plural       = PluralBox.Text ?? "";
            _currentEntry.Description  = DescriptionBox.Text ?? "";
            _currentEntry.Type         = TypeBox.Text ?? "";
            _currentEntry.Weight       = (int)(WeightBox.Value ?? 0);

            _currentEntry.SlotType     = SlotTypeBox.Text ?? "";
            _currentEntry.WeaponType   = WeaponTypeBox.Text ?? "";
            _currentEntry.Attack       = (int)(AttackBox.Value ?? 0);
            _currentEntry.Defense      = (int)(DefenseBox.Value ?? 0);
            _currentEntry.ExtraDef     = (int)(ExtraDefBox.Value ?? 0);
            _currentEntry.Armor        = (int)(ArmorBox.Value ?? 0);
            _currentEntry.Range        = (int)(RangeBox.Value ?? 0);
            _currentEntry.BreakChance  = (int)(BreakChanceBox.Value ?? 0);
            _currentEntry.ShootType    = ShootTypeBox.Text ?? "";
            _currentEntry.MaxHitChance = (int)(MaxHitChanceBox.Value ?? 0);
            _currentEntry.RequiredLevel    = (int)(ReqLevelBox.Value ?? 0);
            _currentEntry.RequiredMagLevel = (int)(ReqMagBox.Value ?? 0);
            _currentEntry.Charges      = (int)(ChargesBox.Value ?? 0);
            _currentEntry.Duration     = (int)(DurationBox.Value ?? 0);

            _currentEntry.DecayTo         = (int)(DecayToBox.Value ?? 0);
            _currentEntry.RotateTo        = (int)(RotateToBox.Value ?? 0);
            _currentEntry.ContainerSize   = (int)(ContainerSizeBox.Value ?? 0);
            _currentEntry.Speed           = (int)(SpeedBox.Value ?? 0);
            _currentEntry.MaxTextLen      = (int)(MaxTextLenBox.Value ?? 0);
            _currentEntry.WriteOnceItemId = (int)(WriteOnceIdBox.Value ?? 0);
            _currentEntry.CorpseType      = CorpseTypeBox.Text ?? "";
            _currentEntry.FluidSource     = FluidSourceBox.Text ?? "";
            _currentEntry.Effect          = EffectBox.Text ?? "";
            _currentEntry.Wrapable        = (int)(WrapableBox.Value ?? 0);
            _currentEntry.Unwrapable      = (int)(UnwrapableBox.Value ?? 0);

            _currentEntry.SkillSword  = (int)(SkillSwordBox.Value ?? 0);
            _currentEntry.SkillAxe    = (int)(SkillAxeBox.Value ?? 0);
            _currentEntry.SkillClub   = (int)(SkillClubBox.Value ?? 0);
            _currentEntry.SkillDist   = (int)(SkillDistBox.Value ?? 0);
            _currentEntry.SkillShield = (int)(SkillShieldBox.Value ?? 0);
            _currentEntry.SkillFish   = (int)(SkillFishBox.Value ?? 0);
            _currentEntry.MagicPoints = (int)(MagicPointsBox.Value ?? 0);

            _currentEntry.StopDuration    = StopDurationCb.IsChecked == true;
            _currentEntry.ShowCount       = ShowCountCb.IsChecked == true;
            _currentEntry.ShowAttributes  = ShowAttributesCb.IsChecked == true;
            _currentEntry.Readable        = ReadableCb.IsChecked == true;
            _currentEntry.Writeable       = WriteableCb.IsChecked == true;
            _currentEntry.AllowDistRead   = AllowDistReadCb.IsChecked == true;
            _currentEntry.ReplaceAble     = ReplaceAbleCb.IsChecked == true;
            _currentEntry.BlockProjectile = BlockProjectileCb.IsChecked == true;
            _currentEntry.MoveAble        = MoveAbleCb.IsChecked == true;
            _currentEntry.PickupAble      = PickupAbleCb.IsChecked == true;

            _currentEntry.CustomAttributes.Clear();
            foreach (var kv in _currentAttrs)
                if (!string.IsNullOrEmpty(kv.Key))
                    _currentEntry.CustomAttributes[kv.Key] = kv.Value;

            // Update header reflection
            HeaderIdText.Text = $"ID {_currentEntry.Id}";
            HeaderNameText.Text = string.IsNullOrEmpty(_currentEntry.Name) ? "(unnamed)" : _currentEntry.Name;
            HeaderTypeText.Text = BuildTypeHint(_currentEntry);

            RefreshList();
            StatusText.Text = $"Applied changes to entry {_currentEntry.Id}";
        }

        private void AddAttr_Click(object? sender, RoutedEventArgs e)
        {
            _currentAttrs.Add(new AttrKv { Key = "key", Value = "value" });
        }

        private void RemoveAttr_Click(object? sender, RoutedEventArgs e)
        {
            if (AttrsGrid.SelectedItem is AttrKv kv)
                _currentAttrs.Remove(kv);
        }
    }
}
