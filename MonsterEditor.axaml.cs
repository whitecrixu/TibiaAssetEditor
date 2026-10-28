using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Tibia.Protobuf.Appearances;

namespace Assets_Editor
{
    public partial class MonsterEditor : Window
    {
        private ObservableCollection<LuaMonsterData> _filtered = new();
        private LuaMonsterData? _current;
        private bool _suspend;

        // Outfit (lookType) → Appearance and Object (lookTypeEx) → Appearance
        // lookups rebuilt from MainWindow.appearances, so we can resolve a
        // monster's sprite without scanning the protobuf every time.
        private Dictionary<uint, Appearance> _outfitById = new();
        private Dictionary<uint, Appearance> _itemById = new();

        public MonsterEditor()
        {
            InitializeComponent();
            PathText.Text = MonsterRegistry.LoadedFolder ?? "";
            BuildAppearanceLookups();
            if (MonsterRegistry.IsLoaded) RefreshList();
        }

        private void BuildAppearanceLookups()
        {
            _outfitById.Clear();
            _itemById.Clear();
            if (MainWindow.appearances == null) return;
            foreach (var a in MainWindow.appearances.Outfit) _outfitById[a.Id] = a;
            foreach (var a in MainWindow.appearances.Object) _itemById[a.Id] = a;
        }

        // ── Thumbnails ────────────────────────────────────────────────────
        private void MonsterList_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        {
            if (e.Container.DataContext is LuaMonsterData m) LoadImageFor(m);
        }

        private void MonsterList_ContainerClearing(object? sender, ContainerClearingEventArgs e)
        {
            if (e.Container.DataContext is LuaMonsterData m) m.Image = null;
        }

        private void LoadImageFor(LuaMonsterData m)
        {
            if (m.Image != null) return;
            try
            {
                Appearance? a = null;
                // Creature outfit first; fall back to lookTypeEx (item-based
                // monsters like decoration "mimic" style mobs) if lookType is 0.
                if (m.LookType > 0) _outfitById.TryGetValue(m.LookType, out a);
                if (a == null && m.LookTypeEx > 0) _itemById.TryGetValue(m.LookTypeEx, out a);
                if (a == null || a.FrameGroup.Count == 0) return;

                var info = a.FrameGroup[0].SpriteInfo;
                if (info.SpriteId.Count == 0) return;
                var stream = MainWindow.getSpriteStream((int)info.SpriteId[0]);
                if (stream != null) m.Image = Utils.MemoryStreamToBitmap(stream);
            }
            catch { /* best-effort */ }
        }

        private void RefreshList()
        {
            string? filter = SearchBox.Text?.Trim();
            IEnumerable<LuaMonsterData> q = MonsterRegistry.Monsters.OrderBy(m => m.Name);
            if (!string.IsNullOrEmpty(filter))
            {
                q = q.Where(m =>
                    m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    m.RaceId.ToString().Contains(filter) ||
                    (m.BestiaryClass ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase));
            }
            _filtered = new ObservableCollection<LuaMonsterData>(q);
            MonsterList.ItemsSource = _filtered;
        }

