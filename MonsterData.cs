using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;

namespace Assets_Editor
{
    // Typed model for a Canary-format monster .lua file. Core fields are
    // parsed explicitly; complex blocks (loot/attacks/elements/…) are kept
    // as raw Lua text and re-emitted verbatim on save so we never drop
    // custom spells or loot rules the editor doesn't understand.
    public class LuaMonsterData : INotifyPropertyChanged
    {
        private string _name = "";
        public string Name { get => _name; set { _name = value; On(nameof(Name)); On(nameof(Display)); } }

        public string? Description { get; set; }
        public uint RaceId { get; set; }
        public uint Health { get; set; } = 100;
        public uint MaxHealth { get; set; } = 100;
        public uint Experience { get; set; } = 50;
        public uint Speed { get; set; } = 200;
        public uint Corpse { get; set; }
        public string Race { get; set; } = "blood";
        public uint ManaCost { get; set; }

        public uint LookType { get; set; }
        public uint LookTypeEx { get; set; }
        public uint LookHead { get; set; }
        public uint LookBody { get; set; }
        public uint LookLegs { get; set; }
        public uint LookFeet { get; set; }
        public uint LookAddons { get; set; }
        public uint LookMount { get; set; }

        public bool Summonable { get; set; }
        public bool Attackable { get; set; } = true;
        public bool Hostile { get; set; } = true;
        public bool Convinceable { get; set; }
        public bool Pushable { get; set; }
        public bool RewardBoss { get; set; }
        public bool Illusionable { get; set; }
        public bool CanPushItems { get; set; }
        public bool CanPushCreatures { get; set; }
        public uint StaticAttackChance { get; set; } = 90;
        public uint TargetDistance { get; set; } = 1;
        public uint RunHealth { get; set; }
        public bool HealthHidden { get; set; }
        public bool CanWalkOnEnergy { get; set; }
        public bool CanWalkOnFire { get; set; }
        public bool CanWalkOnPoison { get; set; }

        public bool HasBestiary { get; set; }
        public string? BestiaryClass { get; set; }
        public string? BestiaryRace { get; set; }
        public uint BestiaryToKill { get; set; }
        public uint BestiaryFirstUnlock { get; set; }
        public uint BestiarySecondUnlock { get; set; }
        public uint BestiaryCharmsPoints { get; set; }
        public uint BestiaryStars { get; set; }
        public uint BestiaryOccurrence { get; set; }
        public string? BestiaryLocations { get; set; }

        public bool HasBosstiary { get; set; }
        public uint BosstiaryRaceId { get; set; }
        public string? BosstiaryRace { get; set; }

        public string? RawLoot { get; set; }
        public string? RawAttacks { get; set; }
        public string? RawDefenses { get; set; }
        public string? RawElements { get; set; }
        public string? RawImmunities { get; set; }
        public string? RawVoices { get; set; }
        public string? RawEvents { get; set; }
        public string? RawChangeTarget { get; set; }
        public string? RawStrategiesTarget { get; set; }
        public string? RawSummon { get; set; }
        public string? RawLight { get; set; }

        public string? SourcePath { get; set; }

        // Bitmap derived from lookType (or lookTypeEx for item-based monsters).
        // Populated lazily by MonsterEditor; stays null if DAT isn't loaded.
        private Bitmap? _image;
        public Bitmap? Image
        {
            get => _image;
            set { if (_image != value) { _image = value; On(nameof(Image)); } }
        }

