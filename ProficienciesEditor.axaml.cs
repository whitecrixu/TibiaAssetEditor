using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Newtonsoft.Json.Linq;
using Avalonia.Platform.Storage;

namespace Assets_Editor
{
    public partial class ProficienciesEditor : Window
    {
        // View-model wrapper over ProficiencyPerk. Exposes Select*Choice
        // props for ComboBox binding and Show* bools that collapse optional
        // fields the current Type doesn't consume.
        public class PerkVM : INotifyPropertyChanged
        {
            public ProficiencyPerk Model { get; }

            public IReadOnlyList<ProficiencyEnums.Choice> TypeChoices    => ProficiencyEnums.PerkTypes;
            public IReadOnlyList<ProficiencyEnums.Choice> SkillChoices   => _skillChoices;
            public IReadOnlyList<ProficiencyEnums.Choice> AugmentChoices => _augmentChoices;
            public IReadOnlyList<ProficiencyEnums.Choice> DamageChoices  => _damageChoices;

            private static readonly List<ProficiencyEnums.Choice> _skillChoices =
                new[] { new ProficiencyEnums.Choice(int.MinValue, "(unset)") }
                .Concat(ProficiencyEnums.Skills).ToList();

            private static readonly List<ProficiencyEnums.Choice> _augmentChoices =
                new[] { new ProficiencyEnums.Choice(int.MinValue, "(unset)") }
                .Concat(ProficiencyEnums.AugmentTypes).ToList();

            private static readonly List<ProficiencyEnums.Choice> _damageChoices =
                new[] { new ProficiencyEnums.Choice(int.MinValue, "(not set)") }
                .Concat(ProficiencyEnums.DamageTypes).ToList();

            public PerkVM(ProficiencyPerk model) { Model = model; }

            public ProficiencyEnums.Choice? SelectedTypeChoice
            {
                get => TypeChoices.FirstOrDefault(c => c.Value == Model.Type);
                set
                {
                    if (value == null || Model.Type == value.Value) return;
                    Model.Type = value.Value;
                    // Clear fields that the new Type doesn't consume — keeps
                    // the JSON clean and avoids stale junk like a SkillId on
                    // an Attack Damage perk.
                    var fields = ProficiencyEnums.FieldsFor(Model.Type);
                    if ((fields & ProficiencyEnums.PerkField.Skill)    == 0) Model.SkillId    = null;
                    if ((fields & ProficiencyEnums.PerkField.Augment)  == 0) Model.AugmentType = null;
                    if ((fields & ProficiencyEnums.PerkField.Spell)    == 0) Model.SpellId    = null;
                    if ((fields & ProficiencyEnums.PerkField.Bestiary) == 0) { Model.BestiaryId = null; Model.BestiaryName = null; }
                    if ((fields & ProficiencyEnums.PerkField.Damage)   == 0) Model.DamageType = null;
                    if ((fields & ProficiencyEnums.PerkField.Element)  == 0) Model.ElementId  = null;
                    if ((fields & ProficiencyEnums.PerkField.Missile)  == 0)
                        foreach (string key in MissileKeys) Model.Extra.Remove(key);
                    // the missile proc has no Value in the game data
                    Model.ValueMissing = Model.Type == ProficiencyEnums.MissileProcType;
                    if (Model.ValueMissing) Model.Value = 0;
                    if ((fields & ProficiencyEnums.PerkField.Range)    == 0) Model.Range      = null;
                    OnAll();
                }
            }

            public double Value
            {
                get => Model.Value;
                set { if (Math.Abs(Model.Value - value) > 1e-9) { Model.Value = value; On(nameof(Value)); } }
            }

            public string ValueHint => ProficiencyEnums.ValueHint(Model.Type);