        private async void LoadFolder_Click(object? sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Pick the monster/ folder",
            });
            if (folders.Count == 0) return;
            string path = folders[0].Path.LocalPath;
            LoadFolder(path);
        }

        public void LoadFolder(string path)
        {
            int count = MonsterRegistry.LoadFolder(path);
            PathText.Text = path;
            StatusText.Text = $"Loaded {count} monsters from {path}";
            MainWindow.AddRecentMonsterFolder(path);
            RefreshList();
        }

        private void SaveCurrent_Click(object? sender, RoutedEventArgs e)
        {
            if (_current == null) { ErrorManager.ShowInfo("No monster selected."); return; }
            if (_current.ReadOnly)
            {
                ErrorManager.ShowWarning($"{_current.Name} uses the revscript format (mType:...) of this server. The editor can only read it - saving would overwrite its loot, attacks and defenses. Edit the .lua file directly.");
                return;
            }
            if (string.IsNullOrEmpty(_current.SourcePath))
            {
                // Newly-added monster — drop it into the loaded folder under a
                // filename derived from the name.
                if (string.IsNullOrEmpty(MonsterRegistry.LoadedFolder))
                {
                    ErrorManager.ShowError("Load a monster folder first (so we know where to save).");
                    return;
                }
                string fileName = _current.Name.ToLower().Replace(' ', '_') + ".lua";
                _current.SourcePath = Path.Combine(MonsterRegistry.LoadedFolder, fileName);
            }

            if (LuaMonsterGenerator.SaveToFile(_current))
                StatusText.Text = $"Saved {_current.Name} → {_current.SourcePath}  |  reload the server scripts to apply";
            else ErrorManager.ShowError("Save failed.");
        }

        private void SaveAll_Click(object? sender, RoutedEventArgs e)
        {
            int ok = 0, fail = 0, readOnly = 0;
            foreach (var m in MonsterRegistry.Monsters)
            {
                if (string.IsNullOrEmpty(m.SourcePath))
                {
                    // Skip new unsaved monsters — user should Save Current first.
                    continue;
                }
                if (m.ReadOnly) { readOnly++; continue; }
                if (LuaMonsterGenerator.SaveToFile(m)) ok++; else fail++;
            }
            StatusText.Text = (fail == 0 ? $"Saved {ok} monsters" : $"Saved {ok}, failed {fail}")
                + (readOnly > 0 ? $", skipped {readOnly} read-only (mType:) monsters" : "");
        }

        private void AddNew_Click(object? sender, RoutedEventArgs e)
        {
            uint raceId = MonsterRegistry.NextFreeRaceId();
            var m = new LuaMonsterData
            {
                Name = $"New Monster {raceId}",
                RaceId = raceId,
                Description = $"a new monster {raceId}",
                LookType = 1,
            };
            // Default to Bestiary (typical monsters). Boss toggle flips this.
            LuaMonsterParser.AutoFillBestiary(m);
            MonsterRegistry.Monsters.Add(m);
            RefreshList();
            MonsterList.SelectedItem = _filtered.FirstOrDefault(x => x.RaceId == raceId);
            StatusText.Text = $"Added {m.Name}";
        }

        private void Duplicate_Click(object? sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            uint raceId = MonsterRegistry.NextFreeRaceId();
            var copy = new LuaMonsterData
            {
                Name = _current.Name + " Copy",
                Description = _current.Description,
                RaceId = raceId,
                Health = _current.Health, MaxHealth = _current.MaxHealth,
                Experience = _current.Experience, Speed = _current.Speed,
                Corpse = _current.Corpse, Race = _current.Race, ManaCost = _current.ManaCost,
                LookType = _current.LookType, LookTypeEx = _current.LookTypeEx,
                LookHead = _current.LookHead, LookBody = _current.LookBody,
                LookLegs = _current.LookLegs, LookFeet = _current.LookFeet,
                LookAddons = _current.LookAddons, LookMount = _current.LookMount,
                Summonable = _current.Summonable, Attackable = _current.Attackable,
                Hostile = _current.Hostile, Convinceable = _current.Convinceable,
                Pushable = _current.Pushable, RewardBoss = _current.RewardBoss,
                Illusionable = _current.Illusionable, CanPushItems = _current.CanPushItems,
                CanPushCreatures = _current.CanPushCreatures,
                StaticAttackChance = _current.StaticAttackChance,
                TargetDistance = _current.TargetDistance, RunHealth = _current.RunHealth,
                HealthHidden = _current.HealthHidden,
                CanWalkOnEnergy = _current.CanWalkOnEnergy,
                CanWalkOnFire = _current.CanWalkOnFire,
                CanWalkOnPoison = _current.CanWalkOnPoison,
                HasBestiary = _current.HasBestiary,
                BestiaryClass = _current.BestiaryClass, BestiaryRace = _current.BestiaryRace,
                BestiaryToKill = _current.BestiaryToKill,
                BestiaryFirstUnlock = _current.BestiaryFirstUnlock,
                BestiarySecondUnlock = _current.BestiarySecondUnlock,
                BestiaryCharmsPoints = _current.BestiaryCharmsPoints,
                BestiaryStars = _current.BestiaryStars,
                BestiaryOccurrence = _current.BestiaryOccurrence,
                BestiaryLocations = _current.BestiaryLocations,
                HasBosstiary = _current.HasBosstiary,
                BosstiaryRaceId = _current.BosstiaryRaceId,
                BosstiaryRace = _current.BosstiaryRace,
                RawLoot = _current.RawLoot, RawAttacks = _current.RawAttacks,
                RawDefenses = _current.RawDefenses, RawElements = _current.RawElements,
                RawImmunities = _current.RawImmunities, RawVoices = _current.RawVoices,
                RawEvents = _current.RawEvents, RawChangeTarget = _current.RawChangeTarget,
                RawStrategiesTarget = _current.RawStrategiesTarget,
                RawSummon = _current.RawSummon, RawLight = _current.RawLight,
            };
            MonsterRegistry.Monsters.Add(copy);
            RefreshList();
            MonsterList.SelectedItem = _filtered.FirstOrDefault(x => x.RaceId == raceId);
            StatusText.Text = $"Duplicated as {copy.Name}";
        }

        private void Delete_Click(object? sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            MonsterRegistry.Monsters.Remove(_current);
            StatusText.Text = $"Deleted {_current.Name} (file on disk NOT removed)";
            _current = null;
            RefreshList();
            ClearDetail();
        }

        private void AutoFill_Click(object? sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            if (_current.RewardBoss)
            {
                LuaMonsterParser.AutoFillBosstiary(_current, MonsterRegistry.NextFreeBossRaceId());
                StatusText.Text = "Bosstiary auto-filled.";
            }
            else
            {
                LuaMonsterParser.AutoFillBestiary(_current);
                StatusText.Text = "Bestiary auto-filled.";
            }
            PopulateDetail(_current);
        }

        private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => RefreshList();

        private void MonsterList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            _current = MonsterList.SelectedItem as LuaMonsterData;
            if (_current == null) { ClearDetail(); return; }
            PopulateDetail(_current);
        }

        private void ClearDetail()
        {
            _suspend = true;
            HeaderText.Text = "No selection";
            HeaderHint.Text = "Pick a monster, or ➕ create a new one.";
            HeaderImage.Source = null;
            NameBox.Text = ""; DescriptionBox.Text = "";
            RaceIdBox.Value = 0; HealthBox.Value = 0; MaxHealthBox.Value = 0;
            ExperienceBox.Value = 0; SpeedBox.Value = 0; CorpseBox.Value = 0;
            ManaCostBox.Value = 0; RaceBox.SelectedIndex = 0;
            LookTypeBox.Value = 0; LookTypeExBox.Value = 0;
            LookHeadBox.Value = 0; LookBodyBox.Value = 0;
            LookLegsBox.Value = 0; LookFeetBox.Value = 0;
            LookAddonsBox.Value = 0; LookMountBox.Value = 0;
            SummonableCb.IsChecked = false; AttackableCb.IsChecked = true;
            HostileCb.IsChecked = true; ConvinceableCb.IsChecked = false;
            PushableCb.IsChecked = false; RewardBossCb.IsChecked = false;
            IllusionableCb.IsChecked = false; CanPushItemsCb.IsChecked = false;
            CanPushCreaturesCb.IsChecked = false; HealthHiddenCb.IsChecked = false;
            CanWalkOnEnergyCb.IsChecked = false; CanWalkOnFireCb.IsChecked = false;
            CanWalkOnPoisonCb.IsChecked = false;
            StaticAttackBox.Value = 90; TargetDistanceBox.Value = 1; RunHealthBox.Value = 0;
            HasBestiaryCb.IsChecked = false;
            BestiaryClassBox.Text = ""; BestiaryRaceBox.Text = "";
            BestiaryToKillBox.Value = 0; BestiaryStarsBox.Value = 0;
            BestiaryFirstBox.Value = 0; BestiarySecondBox.Value = 0;
            BestiaryCharmsBox.Value = 0; BestiaryOccurrenceBox.Value = 0;
            BestiaryLocationsBox.Text = "";
            HasBosstiaryCb.IsChecked = false; BossRaceIdBox.Value = 0;
            BossRarityBox.SelectedIndex = 0;
            RawLootBox.Text = ""; RawAttacksBox.Text = ""; RawDefensesBox.Text = "";
            RawElementsBox.Text = ""; RawImmunitiesBox.Text = ""; RawVoicesBox.Text = "";
            RawLightBox.Text = ""; RawEventsBox.Text = "";
            RawChangeTargetBox.Text = ""; RawStrategiesBox.Text = ""; RawSummonBox.Text = "";
            PreviewBox.Text = "";
            _suspend = false;
        }

        private void PopulateDetail(LuaMonsterData d)
        {
            _suspend = true;
            HeaderText.Text = d.Display;
            HeaderHint.Text = string.IsNullOrEmpty(d.SourcePath) ? "(new, not saved yet)" : d.SourcePath;
            LoadImageFor(d);
            HeaderImage.Source = d.Image;

            NameBox.Text = d.Name;
            DescriptionBox.Text = d.Description ?? "";
            RaceIdBox.Value = d.RaceId;
            HealthBox.Value = d.Health; MaxHealthBox.Value = d.MaxHealth;
            ExperienceBox.Value = d.Experience; SpeedBox.Value = d.Speed;
            CorpseBox.Value = d.Corpse; ManaCostBox.Value = d.ManaCost;
            RaceBox.SelectedIndex = RaceOptionIndex(d.Race);

            LookTypeBox.Value = d.LookType; LookTypeExBox.Value = d.LookTypeEx;
            LookHeadBox.Value = d.LookHead; LookBodyBox.Value = d.LookBody;
            LookLegsBox.Value = d.LookLegs; LookFeetBox.Value = d.LookFeet;
            LookAddonsBox.Value = d.LookAddons; LookMountBox.Value = d.LookMount;

            SummonableCb.IsChecked = d.Summonable;
            AttackableCb.IsChecked = d.Attackable;
            HostileCb.IsChecked = d.Hostile;
            ConvinceableCb.IsChecked = d.Convinceable;
            PushableCb.IsChecked = d.Pushable;
            RewardBossCb.IsChecked = d.RewardBoss;
            IllusionableCb.IsChecked = d.Illusionable;
            CanPushItemsCb.IsChecked = d.CanPushItems;
            CanPushCreaturesCb.IsChecked = d.CanPushCreatures;
            HealthHiddenCb.IsChecked = d.HealthHidden;
            CanWalkOnEnergyCb.IsChecked = d.CanWalkOnEnergy;
            CanWalkOnFireCb.IsChecked = d.CanWalkOnFire;
            CanWalkOnPoisonCb.IsChecked = d.CanWalkOnPoison;
            StaticAttackBox.Value = d.StaticAttackChance;
            TargetDistanceBox.Value = d.TargetDistance;
            RunHealthBox.Value = d.RunHealth;

            HasBestiaryCb.IsChecked = d.HasBestiary;
            BestiaryClassBox.Text = d.BestiaryClass ?? "";
            BestiaryRaceBox.Text = d.BestiaryRace ?? "";
            BestiaryToKillBox.Value = d.BestiaryToKill;
            BestiaryStarsBox.Value = d.BestiaryStars;
            BestiaryFirstBox.Value = d.BestiaryFirstUnlock;
            BestiarySecondBox.Value = d.BestiarySecondUnlock;
            BestiaryCharmsBox.Value = d.BestiaryCharmsPoints;
            BestiaryOccurrenceBox.Value = d.BestiaryOccurrence;
            BestiaryLocationsBox.Text = d.BestiaryLocations ?? "";

            HasBosstiaryCb.IsChecked = d.HasBosstiary;
            BossRaceIdBox.Value = d.BosstiaryRaceId;
            BossRarityBox.SelectedIndex = d.BosstiaryRace switch
            {
                "RARITY_ARCHFOE" => 1,
                "RARITY_NEMESIS" => 2,
                _ => 0,
            };

            RawLootBox.Text = d.RawLoot ?? "";
            RawAttacksBox.Text = d.RawAttacks ?? "";
            RawDefensesBox.Text = d.RawDefenses ?? "";
            RawElementsBox.Text = d.RawElements ?? "";
            RawImmunitiesBox.Text = d.RawImmunities ?? "";
            RawVoicesBox.Text = d.RawVoices ?? "";
            RawLightBox.Text = d.RawLight ?? "";
            RawEventsBox.Text = d.RawEvents ?? "";
            RawChangeTargetBox.Text = d.RawChangeTarget ?? "";
            RawStrategiesBox.Text = d.RawStrategiesTarget ?? "";
            RawSummonBox.Text = d.RawSummon ?? "";

            _suspend = false;
            UpdatePreview();
        }

        // Avalonia XAML requires the handler signature to match the event's
        // delegate type exactly, so route every change into one ApplyFields
        // body via typed wrappers.
        private void Field_Text(object? sender, TextChangedEventArgs e) => ApplyFields();
        private void Field_Num(object? sender, NumericUpDownValueChangedEventArgs e) => ApplyFields();
        private void Field_Bool(object? sender, RoutedEventArgs e) => ApplyFields();
        private void Field_Sel(object? sender, SelectionChangedEventArgs e) => ApplyFields();

        private void ApplyFields()
        {
            if (_suspend || _current == null) return;
            _current.Name = NameBox.Text ?? "";
            _current.Description = DescriptionBox.Text;
            _current.RaceId = (uint)(RaceIdBox.Value ?? 0);
            _current.Health = (uint)(HealthBox.Value ?? 0);
            _current.MaxHealth = (uint)(MaxHealthBox.Value ?? 0);
            _current.Experience = (uint)(ExperienceBox.Value ?? 0);
            _current.Speed = (uint)(SpeedBox.Value ?? 0);
            _current.Corpse = (uint)(CorpseBox.Value ?? 0);
            _current.ManaCost = (uint)(ManaCostBox.Value ?? 0);
            if (RaceBox.SelectedItem is ComboBoxItem cbi) _current.Race = cbi.Content?.ToString() ?? "blood";

            uint prevLookType = _current.LookType, prevLookEx = _current.LookTypeEx;
            _current.LookType = (uint)(LookTypeBox.Value ?? 0);
            _current.LookTypeEx = (uint)(LookTypeExBox.Value ?? 0);
            // If the outfit ID changed, drop the cached image so the next
            // PopulateDetail / list-render picks up the new sprite.
            if (prevLookType != _current.LookType || prevLookEx != _current.LookTypeEx)
            {
                _current.Image = null;
                LoadImageFor(_current);
                HeaderImage.Source = _current.Image;
            }
            _current.LookHead = (uint)(LookHeadBox.Value ?? 0);
            _current.LookBody = (uint)(LookBodyBox.Value ?? 0);
            _current.LookLegs = (uint)(LookLegsBox.Value ?? 0);
            _current.LookFeet = (uint)(LookFeetBox.Value ?? 0);
            _current.LookAddons = (uint)(LookAddonsBox.Value ?? 0);
            _current.LookMount = (uint)(LookMountBox.Value ?? 0);

            _current.Summonable = SummonableCb.IsChecked == true;
            _current.Attackable = AttackableCb.IsChecked == true;
            _current.Hostile = HostileCb.IsChecked == true;
            _current.Convinceable = ConvinceableCb.IsChecked == true;
            _current.Pushable = PushableCb.IsChecked == true;
            _current.RewardBoss = RewardBossCb.IsChecked == true;
            _current.Illusionable = IllusionableCb.IsChecked == true;
            _current.CanPushItems = CanPushItemsCb.IsChecked == true;
            _current.CanPushCreatures = CanPushCreaturesCb.IsChecked == true;
            _current.HealthHidden = HealthHiddenCb.IsChecked == true;
            _current.CanWalkOnEnergy = CanWalkOnEnergyCb.IsChecked == true;
            _current.CanWalkOnFire = CanWalkOnFireCb.IsChecked == true;
            _current.CanWalkOnPoison = CanWalkOnPoisonCb.IsChecked == true;
            _current.StaticAttackChance = (uint)(StaticAttackBox.Value ?? 0);
            _current.TargetDistance = (uint)(TargetDistanceBox.Value ?? 0);
            _current.RunHealth = (uint)(RunHealthBox.Value ?? 0);

            _current.HasBestiary = HasBestiaryCb.IsChecked == true;
            _current.BestiaryClass = BestiaryClassBox.Text;
            _current.BestiaryRace = BestiaryRaceBox.Text;
            _current.BestiaryToKill = (uint)(BestiaryToKillBox.Value ?? 0);
            _current.BestiaryStars = (uint)(BestiaryStarsBox.Value ?? 0);
            _current.BestiaryFirstUnlock = (uint)(BestiaryFirstBox.Value ?? 0);
            _current.BestiarySecondUnlock = (uint)(BestiarySecondBox.Value ?? 0);
            _current.BestiaryCharmsPoints = (uint)(BestiaryCharmsBox.Value ?? 0);
            _current.BestiaryOccurrence = (uint)(BestiaryOccurrenceBox.Value ?? 0);
            _current.BestiaryLocations = BestiaryLocationsBox.Text;

            _current.HasBosstiary = HasBosstiaryCb.IsChecked == true;
            _current.BosstiaryRaceId = (uint)(BossRaceIdBox.Value ?? 0);
            if (BossRarityBox.SelectedItem is ComboBoxItem brCbi)
                _current.BosstiaryRace = brCbi.Content?.ToString();

            _current.RawLoot = NullIfEmpty(RawLootBox.Text);
            _current.RawAttacks = NullIfEmpty(RawAttacksBox.Text);
            _current.RawDefenses = NullIfEmpty(RawDefensesBox.Text);
            _current.RawElements = NullIfEmpty(RawElementsBox.Text);
            _current.RawImmunities = NullIfEmpty(RawImmunitiesBox.Text);
            _current.RawVoices = NullIfEmpty(RawVoicesBox.Text);
            _current.RawLight = NullIfEmpty(RawLightBox.Text);
            _current.RawEvents = NullIfEmpty(RawEventsBox.Text);
            _current.RawChangeTarget = NullIfEmpty(RawChangeTargetBox.Text);
            _current.RawStrategiesTarget = NullIfEmpty(RawStrategiesBox.Text);
            _current.RawSummon = NullIfEmpty(RawSummonBox.Text);

            HeaderText.Text = _current.Display;
        }

        private void RefreshPreview_Click(object? sender, RoutedEventArgs e) => UpdatePreview();

        private void UpdatePreview()
        {
            if (_current == null) { PreviewBox.Text = ""; return; }
            try { PreviewBox.Text = LuaMonsterGenerator.GenerateFullLua(_current); }
            catch (Exception ex) { PreviewBox.Text = $"-- preview failed: {ex.Message}"; }
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static int RaceOptionIndex(string? race) => race switch
        {
            "venom"   => 1,
            "undead"  => 2,
            "fire"    => 3,
            "energy"  => 4,
            "ink"     => 5,
            _         => 0,
        };
    }
}