        public string Display => string.IsNullOrEmpty(Name)
            ? $"(monster {RaceId})"
            : $"{RaceId}: {Name}";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void On(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public static class LuaMonsterParser
    {
        public static LuaMonsterData? ParseFile(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            try
            {
                string lua = File.ReadAllText(filePath);
                var data = ParseLua(lua);
                if (data != null) data.SourcePath = filePath;
                return data;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"Failed to parse monster lua {filePath}: {ex.Message}");
                return null;
            }
        }

        public static LuaMonsterData? ParseLua(string lua)
        {
            if (string.IsNullOrWhiteSpace(lua)) return null;

            var nameMatch = Regex.Match(lua, @"Game\.createMonsterType\(\s*""([^""]+)""\s*\)");
            if (!nameMatch.Success) return null;

            var d = new LuaMonsterData { Name = nameMatch.Groups[1].Value };

            d.Description = ExtStr(lua, @"monster\.description\s*=\s*""([^""]+)""");
            d.RaceId = ExtUint(lua, @"monster\.raceId\s*=\s*(\d+)");
            d.Health = ExtUint(lua, @"monster\.health\s*=\s*(\d+)");
            d.MaxHealth = ExtUint(lua, @"monster\.maxHealth\s*=\s*(\d+)");
            d.Experience = ExtUint(lua, @"monster\.experience\s*=\s*(\d+)");
            d.Speed = ExtUint(lua, @"monster\.speed\s*=\s*(\d+)");
            d.Corpse = ExtUint(lua, @"monster\.corpse\s*=\s*(\d+)");
            d.Race = ExtStr(lua, @"monster\.race\s*=\s*""([^""]+)""") ?? "blood";
            d.ManaCost = ExtUint(lua, @"monster\.manaCost\s*=\s*(\d+)");

            string? outfitBlock = ExtBlock(lua, @"monster\.outfit\s*=\s*\{");
            if (outfitBlock != null)
            {
                d.LookType = ExtUint(outfitBlock, @"lookType\s*=\s*(\d+)");
                d.LookTypeEx = ExtUint(outfitBlock, @"lookTypeEx\s*=\s*(\d+)");
                d.LookHead = ExtUint(outfitBlock, @"lookHead\s*=\s*(\d+)");
                d.LookBody = ExtUint(outfitBlock, @"lookBody\s*=\s*(\d+)");
                d.LookLegs = ExtUint(outfitBlock, @"lookLegs\s*=\s*(\d+)");
                d.LookFeet = ExtUint(outfitBlock, @"lookFeet\s*=\s*(\d+)");
                d.LookAddons = ExtUint(outfitBlock, @"lookAddons\s*=\s*(\d+)");
                d.LookMount = ExtUint(outfitBlock, @"lookMount\s*=\s*(\d+)");
            }

            string? flagsBlock = ExtBlock(lua, @"monster\.flags\s*=\s*\{");
            if (flagsBlock != null)
            {
                d.Summonable = ExtBool(flagsBlock, @"summonable\s*=\s*(true|false)");
                d.Attackable = ExtBool(flagsBlock, @"attackable\s*=\s*(true|false)", true);
                d.Hostile = ExtBool(flagsBlock, @"hostile\s*=\s*(true|false)", true);
                d.Convinceable = ExtBool(flagsBlock, @"convinceable\s*=\s*(true|false)");
                d.Pushable = ExtBool(flagsBlock, @"pushable\s*=\s*(true|false)");
                d.RewardBoss = ExtBool(flagsBlock, @"rewardBoss\s*=\s*(true|false)");
                d.Illusionable = ExtBool(flagsBlock, @"illusionable\s*=\s*(true|false)");
                d.CanPushItems = ExtBool(flagsBlock, @"canPushItems\s*=\s*(true|false)");
                d.CanPushCreatures = ExtBool(flagsBlock, @"canPushCreatures\s*=\s*(true|false)");
                d.StaticAttackChance = ExtUint(flagsBlock, @"staticAttackChance\s*=\s*(\d+)");
                d.TargetDistance = ExtUint(flagsBlock, @"targetDistance\s*=\s*(\d+)");
                d.RunHealth = ExtUint(flagsBlock, @"runHealth\s*=\s*(\d+)");
                d.HealthHidden = ExtBool(flagsBlock, @"healthHidden\s*=\s*(true|false)");
                d.CanWalkOnEnergy = ExtBool(flagsBlock, @"canWalkOnEnergy\s*=\s*(true|false)");
                d.CanWalkOnFire = ExtBool(flagsBlock, @"canWalkOnFire\s*=\s*(true|false)");
                d.CanWalkOnPoison = ExtBool(flagsBlock, @"canWalkOnPoison\s*=\s*(true|false)");
            }

            string? bestiaryBlock = ExtBlock(lua, @"monster\.Bestiary\s*=\s*\{");
            if (bestiaryBlock != null)
            {
                d.HasBestiary = true;
                d.BestiaryClass = ExtStr(bestiaryBlock, @"class\s*=\s*""([^""]+)""");
                d.BestiaryRace = ExtStr(bestiaryBlock, @"race\s*=\s*(BESTY_RACE_\w+)");
                d.BestiaryToKill = ExtUint(bestiaryBlock, @"toKill\s*=\s*(\d+)");
                d.BestiaryFirstUnlock = ExtUint(bestiaryBlock, @"FirstUnlock\s*=\s*(\d+)");
                d.BestiarySecondUnlock = ExtUint(bestiaryBlock, @"SecondUnlock\s*=\s*(\d+)");
                d.BestiaryCharmsPoints = ExtUint(bestiaryBlock, @"CharmsPoints\s*=\s*(\d+)");
                d.BestiaryStars = ExtUint(bestiaryBlock, @"Stars\s*=\s*(\d+)");
                d.BestiaryOccurrence = ExtUint(bestiaryBlock, @"Occurrence\s*=\s*(\d+)");
                var locMatch = Regex.Match(bestiaryBlock, @"Locations\s*=\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Singleline);
                if (locMatch.Success)
                    d.BestiaryLocations = locMatch.Groups[1].Value.Replace("\\z", "").Replace("\n", " ").Replace("\t", " ").Trim();
            }

            string? bosstiaryBlock = ExtBlock(lua, @"monster\.bosstiary\s*=\s*\{");
            if (bosstiaryBlock != null)
            {
                d.HasBosstiary = true;
                d.BosstiaryRaceId = ExtUint(bosstiaryBlock, @"bossRaceId\s*=\s*(\d+)");
                d.BosstiaryRace = ExtStr(bosstiaryBlock, @"bossRace\s*=\s*(RARITY_\w+)");
            }

            d.RawLoot = ExtRawBlock(lua, @"monster\.loot\s*=\s*\{");
            d.RawAttacks = ExtRawBlock(lua, @"monster\.attacks\s*=\s*\{");
            d.RawDefenses = ExtRawBlock(lua, @"monster\.defenses\s*=\s*\{");
            d.RawElements = ExtRawBlock(lua, @"monster\.elements\s*=\s*\{");
            d.RawImmunities = ExtRawBlock(lua, @"monster\.immunities\s*=\s*\{");
            d.RawVoices = ExtRawBlock(lua, @"monster\.voices\s*=\s*\{");
            d.RawEvents = ExtRawBlock(lua, @"monster\.events\s*=\s*\{");
            d.RawChangeTarget = ExtRawBlock(lua, @"monster\.changeTarget\s*=\s*\{");
            d.RawStrategiesTarget = ExtRawBlock(lua, @"monster\.strategiesTarget\s*=\s*\{");
            d.RawSummon = ExtRawBlock(lua, @"monster\.summons?\s*=\s*\{");
            d.RawLight = ExtRawBlock(lua, @"monster\.light\s*=\s*\{");

            return d;
        }

        public static List<LuaMonsterData> ParseDirectory(string directoryPath)
        {
            var results = new List<LuaMonsterData>();
            if (!Directory.Exists(directoryPath)) return results;
            try
            {
                foreach (var file in Directory.EnumerateFiles(directoryPath, "*.lua", SearchOption.AllDirectories))
                {
                    var data = ParseFile(file);
                    if (data != null && !string.IsNullOrEmpty(data.Name))
                        results.Add(data);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"ParseDirectory({directoryPath}) failed: {ex.Message}");
            }
            return results;
        }

        #region Helpers
        // Extracts the Lua table BODY (between the opening { and the matching closing }).
        private static string? ExtBlock(string lua, string startPattern)
        {
            var match = Regex.Match(lua, startPattern);
            if (!match.Success) return null;
            int start = match.Index + match.Length;
            int depth = 1, i = start;
            while (i < lua.Length && depth > 0)
            {
                if (lua[i] == '{') depth++;
                else if (lua[i] == '}') depth--;
                i++;
            }
            return depth == 0 ? lua.Substring(start, i - start - 1) : null;
        }

        // Extracts the Lua table INCLUDING braces — so we can round-trip
        // the full block verbatim via string interpolation ("monster.loot = " + raw).
        private static string? ExtRawBlock(string lua, string startPattern)
        {
            var match = Regex.Match(lua, startPattern);
            if (!match.Success) return null;
            int start = match.Index + match.Length;
            int depth = 1, i = start;
            while (i < lua.Length && depth > 0)
            {
                if (lua[i] == '{') depth++;
                else if (lua[i] == '}') depth--;
                i++;
            }
            return depth == 0 ? "{" + lua.Substring(start, i - start - 1) + "}" : null;
        }

        private static string? ExtStr(string text, string pattern)
        {
            var m = Regex.Match(text, pattern);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static uint ExtUint(string text, string pattern)
        {
            var m = Regex.Match(text, pattern);
            return m.Success && uint.TryParse(m.Groups[1].Value, out uint v) ? v : 0;
        }

        private static bool ExtBool(string text, string pattern, bool defaultVal = false)
        {
            var m = Regex.Match(text, pattern);
            return m.Success ? m.Groups[1].Value == "true" : defaultVal;
        }
        #endregion

        #region Auto-fill helpers
        public static string GuessBestiaryClass(LuaMonsterData d)
        {
            string path = (d.SourcePath ?? "").ToLower().Replace('\\', '/');
            if (path.Contains("/dragons/")) return "Dragon";
            if (path.Contains("/demons/")) return "Demon";
            if (path.Contains("/undead/")) return "Undead";
            if (path.Contains("/humanoids/") || path.Contains("/humans/")) return "Human";
            if (path.Contains("/reptiles/")) return "Reptile";
            if (path.Contains("/amphibics/")) return "Amphibic";
            if (path.Contains("/aquatics/")) return "Aquatic";
            if (path.Contains("/birds/")) return "Bird";
            if (path.Contains("/constructs/")) return "Construct";
            if (path.Contains("/elementals/")) return "Elemental";
            if (path.Contains("/fey/")) return "Fey";
            if (path.Contains("/giants/")) return "Giant";
            if (path.Contains("/lycanthropes/")) return "Lycanthrope";
            if (path.Contains("/magicals/") || path.Contains("/magical/")) return "Magical";
            if (path.Contains("/mammals/")) return "Mammal";
            if (path.Contains("/plants/")) return "Plant";
            if (path.Contains("/slimes/")) return "Slime";
            if (path.Contains("/vermin/")) return "Vermin";
            if (path.Contains("/bosses/")) return "Boss";
            return "Extra Dimensional";
        }

        public static string GuessBestiaryRace(string? className) =>
            (className ?? "").ToLower() switch
            {
                "dragon" => "BESTY_RACE_DRAGON",
                "demon" => "BESTY_RACE_DEMON",
                "undead" => "BESTY_RACE_UNDEAD",
                "human" or "humanoid" => "BESTY_RACE_HUMAN",
                "reptile" => "BESTY_RACE_REPTILE",
                "amphibic" => "BESTY_RACE_AMPHIBIC",
                "aquatic" => "BESTY_RACE_AQUATIC",
                "bird" => "BESTY_RACE_BIRD",
                "construct" => "BESTY_RACE_CONSTRUCT",
                "elemental" => "BESTY_RACE_ELEMENTAL",
                "fey" => "BESTY_RACE_FEY",
                "giant" => "BESTY_RACE_GIANT",
                "lycanthrope" => "BESTY_RACE_LYCANTHROPE",
                "magical" => "BESTY_RACE_MAGICAL",
                "mammal" => "BESTY_RACE_MAMMAL",
                "plant" => "BESTY_RACE_PLANT",
                "slime" => "BESTY_RACE_SLIME",
                "vermin" => "BESTY_RACE_VERMIN",
                _ => "BESTY_RACE_MAGICAL"
            };

        public static uint GuessBestiaryStars(uint experience)
        {
            if (experience <= 50) return 1;
            if (experience <= 500) return 2;
            return 3;
        }

        public static (uint toKill, uint first, uint second, uint charms) GuessBestiaryCounts(uint stars) =>
            stars switch
            {
                1 => (500u, 25u, 250u, 15u),
                2 => (1000u, 50u, 500u, 25u),
                _ => (2500u, 100u, 1000u, 50u),
            };

        public static void AutoFillBestiary(LuaMonsterData d)
        {
            if (d.HasBestiary || d.RewardBoss) return;
            d.HasBestiary = true;
            d.BestiaryClass = GuessBestiaryClass(d);
            d.BestiaryRace = GuessBestiaryRace(d.BestiaryClass);
            d.BestiaryStars = GuessBestiaryStars(d.Experience);
            var (toKill, first, second, charms) = GuessBestiaryCounts(d.BestiaryStars);
            d.BestiaryToKill = toKill;
            d.BestiaryFirstUnlock = first;
            d.BestiarySecondUnlock = second;
            d.BestiaryCharmsPoints = charms;
            d.BestiaryOccurrence = 0;
            d.BestiaryLocations = "Unknown";
        }

        public static void AutoFillBosstiary(LuaMonsterData d, uint nextBossRaceId)
        {
            if (d.HasBosstiary || !d.RewardBoss) return;
            d.HasBosstiary = true;
            d.BosstiaryRaceId = nextBossRaceId;
            if (d.Health >= 50000) d.BosstiaryRace = "RARITY_NEMESIS";
            else if (d.Health >= 10000) d.BosstiaryRace = "RARITY_ARCHFOE";
            else d.BosstiaryRace = "RARITY_BANE";
        }
        #endregion
    }

    public static class LuaMonsterGenerator
    {
        public static string GenerateFullLua(LuaMonsterData d)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"local mType = Game.createMonsterType(\"{Esc(d.Name)}\")");
            sb.AppendLine("local monster = {}");
            sb.AppendLine();

            sb.AppendLine($"monster.description = \"{Esc(d.Description ?? ("a " + d.Name.ToLower()))}\"");
            sb.AppendLine($"monster.experience = {d.Experience}");

            sb.AppendLine("monster.outfit = {");
            if (d.LookTypeEx > 0 && d.LookType == 0)
            {
                sb.AppendLine($"\tlookTypeEx = {d.LookTypeEx},");
            }
            else
            {
                sb.AppendLine($"\tlookType = {d.LookType},");
                sb.AppendLine($"\tlookHead = {d.LookHead},");
                sb.AppendLine($"\tlookBody = {d.LookBody},");
                sb.AppendLine($"\tlookLegs = {d.LookLegs},");
                sb.AppendLine($"\tlookFeet = {d.LookFeet},");
                sb.AppendLine($"\tlookAddons = {d.LookAddons},");
                sb.AppendLine($"\tlookMount = {d.LookMount},");
            }
            sb.AppendLine("}");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(d.RawEvents))
            {
                sb.AppendLine($"monster.events = {d.RawEvents}");
                sb.AppendLine();
            }

            sb.AppendLine($"monster.raceId = {d.RaceId}");
            if (d.HasBestiary)
            {
                sb.AppendLine("monster.Bestiary = {");
                sb.AppendLine($"\tclass = \"{Esc(d.BestiaryClass ?? "Unknown")}\",");
                sb.AppendLine($"\trace = {d.BestiaryRace ?? "BESTY_RACE_MAGICAL"},");
                sb.AppendLine($"\ttoKill = {d.BestiaryToKill},");
                sb.AppendLine($"\tFirstUnlock = {d.BestiaryFirstUnlock},");
                sb.AppendLine($"\tSecondUnlock = {d.BestiarySecondUnlock},");
                sb.AppendLine($"\tCharmsPoints = {d.BestiaryCharmsPoints},");
                sb.AppendLine($"\tStars = {d.BestiaryStars},");
                sb.AppendLine($"\tOccurrence = {d.BestiaryOccurrence},");
                sb.AppendLine($"\tLocations = \"{Esc(d.BestiaryLocations ?? "Unknown")}\"");
                sb.AppendLine("}");
            }

            if (d.HasBosstiary)
            {
                sb.AppendLine();
                sb.AppendLine("monster.bosstiary = {");
                sb.AppendLine($"\tbossRaceId = {d.BosstiaryRaceId},");
                sb.AppendLine($"\tbossRace = {d.BosstiaryRace ?? "RARITY_BANE"},");
                sb.AppendLine("}");
            }
            sb.AppendLine();

            sb.AppendLine($"monster.health = {d.Health}");
            sb.AppendLine($"monster.maxHealth = {d.MaxHealth}");
            sb.AppendLine($"monster.race = \"{Esc(d.Race ?? "blood")}\"");
            sb.AppendLine($"monster.corpse = {d.Corpse}");
            sb.AppendLine($"monster.speed = {d.Speed}");
            sb.AppendLine($"monster.manaCost = {d.ManaCost}");
            sb.AppendLine();

            sb.AppendLine($"monster.changeTarget = {d.RawChangeTarget ?? "{\n\tinterval = 4000,\n\tchance = 10,\n}"}");
            sb.AppendLine();
            sb.AppendLine($"monster.strategiesTarget = {d.RawStrategiesTarget ?? "{\n\tnearest = 70,\n\thealth = 10,\n\tdamage = 10,\n\trandom = 10,\n}"}");
            sb.AppendLine();

            sb.AppendLine("monster.flags = {");
            sb.AppendLine($"\tsummonable = {B(d.Summonable)},");
            sb.AppendLine($"\tattackable = {B(d.Attackable)},");
            sb.AppendLine($"\thostile = {B(d.Hostile)},");
            sb.AppendLine($"\tconvinceable = {B(d.Convinceable)},");
            sb.AppendLine($"\tpushable = {B(d.Pushable)},");
            sb.AppendLine($"\trewardBoss = {B(d.RewardBoss)},");
            sb.AppendLine($"\tillusionable = {B(d.Illusionable)},");
            sb.AppendLine($"\tcanPushItems = {B(d.CanPushItems)},");
            sb.AppendLine($"\tcanPushCreatures = {B(d.CanPushCreatures)},");
            sb.AppendLine($"\tstaticAttackChance = {d.StaticAttackChance},");
            sb.AppendLine($"\ttargetDistance = {d.TargetDistance},");
            sb.AppendLine($"\trunHealth = {d.RunHealth},");
            sb.AppendLine($"\thealthHidden = {B(d.HealthHidden)},");
            sb.AppendLine($"\tisBlockable = false,");
            sb.AppendLine($"\tcanWalkOnEnergy = {B(d.CanWalkOnEnergy)},");
            sb.AppendLine($"\tcanWalkOnFire = {B(d.CanWalkOnFire)},");
            sb.AppendLine($"\tcanWalkOnPoison = {B(d.CanWalkOnPoison)},");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine($"monster.light = {d.RawLight ?? "{\n\tlevel = 0,\n\tcolor = 0,\n}"}");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(d.RawVoices))
            {
                sb.AppendLine($"monster.voices = {d.RawVoices}");
                sb.AppendLine();
            }