            // Visibility flags driven by the Type → fields map.
            public bool ShowSkill    => (ProficiencyEnums.FieldsFor(Model.Type) & ProficiencyEnums.PerkField.Skill)    != 0;
            public bool ShowAugment  => (ProficiencyEnums.FieldsFor(Model.Type) & ProficiencyEnums.PerkField.Augment)  != 0;
            public bool ShowSpell    => (ProficiencyEnums.FieldsFor(Model.Type) & ProficiencyEnums.PerkField.Spell)    != 0;
            public bool ShowBestiary => (ProficiencyEnums.FieldsFor(Model.Type) & ProficiencyEnums.PerkField.Bestiary) != 0;
            public bool ShowDamage   => (ProficiencyEnums.FieldsFor(Model.Type) & ProficiencyEnums.PerkField.Damage)   != 0;
            public bool ShowElement  => (ProficiencyEnums.FieldsFor(Model.Type) & ProficiencyEnums.PerkField.Element)  != 0;
            public bool ShowMissile  => (ProficiencyEnums.FieldsFor(Model.Type) & ProficiencyEnums.PerkField.Missile)  != 0;
            public bool ShowRange    => (ProficiencyEnums.FieldsFor(Model.Type) & ProficiencyEnums.PerkField.Range)    != 0;
            public bool ShowAugmentOrSpell  => ShowAugment || ShowSpell;
            public bool ShowBestiaryOrRange => ShowBestiary || ShowRange;

            public ProficiencyEnums.Choice? SelectedSkillChoice
            {
                get => Model.SkillId.HasValue
                    ? SkillChoices.FirstOrDefault(c => c.Value == Model.SkillId.Value) ?? SkillChoices[0]
                    : SkillChoices[0];
                set
                {
                    if (value == null) return;
                    int? desired = value.Value == int.MinValue ? null : value.Value;
                    if (Model.SkillId != desired) { Model.SkillId = desired; On(nameof(SelectedSkillChoice)); }
                }
            }

            public ProficiencyEnums.Choice? SelectedAugmentChoice
            {
                get => Model.AugmentType.HasValue
                    ? AugmentChoices.FirstOrDefault(c => c.Value == Model.AugmentType.Value) ?? AugmentChoices[0]
                    : AugmentChoices[0];
                set
                {
                    if (value == null) return;
                    int? desired = value.Value == int.MinValue ? null : value.Value;
                    if (Model.AugmentType != desired) { Model.AugmentType = desired; On(nameof(SelectedAugmentChoice)); }
                }
            }

            public ProficiencyEnums.Choice? SelectedDamageChoice
            {
                get => Model.DamageType.HasValue
                    ? DamageChoices.FirstOrDefault(c => c.Value == Model.DamageType.Value) ?? DamageChoices[0]
                    : DamageChoices[0];
                set
                {
                    if (value == null) return;
                    int? desired = value.Value == int.MinValue ? null : value.Value;
                    if (Model.DamageType != desired) { Model.DamageType = desired; On(nameof(SelectedDamageChoice)); }
                }
            }

            public ProficiencyEnums.Choice? SelectedElementChoice
            {
                get => Model.ElementId.HasValue
                    ? DamageChoices.FirstOrDefault(c => c.Value == Model.ElementId.Value) ?? DamageChoices[0]
                    : DamageChoices[0];
                set
                {
                    if (value == null) return;
                    int? desired = value.Value == int.MinValue ? null : value.Value;
                    if (Model.ElementId != desired) { Model.ElementId = desired; On(nameof(SelectedElementChoice)); }
                }
            }

            // the missile proc fields stay in Extra, so untouched numbers keep their exact form
            private static readonly string[] MissileKeys = { "MissileId", "Multiplier", "Probability" };

            private double ExtraNumber(string key) =>
                Model.Extra.TryGetValue(key, out var t) && t.Type is JTokenType.Integer or JTokenType.Float ? t.Value<double>() : 0;

            private void SetExtra(string key, JToken value, string property)
            {
                Model.Extra[key] = value;
                On(property);
            }

            public decimal MissileIdValue
            {
                get => (decimal)ExtraNumber("MissileId");
                set { if ((double)value != ExtraNumber("MissileId")) SetExtra("MissileId", (int)value, nameof(MissileIdValue)); }
            }

