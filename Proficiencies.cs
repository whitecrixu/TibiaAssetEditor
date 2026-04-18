using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Assets_Editor
{
    // Mirrors data/JSON/proficiencies.json (jataria canary server).
    // Each perk has a required Type + Value and a shifting set of optional
    // fields depending on the perk type — we model them all as nullable ints,
    // and only emit the ones that are set on save.
    public class ProficiencyPerk : INotifyPropertyChanged
    {
        private int _type;
        private double _value;
        private int? _skillId;
        private int? _augmentType;
        private int? _spellId;
        private int? _bestiaryId;
        private int? _damageType;
        private int? _elementId;
        private int? _range;

        public int     Type        { get => _type;        set { _type = value;        On(nameof(Type)); On(nameof(TypeLabel)); } }
        public double  Value       { get => _value;       set { _value = value;       On(nameof(Value)); } }
        public int?    SkillId     { get => _skillId;     set { _skillId = value;     On(nameof(SkillId)); } }
        public int?    AugmentType { get => _augmentType; set { _augmentType = value; On(nameof(AugmentType)); } }
        public int?    SpellId     { get => _spellId;     set { _spellId = value;     On(nameof(SpellId)); } }
        public int?    BestiaryId  { get => _bestiaryId;  set { _bestiaryId = value;  On(nameof(BestiaryId)); } }
        public int?    DamageType  { get => _damageType;  set { _damageType = value;  On(nameof(DamageType)); } }
        public int?    ElementId   { get => _elementId;   set { _elementId = value;   On(nameof(ElementId)); } }
        public int?    Range       { get => _range;       set { _range = value;       On(nameof(Range)); } }

        // Preserve any unknown keys on round-trip.
        public Dictionary<string, JToken> Extra { get; set; } = new();

        public string TypeLabel => ProficiencyEnums.PerkTypeName(_type);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void On(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public ProficiencyPerk Clone() => new()
        {
            Type = Type, Value = Value, SkillId = SkillId, AugmentType = AugmentType,
            SpellId = SpellId, BestiaryId = BestiaryId, DamageType = DamageType,
            ElementId = ElementId, Range = Range,
            Extra = new Dictionary<string, JToken>(Extra),
        };
    }

    public class ProficiencyLevel
    {
        public System.Collections.ObjectModel.ObservableCollection<ProficiencyPerk> Perks { get; } = new();
    }

    public class ProficiencyEntry : INotifyPropertyChanged
    {
        private string _name = "";
        private uint _id;
        private int _version = 1;

        public string Name { get => _name; set { _name = value; On(nameof(Name)); On(nameof(Display)); } }
        public uint ProficiencyId { get => _id; set { _id = value; On(nameof(ProficiencyId)); On(nameof(Display)); } }
        public int Version { get => _version; set { _version = value; On(nameof(Version)); } }
        public System.Collections.ObjectModel.ObservableCollection<ProficiencyLevel> Levels { get; } = new();

        // Preserve any unknown top-level keys on round-trip.
        public Dictionary<string, JToken> Extra { get; set; } = new();

        public string Display => $"{ProficiencyId}: {(string.IsNullOrEmpty(Name) ? "(unnamed)" : Name)}";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void On(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public static class Proficiencies
    {
        public static List<ProficiencyEntry> Entries { get; private set; } = new();
        public static string? LoadedPath { get; private set; }
        public static bool IsLoaded => Entries.Count > 0;

        public static bool Load(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                string raw = File.ReadAllText(path);
                var array = JArray.Parse(raw);
                var list = new List<ProficiencyEntry>(array.Count);
                foreach (var entryTok in array.OfType<JObject>())
                    list.Add(ReadEntry(entryTok));

                Entries = list;
                LoadedPath = path;
                return true;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"Failed to load proficiencies.json: {ex.Message}");
                return false;
            }
        }

        public static bool Save(string? path = null)
        {
            path ??= LoadedPath;
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                var array = new JArray();
                foreach (var e in Entries.OrderBy(e => e.ProficiencyId))
                    array.Add(WriteEntry(e));

                File.WriteAllText(path, array.ToString(Formatting.Indented));
                LoadedPath = path;
                return true;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"Failed to save proficiencies.json: {ex.Message}");
                return false;
            }
        }

        private static ProficiencyEntry ReadEntry(JObject obj)
        {
            var e = new ProficiencyEntry
            {
                Name = (string?)obj["Name"] ?? "",
                ProficiencyId = obj["ProficiencyId"]?.Value<uint>() ?? 0,
                Version = obj["Version"]?.Value<int>() ?? 1,
            };

            var levels = obj["Levels"] as JArray;
            if (levels != null)
            {
                foreach (var lvlTok in levels.OfType<JObject>())
                {
                    var level = new ProficiencyLevel();
                    var perks = lvlTok["Perks"] as JArray;
                    if (perks != null)
                    {
                        foreach (var perkTok in perks.OfType<JObject>())
                            level.Perks.Add(ReadPerk(perkTok));
                    }
                    e.Levels.Add(level);
                }
            }

            foreach (var prop in obj.Properties())
            {
                if (prop.Name is "Name" or "ProficiencyId" or "Version" or "Levels") continue;
                e.Extra[prop.Name] = prop.Value;
            }
            return e;
        }

        private static ProficiencyPerk ReadPerk(JObject obj)
        {
            var p = new ProficiencyPerk
            {
                Type = obj["Type"]?.Value<int>() ?? 0,
                Value = obj["Value"]?.Value<double>() ?? 0.0,
                SkillId = obj.TryGetValue("SkillId", out var si) ? si.Value<int>() : null,
                AugmentType = obj.TryGetValue("AugmentType", out var at) ? at.Value<int>() : null,
                SpellId = obj.TryGetValue("SpellId", out var sp) ? sp.Value<int>() : null,
                BestiaryId = obj.TryGetValue("BestiaryId", out var bi) ? bi.Value<int>() : null,
                DamageType = obj.TryGetValue("DamageType", out var dt) ? dt.Value<int>() : null,
                ElementId = obj.TryGetValue("ElementId", out var el) ? el.Value<int>() : null,
                Range = obj.TryGetValue("Range", out var rg) ? rg.Value<int>() : null,
            };
            foreach (var prop in obj.Properties())
            {
                if (prop.Name is "Type" or "Value" or "SkillId" or "AugmentType"
                    or "SpellId" or "BestiaryId" or "DamageType" or "ElementId" or "Range") continue;
                p.Extra[prop.Name] = prop.Value;
            }
            return p;
        }

        private static JObject WriteEntry(ProficiencyEntry e)
        {
            var levels = new JArray();
            foreach (var lvl in e.Levels)
            {
                var perks = new JArray();
                foreach (var perk in lvl.Perks)
                    perks.Add(WritePerk(perk));
                levels.Add(new JObject { ["Perks"] = perks });
            }

            // Ordering matches the existing file: Levels, Name, ProficiencyId, Version.
            var obj = new JObject
            {
                ["Levels"] = levels,
                ["Name"] = e.Name,
                ["ProficiencyId"] = e.ProficiencyId,
                ["Version"] = e.Version,
            };
            foreach (var kv in e.Extra)
                if (!obj.ContainsKey(kv.Key))
                    obj[kv.Key] = kv.Value;
            return obj;
        }

        private static JObject WritePerk(ProficiencyPerk p)
        {
            // Preserve the original field ordering (alphabetical) — keeps diffs clean.
            var obj = new JObject();
            if (p.AugmentType.HasValue) obj["AugmentType"] = p.AugmentType.Value;
            if (p.BestiaryId.HasValue)  obj["BestiaryId"]  = p.BestiaryId.Value;
            if (p.DamageType.HasValue)  obj["DamageType"]  = p.DamageType.Value;
            if (p.ElementId.HasValue)   obj["ElementId"]   = p.ElementId.Value;
            if (p.Range.HasValue)       obj["Range"]       = p.Range.Value;
            if (p.SkillId.HasValue)     obj["SkillId"]     = p.SkillId.Value;
            if (p.SpellId.HasValue)     obj["SpellId"]     = p.SpellId.Value;
            obj["Type"] = p.Type;
            obj["Value"] = p.Value;
            foreach (var kv in p.Extra)
                if (!obj.ContainsKey(kv.Key))
                    obj[kv.Key] = kv.Value;
            return obj;
        }

        public static ProficiencyEntry? FindById(uint id) =>
            Entries.FirstOrDefault(e => e.ProficiencyId == id);

        public static string? GetName(uint id) => FindById(id)?.Name;
    }

    // Human-readable labels for the enums in
    // src/creatures/players/proficiencies/proficiencies_definitions.hpp.
    public static class ProficiencyEnums
    {
        public record Choice(int Value, string Label)
        {
            public override string ToString() => $"{Value} — {Label}";
        }

        public static readonly List<Choice> PerkTypes = new()
        {
            new(0,  "Attack Damage"),
            new(1,  "Defense"),
            new(2,  "Weapon/Shield Mod"),
            new(3,  "Skill ID Bonus"),
            new(4,  "Special Magic Level"),
            new(5,  "Augment (Spell)"),
            new(6,  "Bestiary Damage"),
            new(7,  "Dmg vs Boss + Sinister"),
            new(8,  "Critical Hit Chance"),
            new(9,  "Crit Chance — Element Spells/Runes"),
            new(10, "Crit Chance — Offensive Runes"),
            new(11, "Crit Chance — Autoattack"),
            new(12, "Critical Extra Damage"),
            new(13, "Crit Extra Dmg — Element Spells/Runes"),
            new(14, "Crit Extra Dmg — Offensive Runes"),
            new(15, "Crit Extra Dmg — Autoattack"),
            new(16, "Mana Leech"),
            new(17, "Life Leech"),
            new(18, "Mana Gain OnHit"),
            new(19, "Life Gain OnHit"),
            new(20, "Mana Gain OnKill"),
            new(21, "Life Gain OnKill"),
            new(22, "Gain Damage at Range"),
            new(23, "Ranged Hit Chance"),
            new(24, "Attack Range"),
            new(25, "SkillID % as Extra Dmg (Autoattack)"),
            new(26, "SkillID % as Extra Dmg (Spells)"),
            new(27, "SkillID % as Extra Healing (Spells)"),
            new(28, "Alpha Strike Extra Damage"),
            new(29, "Omega Strike Extra Damage"),
            new(30, "Armor Penetration"),
            new(31, "Elemental Pierce"),
        };

        public static readonly List<Choice> Skills = new()
        {
            new(1,  "Magic"),
            new(6,  "Shield"),
            new(7,  "Distance"),
            new(8,  "Sword"),
            new(9,  "Club"),
            new(10, "Axe"),
            new(11, "Fist"),
            new(13, "Fishing"),
        };

        public static readonly List<Choice> AugmentTypes = new()
        {
            new(0,  "None"),
            new(1,  "Mana Cost"),
            new(2,  "Base Damage"),
            new(3,  "Healing"),
            new(4,  "Duration"),
            new(5,  "Additional Targets"),
            new(6,  "Cooldown"),
            new(7,  "Secondary Group Cooldown"),
            new(8,  "Area Enlarged"),
            new(9,  "Increased Damage"),
            new(10, "Enhanced Effect"),
            new(11, "Area Enlarged 2"),
            new(12, "Enhanced Effect 2"),
            new(13, "Increased Skill"),
            new(14, "Life Leech"),
            new(15, "Mana Leech"),
            new(16, "Critical Extra Damage"),
            new(17, "Critical Hit Chance"),
        };

        public static readonly List<Choice> DamageTypes = new()
        {
            new(0,       "None"),
            new(1,       "Physical"),
            new(8,       "Fire"),
            new(16,      "Earth"),
            new(32,      "Energy"),
            new(64,      "Ice"),
            new(128,     "Holy"),
            new(256,     "Death"),
            new(1048576, "Healing"),
        };

        public static string PerkTypeName(int value) =>
            PerkTypes.FirstOrDefault(c => c.Value == value)?.Label ?? value.ToString();

        public static string SkillName(int value) =>
            Skills.FirstOrDefault(c => c.Value == value)?.Label ?? value.ToString();

        // Which optional fields does each perk Type actually consume?
        // Derived from proficiencies_definitions.hpp + real perks in
        // data/JSON/proficiencies.json. Used by the editor to hide fields
        // that would have no effect — everything else gets only Value.
        [Flags]
        public enum PerkField
        {
            None     = 0,
            Skill    = 1 << 0,
            Augment  = 1 << 1,
            Spell    = 1 << 2,
            Bestiary = 1 << 3,
            Damage   = 1 << 4,
            Range    = 1 << 5,
        }

        public static readonly Dictionary<int, PerkField> TypeFields = new()
        {
            { 3,  PerkField.Skill },                                  // SKILLID_BONUS
            { 5,  PerkField.Augment | PerkField.Spell },              // AUGMENT_TYPE (per-spell)
            { 6,  PerkField.Bestiary | PerkField.Damage },            // BESTIARY_DAMAGE
            { 9,  PerkField.Damage },                                 // CRIT_CHANCE_ELEMENT
            { 13, PerkField.Damage },                                 // CRIT_EXTRA_ELEMENT
            { 22, PerkField.Range },                                  // GAIN_DAMAGE_AT_RANGE
            { 25, PerkField.Skill },                                  // SKILLID_% EXTRA DMG autoattack
            { 26, PerkField.Skill },                                  // SKILLID_% EXTRA DMG spells
            { 27, PerkField.Skill },                                  // SKILLID_% EXTRA HEAL spells
            { 31, PerkField.Damage },                                 // ELEMENTAL_PIERCE
        };

        public static PerkField FieldsFor(int perkType) =>
            TypeFields.TryGetValue(perkType, out var f) ? f : PerkField.None;

        // Hint that tells the user what Value should look like for the
        // current perk type. Percentages in this file are stored as 0-1
        // decimals (0.05 = 5%), flat bonuses as integers.
        public static string ValueHint(int perkType) => perkType switch
        {
            0 or 1 or 2 or 24 or 28 or 29 => "flat, e.g. 1",
            3 or 4                        => "integer, e.g. 1",
            22                            => "% gain, e.g. 0.05 = 5%",
            _                             => "% fraction, e.g. 0.05 = 5%",
        };
    }

    // One-click "insert a typical perk" options for the editor.
    // Values here mirror the most common pattern found in the real
    // proficiencies.json — users tweak the number after inserting.
    public record PerkPreset(
        string Label,
        int Type,
        double Value,
        int? SkillId = null,
        int? AugmentType = null,
        int? SpellId = null,
        int? BestiaryId = null,
        int? DamageType = null,
        int? Range = null)
    {
        public ProficiencyPerk ToPerk() => new()
        {
            Type = Type, Value = Value, SkillId = SkillId,
            AugmentType = AugmentType, SpellId = SpellId,
            BestiaryId = BestiaryId, DamageType = DamageType, Range = Range,
        };
    }

    public static class PerkLibrary
    {
        public static readonly List<PerkPreset> Presets = new()
        {
            new("Sword Skill +1",         3, 1, SkillId: 8),
            new("Axe Skill +1",           3, 1, SkillId: 10),
            new("Club Skill +1",          3, 1, SkillId: 9),
            new("Distance Skill +1",      3, 1, SkillId: 7),
            new("Magic Skill +1",         3, 1, SkillId: 1),
            new("Fist Skill +1",          3, 1, SkillId: 11),
            new("Shield Skill +1",        3, 1, SkillId: 6),
            new("Fishing Skill +1",       3, 1, SkillId: 13),

            new("Attack Damage +1",       0, 1),
            new("Defense +1",             1, 1),
            new("Special Magic Level +5%",4, 0.05),

            new("Critical Hit Chance +5%",    8,  0.05),
            new("Critical Extra Damage +10%", 12, 0.10),
            new("Crit Chance Autoattack +5%", 11, 0.05),
            new("Crit Extra Dmg Autoattack +10%", 15, 0.10),

            new("Mana Leech +2%",         16, 0.02),
            new("Life Leech +2%",         17, 0.02),
            new("Mana Gain OnHit 2%",     18, 0.02),
            new("Life Gain OnHit 2%",     19, 0.02),

            new("Dmg vs Boss +3%",        7,  0.03),
            new("Ranged Hit Chance +5%",  23, 0.05),
            new("Attack Range +1",        24, 1),
            new("Gain Damage at Range +10%", 22, 0.10, Range: 5),

            new("Armor Penetration +5%",  30, 0.05),
            new("Alpha Strike Dmg +10%",  28, 0.10),
            new("Omega Strike Dmg +10%",  29, 0.10),

            new("SkillID % as Extra Dmg (Autoattack) 5% (Sword)",  25, 0.05, SkillId: 8),
            new("SkillID % as Extra Dmg (Spells) 10% (Magic)",     26, 0.10, SkillId: 1),
            new("SkillID % as Extra Heal (Spells) 5% (Magic)",     27, 5.0,  SkillId: 1),

            new("Augment: Base Damage +10% (spell id=0)",          5, 0.10, AugmentType: 2,  SpellId: 0),
            new("Augment: Critical Extra Dmg +10% (spell id=0)",   5, 0.10, AugmentType: 16, SpellId: 0),
            new("Augment: Critical Hit Chance +5% (spell id=0)",   5, 0.05, AugmentType: 17, SpellId: 0),

            new("Bestiary Damage +3% vs id=0 (Physical)",          6, 0.03, BestiaryId: 0, DamageType: 1),
            new("Crit Chance Fire Spells +5%",                     9, 0.05, DamageType: 8),
            new("Crit Extra Dmg Fire Spells +10%",                 13, 0.10, DamageType: 8),
            new("Elemental Pierce Fire +5%",                       31, 0.05, DamageType: 8),
        };
    }

    // Weapon-category skeletons — 7 empty levels + Level 1 bootstrapped
    // with the matching Skill +1 perk. User fills the rest (or uses
    // PerkPresets on each level).
    public record ProficiencyTemplate(string Label, int SkillId, string HintNamePrefix);

    public static class ProficiencyTemplates
    {
        public static readonly List<ProficiencyTemplate> Templates = new()
        {
            new("1H Sword",    8,  "1H Sword"),
            new("1H Axe",      10, "1H Axe"),
            new("1H Club",     9,  "1H Club"),
            new("2H Sword",    8,  "2H Sword"),
            new("2H Axe",      10, "2H Axe"),
            new("2H Club",     9,  "2H Club"),
            new("2H Bow",      7,  "2H Bow"),
            new("2H Crossbow", 7,  "2H Crossbow"),
            new("2H Fist",     11, "2H Fist"),
            new("1H Wand",     1,  "1H Wand"),
            new("1H Rod",      1,  "1H Rod"),
        };

        public static ProficiencyEntry Build(ProficiencyTemplate t, string name, uint id, int levelCount = 7)
        {
            var e = new ProficiencyEntry { Name = name, ProficiencyId = id, Version = 6 };
            for (int i = 0; i < levelCount; i++)
            {
                var lvl = new ProficiencyLevel();
                if (i == 0)
                {
                    lvl.Perks.Add(new ProficiencyPerk { Type = 3, Value = 1, SkillId = t.SkillId });
                }
                e.Levels.Add(lvl);
            }
            return e;
        }
    }
}