            sb.AppendLine($"monster.loot = {d.RawLoot ?? "{}"}");
            sb.AppendLine();

            sb.AppendLine($"monster.attacks = {d.RawAttacks ?? "{\n\t{ name = \"melee\", interval = 2000, chance = 100, minDamage = 0, maxDamage = -50 },\n}"}");
            sb.AppendLine();

            sb.AppendLine($"monster.defenses = {d.RawDefenses ?? "{\n\tdefense = 10,\n\tarmor = 10,\n}"}");
            sb.AppendLine();

            sb.AppendLine($"monster.elements = {d.RawElements ?? DefaultElements()}");
            sb.AppendLine();

            sb.AppendLine($"monster.immunities = {d.RawImmunities ?? DefaultImmunities()}");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(d.RawSummon))
            {
                sb.AppendLine($"monster.summon = {d.RawSummon}");
                sb.AppendLine();
            }

            sb.AppendLine("mType:register(monster)");
            return sb.ToString();
        }

        public static string GetTemplate()
        {
            var d = new LuaMonsterData
            {
                Name = "New Monster",
                Description = "a new monster",
                RaceId = 9999,
                Health = 100, MaxHealth = 100, Experience = 50, Speed = 200,
                Race = "blood", LookType = 1, Hostile = true, Attackable = true,
            };
            LuaMonsterParser.AutoFillBestiary(d);
            return GenerateFullLua(d);
        }