            public double MultiplierValue
            {
                get => ExtraNumber("Multiplier");
                set { if (value != ExtraNumber("Multiplier")) SetExtra("Multiplier", value, nameof(MultiplierValue)); }
            }

            public double ProbabilityValue
            {
                get => ExtraNumber("Probability");
                set { if (value != ExtraNumber("Probability")) SetExtra("Probability", value, nameof(ProbabilityValue)); }
            }

            public string BestiaryNameText
            {
                get => Model.BestiaryName ?? "";
                set { Model.BestiaryName = string.IsNullOrWhiteSpace(value) ? null : value; On(nameof(BestiaryNameText)); }
            }

            public decimal SpellIdValue
            {
                get => (decimal)(Model.SpellId ?? 0);
                set { Model.SpellId = (int)value; On(nameof(SpellIdValue)); }
            }

            public decimal BestiaryIdValue
            {
                get => (decimal)(Model.BestiaryId ?? 0);
                set { Model.BestiaryId = (int)value; On(nameof(BestiaryIdValue)); }
            }

            public decimal RangeValue
            {
                get => (decimal)(Model.Range ?? 0);
                set { Model.Range = (int)value == 0 ? null : (int)value; On(nameof(RangeValue)); }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void On(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
            private void OnAll()
            {
                On(nameof(SelectedTypeChoice));
                On(nameof(ValueHint));
                On(nameof(ShowSkill));   On(nameof(ShowAugment)); On(nameof(ShowSpell));
                On(nameof(ShowBestiary)); On(nameof(ShowDamage)); On(nameof(ShowElement)); On(nameof(ShowMissile)); On(nameof(ShowRange));
                On(nameof(ShowAugmentOrSpell)); On(nameof(ShowBestiaryOrRange));
                On(nameof(SelectedSkillChoice));   On(nameof(SelectedAugmentChoice));
                On(nameof(SelectedDamageChoice));  On(nameof(SelectedElementChoice)); On(nameof(SpellIdValue));
                On(nameof(BestiaryNameText));
                On(nameof(MissileIdValue)); On(nameof(MultiplierValue)); On(nameof(ProbabilityValue));
                On(nameof(Value));
                On(nameof(BestiaryIdValue));       On(nameof(RangeValue));
            }
        }

        private ObservableCollection<ProficiencyEntry> _filtered = new();
        private ProficiencyEntry? _current;
        private ProficiencyLevel? _currentLevel;
        private int _currentLevelIdx = -1;
        private ObservableCollection<PerkVM> _perkVMs = new();
        private bool _suspend;

        public ProficienciesEditor()
        {
            InitializeComponent();
            PathText.Text = Proficiencies.LoadedPath ?? "";
            if (Proficiencies.IsLoaded) RefreshList();
        }

        private void RefreshList()
        {
            string? filter = SearchBox.Text?.Trim();
            IEnumerable<ProficiencyEntry> q = Proficiencies.Entries.OrderBy(e => e.ProficiencyId);
            if (!string.IsNullOrEmpty(filter))
            {
                q = q.Where(e =>
                    e.ProficiencyId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    (e.Name ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase));
            }
            _filtered = new ObservableCollection<ProficiencyEntry>(q);
            List.ItemsSource = _filtered;
        }

        // ── Toolbar ────────────────────────────────────────────────────────
        private async void Open_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open proficiencies.json",
                FileTypeFilter = [new FilePickerFileType("proficiencies.json") { Patterns = ["*.json"] }]
            });
            if (files.Count == 0) return;
            string path = files[0].Path.LocalPath;
            if (Proficiencies.Load(path))
            {
                PathText.Text = path;
                StatusText.Text = $"Loaded {Proficiencies.Entries.Count} proficiencies";
                if (Proficiencies.EngineWarning() is { } warning)
                {
                    StatusText.Text += " · " + warning;
                    MainWindow.Log(warning, "Warning");
                }
                MainWindow.AddRecentProficiencies(path);
                RefreshList();
            }
            else ErrorManager.ShowError("Failed to parse proficiencies.json");
        }

        private void Save_Click(object? sender, RoutedEventArgs e)
        {
            if (Proficiencies.Save()) StatusText.Text = $"Saved {Proficiencies.Entries.Count} proficiencies";
            else ErrorManager.ShowError("Save failed.");
        }

        private async void SaveAs_Click(object? sender, RoutedEventArgs e)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save proficiencies.json",
                DefaultExtension = "json",
                SuggestedFileName = "proficiencies.json",
                FileTypeChoices = [new FilePickerFileType("proficiencies.json") { Patterns = ["*.json"] }]
            });
            if (file == null) return;
            if (Proficiencies.Save(file.Path.LocalPath))
            {
                PathText.Text = file.Path.LocalPath;
                StatusText.Text = $"Saved to {file.Path.LocalPath}";
            }
            else ErrorManager.ShowError("Save failed.");
        }

        private uint NextFreeId() =>
            Proficiencies.Entries.Count > 0 ? Proficiencies.Entries.Max(x => x.ProficiencyId) + 1 : 1;

        private void AddNew_Click(object? sender, RoutedEventArgs e)
        {
            uint newId = NextFreeId();
            var entry = new ProficiencyEntry { Name = $"New Proficiency {newId}", ProficiencyId = newId, Version = 1 };
            entry.Levels.Add(new ProficiencyLevel());
            Proficiencies.Entries.Add(entry);
            RefreshList();
            List.SelectedItem = _filtered.FirstOrDefault(x => x.ProficiencyId == newId);
            StatusText.Text = $"Added proficiency {newId}";
        }

        private void NewFromTemplate_Click(object? sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();
            foreach (var t in ProficiencyTemplates.Templates)
            {
                var mi = new MenuItem { Header = t.Label };
                mi.Click += (_, _) =>
                {
                    uint newId = NextFreeId();
                    var entry = ProficiencyTemplates.Build(t, $"{t.HintNamePrefix} {newId}", newId);
                    Proficiencies.Entries.Add(entry);
                    RefreshList();
                    List.SelectedItem = _filtered.FirstOrDefault(x => x.ProficiencyId == newId);
                    StatusText.Text = $"Added {t.Label} proficiency {newId}";
                };
                flyout.Items.Add(mi);
            }
            flyout.ShowAt(NewFromTemplateBtn);
        }

        private void Duplicate_Click(object? sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            uint newId = NextFreeId();
            var copy = new ProficiencyEntry
            {
                Name = _current.Name + " (copy)",
                ProficiencyId = newId,
                Version = _current.Version,
            };
            foreach (var lvl in _current.Levels)
            {
                var lcopy = new ProficiencyLevel();
                foreach (var perk in lvl.Perks) lcopy.Perks.Add(perk.Clone());
                copy.Levels.Add(lcopy);
            }
            Proficiencies.Entries.Add(copy);
            RefreshList();
            List.SelectedItem = _filtered.FirstOrDefault(x => x.ProficiencyId == newId);
            StatusText.Text = $"Duplicated as {newId}";
        }

        private void RemoveEnginePerks_Click(object? sender, RoutedEventArgs e)
        {
            int removed = Proficiencies.RemoveEnginePerks();
            if (removed == 0)
            {
                StatusText.Text = "No perks Crystal Server / Canary cannot load";
                return;
            }
            StatusText.Text = $"Removed {removed} perks Crystal Server / Canary cannot load; Save to write the file";
            MainWindow.Log(StatusText.Text);
            var current = _current;
            RefreshList();
            List.SelectedItem = current;
        }

        private void Delete_Click(object? sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            Proficiencies.Entries.Remove(_current);
            StatusText.Text = $"Deleted proficiency {_current.ProficiencyId}";
            _current = null;
            RefreshList();
            ClearDetail();
        }

        private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => RefreshList();

        // ── Selection ─────────────────────────────────────────────────────
        private void List_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            _current = List.SelectedItem as ProficiencyEntry;
            if (_current == null) { ClearDetail(); return; }

            _suspend = true;
            HeaderText.Text = _current.Display;
            HeaderHint.Text = $"{_current.Levels.Count} level(s)";
            NameBox.Text = _current.Name;
            IdBox.Value = _current.ProficiencyId;
            VersionBox.Value = _current.Version;
            _suspend = false;

            RefreshLevelPills();
            SelectLevel(_current.Levels.Count > 0 ? 0 : -1);
        }

        private void ClearDetail()
        {
            _suspend = true;
            HeaderText.Text = "No selection";
            HeaderHint.Text = "Pick a proficiency from the list, or click ➕ New Blank / 🛠 Template.";
            NameBox.Text = "";
            IdBox.Value = 0;
            VersionBox.Value = 0;
            LevelPills.ItemsSource = null;
            _suspend = false;
            ClearPerks();
        }

        private void ClearPerks()
        {
            _currentLevel = null;
            _currentLevelIdx = -1;
            _perkVMs = new();
            PerksItems.ItemsSource = _perkVMs;
            PerksHeader.Text = "Perks";
        }

        // Renders the horizontal level-pills bar. We build Button controls
        // manually so we can give the selected pill a different style and
        // wire each one to its own index without a value converter.
        private void RefreshLevelPills()
        {
            if (_current == null) { LevelPills.ItemsSource = null; return; }
            var buttons = new List<Button>();
            for (int i = 0; i < _current.Levels.Count; i++)
            {
                int idx = i;
                var btn = new Button
                {
                    Content = $"L{i + 1}",
                    Classes = { "levelPill" },
                    Margin = new Thickness(2, 0),
                    Tag = idx,
                };
                if (i == _currentLevelIdx) btn.Classes.Add("selected");
                btn.Click += (_, _) => SelectLevel(idx);
                buttons.Add(btn);
            }
            LevelPills.ItemsSource = buttons;
        }

        private void SelectLevel(int idx)
        {
            if (_current == null || idx < 0 || idx >= _current.Levels.Count) { ClearPerks(); return; }
            _currentLevelIdx = idx;
            _currentLevel = _current.Levels[idx];
            PerksHeader.Text = $"Level {idx + 1} — Perks";
            _perkVMs = new ObservableCollection<PerkVM>(_currentLevel.Perks.Select(p => new PerkVM(p)));
            PerksItems.ItemsSource = _perkVMs;
            RefreshLevelPills();
        }

        // ── Metadata edits ────────────────────────────────────────────────
        private void NameBox_Changed(object? sender, TextChangedEventArgs e)
        {
            if (_suspend || _current == null) return;
            _current.Name = NameBox.Text ?? "";
            HeaderText.Text = _current.Display;
        }

        private void IdBox_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
        {
            if (_suspend || _current == null) return;
            _current.ProficiencyId = (uint)(IdBox.Value ?? 0);
            HeaderText.Text = _current.Display;
        }

        private void VersionBox_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
        {
            if (_suspend || _current == null) return;
            _current.Version = (int)(VersionBox.Value ?? 0);
        }

        // ── Level ops ─────────────────────────────────────────────────────
        private void AddLevel_Click(object? sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            _current.Levels.Add(new ProficiencyLevel());
            HeaderHint.Text = $"{_current.Levels.Count} level(s)";
            SelectLevel(_current.Levels.Count - 1);
        }

        private void DupLevel_Click(object? sender, RoutedEventArgs e)
        {
            if (_current == null || _currentLevel == null) return;
            int idx = _currentLevelIdx;
            var copy = new ProficiencyLevel();
            foreach (var p in _currentLevel.Perks) copy.Perks.Add(p.Clone());
            _current.Levels.Insert(idx + 1, copy);
            HeaderHint.Text = $"{_current.Levels.Count} level(s)";
            SelectLevel(idx + 1);
        }

        private void DeleteLevel_Click(object? sender, RoutedEventArgs e)
        {
            if (_current == null || _currentLevel == null) return;
            int idx = _currentLevelIdx;
            _current.Levels.RemoveAt(idx);
            HeaderHint.Text = $"{_current.Levels.Count} level(s)";
            if (_current.Levels.Count == 0) { RefreshLevelPills(); ClearPerks(); }
            else SelectLevel(Math.Clamp(idx, 0, _current.Levels.Count - 1));
        }

        private void MoveLevelUp_Click(object? sender, RoutedEventArgs e)
        {
            if (_current == null || _currentLevel == null || _currentLevelIdx <= 0) return;
            int idx = _currentLevelIdx;
            _current.Levels.Move(idx, idx - 1);
            SelectLevel(idx - 1);
        }

        private void MoveLevelDown_Click(object? sender, RoutedEventArgs e)
        {
            if (_current == null || _currentLevel == null) return;
            int idx = _currentLevelIdx;
            if (idx < 0 || idx >= _current.Levels.Count - 1) return;
            _current.Levels.Move(idx, idx + 1);
            SelectLevel(idx + 1);
        }

        // ── Perk ops ──────────────────────────────────────────────────────
        private void AddPerk_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentLevel == null) return;
            var perk = new ProficiencyPerk { Type = 0, Value = 0 };
            _currentLevel.Perks.Add(perk);
            _perkVMs.Add(new PerkVM(perk));
        }

        private void InsertPreset_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentLevel == null)
            {
                ErrorManager.ShowInfo("Select a level first.");
                return;
            }

            // Categorize presets by leading word so the menu doesn't
            // become a 40-item wall of text.
            var flyout = new MenuFlyout();
            var byCategory = new Dictionary<string, MenuItem>();
            foreach (var preset in PerkLibrary.Presets)
            {
                string category = GuessCategory(preset);
                if (!byCategory.TryGetValue(category, out var parent))
                {
                    parent = new MenuItem { Header = category };
                    byCategory[category] = parent;
                    flyout.Items.Add(parent);
                }
                var mi = new MenuItem { Header = preset.Label };
                mi.Click += (_, _) => InsertPreset(preset);
                parent.Items.Add(mi);
            }
            flyout.ShowAt(InsertPresetBtn);
        }

        private static string GuessCategory(PerkPreset p)
        {
            if (p.Label.StartsWith("Augment")) return "Augment";
            if (p.Label.StartsWith("Bestiary") || p.Label.Contains("Element")) return "Bestiary / Element";
            if (p.Label.Contains("Skill +")) return "Skill +1";
            if (p.Label.Contains("Crit")) return "Crit";
            if (p.Label.Contains("Leech") || p.Label.Contains("Gain")) return "Leech / Gain";
            if (p.Label.Contains("Range")) return "Range";
            return "Other";
        }

        private void InsertPreset(PerkPreset preset)
        {
            if (_currentLevel == null) return;
            var perk = preset.ToPerk();
            _currentLevel.Perks.Add(perk);
            _perkVMs.Add(new PerkVM(perk));
            StatusText.Text = $"Inserted: {preset.Label}";
        }

        private void DeletePerk_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentLevel == null || sender is not Button b || b.Tag is not PerkVM vm) return;
            _currentLevel.Perks.Remove(vm.Model);
            _perkVMs.Remove(vm);
        }

        private void MovePerkUp_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentLevel == null || sender is not Button b || b.Tag is not PerkVM vm) return;
            int idx = _currentLevel.Perks.IndexOf(vm.Model);
            if (idx <= 0) return;
            _currentLevel.Perks.Move(idx, idx - 1);
            _perkVMs.Move(idx, idx - 1);
        }

        private void MovePerkDown_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentLevel == null || sender is not Button b || b.Tag is not PerkVM vm) return;
            int idx = _currentLevel.Perks.IndexOf(vm.Model);
            if (idx < 0 || idx >= _currentLevel.Perks.Count - 1) return;
            _currentLevel.Perks.Move(idx, idx + 1);
            _perkVMs.Move(idx, idx + 1);
        }
    }
}