        public static bool SaveToFile(LuaMonsterData d, string? path = null)
        {
            path ??= d.SourcePath;
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, GenerateFullLua(d));
                d.SourcePath = path;
                return true;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"SaveToFile({path}) failed: {ex.Message}");
                return false;
            }
        }

        private static string B(bool v) => v ? "true" : "false";
        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

        private static string DefaultElements() => @"{
	{ type = COMBAT_PHYSICALDAMAGE, percent = 0 },
	{ type = COMBAT_ENERGYDAMAGE, percent = 0 },
	{ type = COMBAT_EARTHDAMAGE, percent = 0 },
	{ type = COMBAT_FIREDAMAGE, percent = 0 },
	{ type = COMBAT_LIFEDRAIN, percent = 0 },
	{ type = COMBAT_MANADRAIN, percent = 0 },
	{ type = COMBAT_DROWNDAMAGE, percent = 0 },
	{ type = COMBAT_ICEDAMAGE, percent = 0 },
	{ type = COMBAT_HOLYDAMAGE, percent = 0 },
	{ type = COMBAT_DEATHDAMAGE, percent = 0 },
}";

        private static string DefaultImmunities() => @"{
	{ type = ""paralyze"", condition = false },
	{ type = ""outfit"", condition = false },
	{ type = ""invisible"", condition = false },
	{ type = ""bleed"", condition = false },
}";
    }

    // In-memory registry of loaded monsters. Canary (jataria) auto-scans the
    // monster/ folder recursively at server start and on `/reload monsters`
    // (see src/game/functions/game_reload.cpp → reloadMonsters), so writing
    // the .lua is enough — no monsters.xml registration needed.
    public static class MonsterRegistry
    {
        public static List<LuaMonsterData> Monsters { get; private set; } = new();
        public static string? LoadedFolder { get; private set; }
        public static bool IsLoaded => Monsters.Count > 0;

        public static int LoadFolder(string path)
        {
            var list = LuaMonsterParser.ParseDirectory(path);
            Monsters = list;
            LoadedFolder = path;
            return list.Count;
        }

        public static uint NextFreeRaceId()
        {
            uint max = 0;
            foreach (var m in Monsters) if (m.RaceId > max) max = m.RaceId;
            return max == 0 ? 10000 : max + 1;
        }

        public static uint NextFreeBossRaceId()
        {
            uint max = 0;
            foreach (var m in Monsters)
                if (m.HasBosstiary && m.BosstiaryRaceId > max) max = m.BosstiaryRaceId;
            return max == 0 ? 500 : max + 1;
        }
    }
}
