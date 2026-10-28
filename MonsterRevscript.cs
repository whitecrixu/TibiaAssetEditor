using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Assets_Editor
{
    // One `do local loot = Loot() ... mType:addLoot(loot) end` block of a revscript monster.
    // Original* values and the spans point into the text the monster was parsed from; the
    // editable properties are what the user sees. New entries (added in the editor) have no spans.
    public class MonsterLootEntry : INotifyPropertyChanged
    {
        private decimal _id, _chance, _maxCount = 1;
        private string _comment = "";
        public decimal Id { get => _id; set { if (_id != value) { _id = value; On(nameof(Id)); } } }
        public decimal Chance { get => _chance; set { if (_chance != value) { _chance = value; On(nameof(Chance)); } } }
        public decimal MaxCount { get => _maxCount; set { if (_maxCount != value) { _maxCount = value; On(nameof(MaxCount)); } } }
        public string Comment { get => _comment; set { value ??= ""; if (_comment != value) { _comment = value; On(nameof(Comment)); } } }

        // Locked = the block has calls the editor can't safely rewrite (item by name, a setter
        // used twice, ...). It's shown but its fields can't be changed; deleting it is still safe.
        public bool Locked { get; init; }
        public bool IsEditable => !Locked;
        public string? LockReason { get; init; }
        public bool IsNew { get; init; }

        internal uint OriginalId, OriginalChance, OriginalMaxCount = 1;
        internal string OriginalComment = "";
        internal int BlockStart, BlockLength;       // whole block, `do` line through `end` line incl. newline
        internal int IdArgStart = -1, IdLineEnd;    // setId( <arg>) [-- comment]  → [IdArgStart, IdLineEnd)
        internal int ChanceArgStart = -1, ChanceArgLength;
        internal int MaxArgStart = -1, MaxArgLength;
        internal int MaxInsertAt;                   // where a missing setMaxCount line goes
        internal string InnerIndent = "\t";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void On(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    // Everything needed to patch a revscript (mType:xxx(...)) monster file in place.
    // Text is the file decoded as Latin-1 so every byte maps to exactly one char and
    // untouched bytes are written back unchanged, whatever their encoding.
    public sealed class RevscriptSource
    {
        internal string Text = "";
        internal readonly Dictionary<string, (int ArgStart, int ArgLength)> Sites = new();
        internal readonly Dictionary<string, object?> Original = new();
        internal readonly Dictionary<string, string> Locked = new();   // field key → reason
        internal readonly Dictionary<string, (int Start, int Length)> OutfitValues = new();
        internal int OutfitArgStart = -1, OutfitArgLength;
        internal int ScalarInsertAt;
        internal string Indent = "";
        internal int LootInsertAt;
        internal string LootIndent = "", LootInnerIndent = "\t";
        internal readonly List<MonsterLootEntry> OriginalLoot = new();
        public string? LootLockReason { get; internal set; }
        public bool LootLocked => LootLockReason != null;

        public bool IsLocked(string key) => Locked.ContainsKey(key);
        public string? LockReason(string key) => Locked.TryGetValue(key, out var r) ? r : null;
    }

    public static class RevscriptPatcher
    {
        private enum Kind { UInt, Bool, Str }

        private sealed record Field(string Key, Kind Kind, Func<LuaMonsterData, object?> Get, Action<LuaMonsterData, object?> Set);

        // Scalar mType calls that the editor rewrites. Key = the MonsterType method name
        // (src/luascript.cpp registerMethod("MonsterType", ...)); it's also the key the UI
        // uses to disable a control whose call can't be patched.
        private static readonly Field[] Fields =
        {
            new("name", Kind.Str, d => d.Name, (d, v) => d.Name = (string?)v ?? ""),
            new("nameDescription", Kind.Str, d => d.Description, (d, v) => d.Description = (string?)v),
            new("experience", Kind.UInt, d => d.Experience, (d, v) => d.Experience = (uint)v!),
            new("maxHealth", Kind.UInt, d => d.MaxHealth, (d, v) => d.MaxHealth = (uint)v!),
            new("health", Kind.UInt, d => d.Health, (d, v) => d.Health = (uint)v!),
            new("race", Kind.Str, d => d.Race, (d, v) => d.Race = (string?)v ?? "blood"),
            new("manaCost", Kind.UInt, d => d.ManaCost, (d, v) => d.ManaCost = (uint)v!),
            new("baseSpeed", Kind.UInt, d => d.Speed, (d, v) => d.Speed = (uint)v!),
            new("corpseId", Kind.UInt, d => d.Corpse, (d, v) => d.Corpse = (uint)v!),
            new("isAttackable", Kind.Bool, d => d.Attackable, (d, v) => d.Attackable = (bool)v!),
            new("isConvinceable", Kind.Bool, d => d.Convinceable, (d, v) => d.Convinceable = (bool)v!),
            new("isSummonable", Kind.Bool, d => d.Summonable, (d, v) => d.Summonable = (bool)v!),
            new("isIllusionable", Kind.Bool, d => d.Illusionable, (d, v) => d.Illusionable = (bool)v!),
            new("isHostile", Kind.Bool, d => d.Hostile, (d, v) => d.Hostile = (bool)v!),
            new("isHealthHidden", Kind.Bool, d => d.HealthHidden, (d, v) => d.HealthHidden = (bool)v!),
            new("isPushable", Kind.Bool, d => d.Pushable, (d, v) => d.Pushable = (bool)v!),
            new("canPushItems", Kind.Bool, d => d.CanPushItems, (d, v) => d.CanPushItems = (bool)v!),
            new("canPushCreatures", Kind.Bool, d => d.CanPushCreatures, (d, v) => d.CanPushCreatures = (bool)v!),
            new("targetDistance", Kind.UInt, d => d.TargetDistance, (d, v) => d.TargetDistance = (uint)v!),
            new("runHealth", Kind.UInt, d => d.RunHealth, (d, v) => d.RunHealth = (uint)v!),
            new("staticAttackChance", Kind.UInt, d => d.StaticAttackChance, (d, v) => d.StaticAttackChance = (uint)v!),
            new("canWalkOnEnergy", Kind.Bool, d => d.CanWalkOnEnergy, (d, v) => d.CanWalkOnEnergy = (bool)v!),
            new("canWalkOnFire", Kind.Bool, d => d.CanWalkOnFire, (d, v) => d.CanWalkOnFire = (bool)v!),
            new("canWalkOnPoison", Kind.Bool, d => d.CanWalkOnPoison, (d, v) => d.CanWalkOnPoison = (bool)v!),
        };

        // Outfit table keys read by LuaScriptInterface::getOutfit (lookMount is ignored by 8.60 but harmless).
        private static readonly (string Key, Func<LuaMonsterData, uint> Get, Action<LuaMonsterData, uint> Set)[] OutfitFields =
        {
            ("lookType", d => d.LookType, (d, v) => d.LookType = v),
            ("lookTypeEx", d => d.LookTypeEx, (d, v) => d.LookTypeEx = v),
            ("lookHead", d => d.LookHead, (d, v) => d.LookHead = v),
            ("lookBody", d => d.LookBody, (d, v) => d.LookBody = v),
            ("lookLegs", d => d.LookLegs, (d, v) => d.LookLegs = v),
            ("lookFeet", d => d.LookFeet, (d, v) => d.LookFeet = v),
            ("lookAddons", d => d.LookAddons, (d, v) => d.LookAddons = v),
            ("lookMount", d => d.LookMount, (d, v) => d.LookMount = v),
        };

        // Every field key the patcher can write; anything else in the editor is not saved for revscript monsters.
        public static readonly HashSet<string> PatchableKeys =
            new(Fields.Select(f => f.Key).Concat(OutfitFields.Select(o => o.Key)));

        private static readonly Encoding Latin1 = Encoding.Latin1;

        // Races accepted by LuaScriptInterface::luaMonsterTypeRace.
        public static readonly string[] ValidRaces = { "venom", "blood", "undead", "fire", "energy" };

        public static bool IsRevscript(string lua) =>
            !Regex.IsMatch(lua, @"\bmonster\.(health|maxHealth|experience|outfit)\s*=");

        // Fills `d` from the file bytes. Sets d.ReadOnly (with a reason) when the file as a whole
        // can't be patched safely; otherwise d.Revscript is set and fields that can't be patched
        // are listed in d.Revscript.Locked.
        public static void Attach(LuaMonsterData d, byte[] bytes)
        {
            string text = Latin1.GetString(bytes);
            d.Revscript = null;
            d.ReadOnly = false;
            d.ReadOnlyReason = null;
            d.Loot.Clear();
            DetectGenerated(d, text);
            ApplyEngineDefaults(d);

            var creates = Regex.Matches(text, @"Game\.createMonsterType\(");
            var local = Regex.Match(text, @"(?m)^(?<ind>[ \t]*)local[ \t]+mType[ \t]*=[ \t]*Game\.createMonsterType\(\s*""(?<n>[^""\\\n]*)""\s*\)[ \t]*(?:--[^\n]*)?$");
            string? reason = null;
            if (creates.Count != 1) reason = $"the file defines {creates.Count} monster types";
            else if (!local.Success) reason = "the monster type is not held in `local mType = Game.createMonsterType(\"...\")`";
            else if (text.Contains('\r')) reason = "the file uses CRLF line endings";
            else if (text.Contains("--[[")) reason = "the file has block comments (--[[ ]])";

            if (reason != null)
            {
                // Read the values best-effort for display, but never write this file.
                FillValuesLoose(d, text);
                d.ReadOnly = true;
                d.ReadOnlyReason = reason;
                return;
            }

            var src = new RevscriptSource { Text = text, Indent = local.Groups["ind"].Value };
            d.Name = FromFile(local.Groups["n"].Value);

            foreach (var f in Fields)
                ParseScalar(d, src, f);
            ParseOutfit(d, src);
            src.ScalarInsertAt = FindScalarInsertPoint(text, local, src.Indent);
            ParseLoot(d, src, local);

            d.Revscript = src;
        }

        // MonsterInfo defaults (src/monsters.h), i.e. what the server uses when a call is missing.
        private static void ApplyEngineDefaults(LuaMonsterData d)
        {
            d.Description = null;
            d.Experience = 0; d.Health = 100; d.MaxHealth = 100;
            d.Race = "blood"; d.ManaCost = 0; d.Speed = 200; d.Corpse = 0;
            d.Attackable = true; d.Convinceable = false; d.Summonable = false; d.Illusionable = false;
            d.Hostile = true; d.HealthHidden = false; d.Pushable = true;
            d.CanPushItems = false; d.CanPushCreatures = false;
            d.TargetDistance = 1; d.RunHealth = 0; d.StaticAttackChance = 95;
            d.CanWalkOnEnergy = true; d.CanWalkOnFire = true; d.CanWalkOnPoison = true;
            d.LookType = 0; d.LookTypeEx = 0; d.LookHead = 0; d.LookBody = 0;
            d.LookLegs = 0; d.LookFeet = 0; d.LookAddons = 0; d.LookMount = 0;
        }

        private static void DetectGenerated(LuaMonsterData d, string text)
        {
            // Look at the leading comment header only.
            var lines = new List<string>();
            foreach (var line in text.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length == 0) continue;
                if (!t.StartsWith("--")) break;
                lines.Add(FromFile(t.TrimStart('-').Trim()));
            }
            var hit = lines.FirstOrDefault(l => Regex.IsMatch(l, @"\b(GENERATED|[Gg]enerated|[Cc]onverted from)\b"));
            d.IsGenerated = hit != null;
            d.GeneratedNote = hit == null ? null : string.Join("\n", lines);
        }

        private static void ParseScalar(LuaMonsterData d, RevscriptSource src, Field f)
        {
            string text = src.Text;
            var all = Regex.Matches(text, @"\bmType\s*:\s*" + f.Key + @"\s*\(");
            if (all.Count == 0)
            {
                src.Original[f.Key] = f.Get(d);   // not in the file: the model default stands, a call is inserted if changed
                return;
            }
            if (all.Count > 1)
            {
                Lock(d, src, f, $"mType:{f.Key}(...) appears {all.Count} times");
                return;
            }
            string arg = f.Kind switch
            {
                Kind.UInt => @"\d{1,10}",
                Kind.Bool => @"true|false",
                _ => @"""(?:[^""\\\n]|\\[""\\n])*""",
            };
            var m = Regex.Match(text, @"(?m)^[ \t]*mType:" + f.Key + @"\((?<a>" + arg + @")\)[ \t]*(?:--[^\n]*)?$");
            if (!m.Success || m.Groups["a"].Index < all[0].Index || m.Groups["a"].Index > all[0].Index + all[0].Length)
            {
                Lock(d, src, f, $"mType:{f.Key}(...) is not a plain literal");
                return;
            }
            string a = m.Groups["a"].Value;
            object? v;
            switch (f.Kind)
            {
                case Kind.UInt:
                    if (!uint.TryParse(a, out uint u)) { Lock(d, src, f, $"mType:{f.Key}({a}) is out of range"); return; }
                    v = u; break;
                case Kind.Bool: v = a == "true"; break;
                default: v = FromFile(Unescape(a.Substring(1, a.Length - 2))); break;
            }
            f.Set(d, v);
            src.Original[f.Key] = f.Get(d);
            src.Sites[f.Key] = (m.Groups["a"].Index, m.Groups["a"].Length);
        }

        private static void Lock(LuaMonsterData d, RevscriptSource src, Field f, string reason)
        {
            src.Locked[f.Key] = reason;
            // Show something sensible even though it can't be edited.
            var loose = Regex.Match(src.Text, @"\bmType:" + f.Key + @"\(\s*(""[^""\n]*""|\d+|true|false)\s*\)");
            if (loose.Success)
            {
                string a = loose.Groups[1].Value;
                if (f.Kind == Kind.UInt && uint.TryParse(a, out uint u)) f.Set(d, u);
                else if (f.Kind == Kind.Bool && (a == "true" || a == "false")) f.Set(d, a == "true");
                else if (f.Kind == Kind.Str && a.StartsWith('"')) f.Set(d, FromFile(a.Trim('"')));
            }
            src.Original[f.Key] = f.Get(d);
        }

        private static void ParseOutfit(LuaMonsterData d, RevscriptSource src)
        {
            string text = src.Text;
            void LockAll(string reason)
            {
                foreach (var o in OutfitFields) src.Locked[o.Key] = reason;
            }

            var all = Regex.Matches(text, @"\bmType\s*:\s*outfit\s*\(");
            if (all.Count > 1) { LockAll($"mType:outfit(...) appears {all.Count} times"); FillOutfitLoose(d, text); }
            else if (all.Count == 1)
            {
                var m = Regex.Match(text, @"(?m)^[ \t]*mType:outfit\((?<a>\{[^{}\n]*\})\)[ \t]*(?:--[^\n]*)?$");
                if (!m.Success)
                {
                    LockAll("mType:outfit(...) is not a plain table of numbers");
                    FillOutfitLoose(d, text);
                }
                else
                {
                    var a = m.Groups["a"];
                    string body = a.Value.Substring(1, a.Value.Length - 2);
                    const string pair = @"[ \t]*[A-Za-z_]\w*[ \t]*=[ \t]*\d{1,10}[ \t]*";
                    var pairs = Regex.Matches(body, @"(?<k>[A-Za-z_]\w*)[ \t]*=[ \t]*(?<v>\d{1,10})");
                    var keys = pairs.Select(p => p.Groups["k"].Value).ToList();
                    if (!Regex.IsMatch(body, @"^[ \t]*(?:" + pair + @"(?:," + pair + @")*,?[ \t]*)?$")
                        || keys.Distinct().Count() != keys.Count
                        || keys.Any(k => !OutfitFields.Any(o => o.Key == k))
                        || pairs.Any(p => !uint.TryParse(p.Groups["v"].Value, out _)))
                    {
                        LockAll("mType:outfit(...) has entries the editor doesn't understand");
                        FillOutfitLoose(d, text);
                    }
                    else
                    {
                        src.OutfitArgStart = a.Index;
                        src.OutfitArgLength = a.Length;
                        foreach (Match p in pairs)
                        {
                            var o = OutfitFields.First(x => x.Key == p.Groups["k"].Value);
                            o.Set(d, uint.Parse(p.Groups["v"].Value));
                            src.OutfitValues[o.Key] = (a.Index + 1 + p.Groups["v"].Index, p.Groups["v"].Length);
                        }
                    }
                }
            }
            foreach (var o in OutfitFields) src.Original[o.Key] = o.Get(d);
        }

        private static void FillOutfitLoose(LuaMonsterData d, string text)
        {
            var m = Regex.Match(text, @"mType:outfit\(\s*\{([^}]*)\}");
            if (!m.Success) return;
            foreach (var o in OutfitFields)
            {
                var v = Regex.Match(m.Groups[1].Value, @"\b" + o.Key + @"\s*=\s*(\d+)");
                if (v.Success && uint.TryParse(v.Groups[1].Value, out uint u)) o.Set(d, u);
            }
        }

        private static void FillValuesLoose(LuaMonsterData d, string text)
        {
            var n = Regex.Match(text, @"Game\.createMonsterType\(\s*""([^""]+)""");
            if (n.Success) d.Name = FromFile(n.Groups[1].Value);
            foreach (var f in Fields)
            {
                if (f.Key == "name") continue;
                var m = Regex.Match(text, @"\bmType:" + f.Key + @"\(\s*(""[^""\n]*""|\d+|true|false)\s*\)");
                if (!m.Success) continue;
                string a = m.Groups[1].Value;
                if (f.Kind == Kind.UInt && uint.TryParse(a, out uint u)) f.Set(d, u);
                else if (f.Kind == Kind.Bool && (a == "true" || a == "false")) f.Set(d, a == "true");
                else if (f.Kind == Kind.Str && a.StartsWith('"')) f.Set(d, FromFile(a.Trim('"')));
            }
            FillOutfitLoose(d, text);
        }

        // After the run of top-level mType:xxx(...) setter lines that follows createMonsterType
        // (stops at the first addVoice/addLoot/do-block/anything else).
        private static int FindScalarInsertPoint(string text, Match local, string indent)
        {
            int pos = LineEnd(text, local.Index);
            int insertAt = pos;
            var setter = new Regex(@"^" + Regex.Escape(indent) + @"mType:(?<m>\w+)\(");
            while (pos < text.Length)
            {
                int end = LineEnd(text, pos);
                string line = text.Substring(pos, end - pos).TrimEnd('\n');
                var m = setter.Match(line);
                if (m.Success)
                {
                    string meth = m.Groups["m"].Value;
                    if (meth.StartsWith("add") || meth.EndsWith("Immunities") || meth == "registerEvent" || meth == "register")
                        break;
                    insertAt = end;
                }
                else if (line.Trim().Length != 0 && !line.TrimStart().StartsWith("--"))
                    break;
                pos = end;
            }
            return insertAt;
        }

        // Offset just past the '\n' that ends the line containing `pos` (or text.Length).
        private static int LineEnd(string text, int pos)
        {
            int nl = text.IndexOf('\n', pos);
            return nl < 0 ? text.Length : nl + 1;
        }

        private static readonly Regex LootBlockRx = new(
            @"(?m)^(?<ind>[ \t]*)do[ \t]*\n(?<in>[ \t]+)local[ \t]+loot[ \t]*=[ \t]*Loot\(\)[ \t]*\n(?<body>(?:[ \t]*(?:loot:|--)[^\n]*\n)*)[ \t]*mType:addLoot\(loot\)[ \t]*\n\k<ind>end[ \t]*(?:\n|$)",
            RegexOptions.Compiled);
        private static readonly Regex LootLineRx = new(
            @"^[ \t]*loot:(?<m>\w+)\((?<a>[^()\n]*)\)(?<tail>[ \t]*(?:--[ \t]?(?<c>[^\n]*))?)$",
            RegexOptions.Compiled);

        private static void ParseLoot(LuaMonsterData d, RevscriptSource src, Match local)
        {
            string text = src.Text;
            int addCount = Regex.Matches(text, @"\bmType\s*:\s*addLoot\s*\(").Count;
            var blocks = LootBlockRx.Matches(text);
            if (blocks.Count != addCount)
                src.LootLockReason = $"{addCount - blocks.Count} of {addCount} mType:addLoot calls are not in the usual `do local loot = Loot() ... end` form";
            else if (text.Contains("addChildLoot"))
                src.LootLockReason = "the file uses container loot (addChildLoot)";

            foreach (Match b in blocks)
            {
                var e = ParseLootBlock(b, src);
                src.OriginalLoot.Add(e);
                d.Loot.Add(e);
            }

            if (blocks.Count > 0)
            {
                var last = blocks[blocks.Count - 1];
                src.LootInsertAt = last.Index + last.Length;
                src.LootIndent = last.Groups["ind"].Value;
                src.LootInnerIndent = last.Groups["in"].Value;
            }
            else
            {
                // No loot yet: put new blocks in front of the first do-block of the monster
                // (attacks), or after the setter calls when there is none.
                src.LootIndent = src.Indent;
                src.LootInnerIndent = src.Indent + "\t";
                var firstDo = new Regex(@"(?m)^" + Regex.Escape(src.Indent) + @"do[ \t]*$").Match(text, local.Index);
                src.LootInsertAt = firstDo.Success ? firstDo.Index : src.ScalarInsertAt;
            }
        }

        private static MonsterLootEntry ParseLootBlock(Match b, RevscriptSource src)
        {
            var body = b.Groups["body"];
            string? reason = null;
            var seen = new HashSet<string>();
            int id = -1, idArg = -1, idLineEnd = 0, chArg = -1, chLen = 0, maxArg = -1, maxLen = 0, chLineEnd = -1;
            uint chance = 0, max = 1;
            string comment = "";
            int pos = body.Index;
            while (pos < body.Index + body.Length)
            {
                int end = LineEnd(src.Text, pos);
                string line = src.Text.Substring(pos, end - pos - 1);
                if (line.TrimStart().StartsWith("--")) { pos = end; continue; }   // comment line, kept as is
                var m = LootLineRx.Match(line);
                if (!m.Success) { reason = "unrecognised line"; pos = end; continue; }
                string meth = m.Groups["m"].Value, a = m.Groups["a"].Value;
                if (!seen.Add(meth)) reason = $"loot:{meth} is called twice";
                int argAt = pos + m.Groups["a"].Index;
                switch (meth)
                {
                    case "setId":
                        if (!int.TryParse(a, out id) || id < 0 || id > ushort.MaxValue) { reason = $"loot:setId({a}) is not an item id"; break; }
                        idArg = argAt; idLineEnd = end - 1;
                        comment = FromFile(m.Groups["c"].Success ? m.Groups["c"].Value.TrimEnd() : "");
                        break;
                    case "setChance":
                        if (!uint.TryParse(a, out chance)) { reason = $"loot:setChance({a}) is not a number"; break; }
                        chArg = argAt; chLen = a.Length; chLineEnd = end;
                        break;
                    case "setMaxCount":
                        if (!uint.TryParse(a, out max)) { reason = $"loot:setMaxCount({a}) is not a number"; break; }
                        maxArg = argAt; maxLen = a.Length;
                        break;
                }
                pos = end;
            }
            if (reason == null && idArg < 0) reason = "no loot:setId";
            if (reason == null && chArg < 0) reason = "no loot:setChance";

            var e = new MonsterLootEntry
            {
                Locked = reason != null,
                LockReason = reason,
                Id = Math.Max(id, 0),
                Chance = chance,
                MaxCount = max,
                Comment = comment,
            };
            e.OriginalId = (uint)Math.Max(id, 0);
            e.OriginalChance = chance;
            e.OriginalMaxCount = max;
            e.OriginalComment = comment;
            e.BlockStart = b.Index;
            e.BlockLength = b.Length;
            e.IdArgStart = idArg;
            e.IdLineEnd = idLineEnd;
            e.ChanceArgStart = chArg;
            e.ChanceArgLength = chLen;
            e.MaxArgStart = maxArg;
            e.MaxArgLength = maxLen;
            e.MaxInsertAt = chLineEnd >= 0 ? chLineEnd : body.Index + body.Length;
            e.InnerIndent = b.Groups["in"].Value;
            return e;
        }

        // Builds the patched file text, or null when nothing the patcher handles has changed.
        // Throws InvalidOperationException for values that can't be written.
        public static string? BuildPatchedText(LuaMonsterData d)
        {
            var src = d.Revscript;
            if (src == null || d.ReadOnly) return null;
            string text = src.Text;
            var edits = new List<(int Start, int Length, string With)>();
            var inserts = new StringBuilder();

            foreach (var f in Fields)
            {
                if (src.IsLocked(f.Key)) continue;
                object? now = f.Get(d);
                if (SameValue(now, src.Original[f.Key])) continue;
                if (f.Key == "race" && !ValidRaces.Contains(now as string ?? ""))
                    throw new InvalidOperationException($"race \"{now}\" is not known to this server (use {string.Join(", ", ValidRaces)})");
                string lit = f.Kind switch
                {
                    Kind.UInt => ((uint)now!).ToString(),
                    Kind.Bool => (bool)now! ? "true" : "false",
                    _ => "\"" + ToFile((string?)now ?? "") + "\"",
                };
                if (src.Sites.TryGetValue(f.Key, out var site))
                    edits.Add((site.ArgStart, site.ArgLength, lit));
                else
                    inserts.Append(src.Indent).Append("mType:").Append(f.Key).Append('(').Append(lit).Append(")\n");
            }

            bool outfitLocked = OutfitFields.Any(o => src.IsLocked(o.Key));
            var changedOutfit = OutfitFields.Where(o => !outfitLocked && o.Get(d) != (uint)src.Original[o.Key]!).ToList();
            if (changedOutfit.Count > 0)
            {
                if (src.OutfitArgStart < 0)
                {
                    var parts = changedOutfit.Select(o => $"{o.Key} = {o.Get(d)}");
                    inserts.Append(src.Indent).Append("mType:outfit({").Append(string.Join(", ", parts)).Append("})\n");
                }
                else
                {
                    var added = new List<string>();
                    foreach (var o in changedOutfit)
                    {
                        if (src.OutfitValues.TryGetValue(o.Key, out var span))
                            edits.Add((span.Start, span.Length, o.Get(d).ToString()));
                        else
                            added.Add($"{o.Key} = {o.Get(d)}");
                    }
                    if (added.Count > 0)
                    {
                        int close = src.OutfitArgStart + src.OutfitArgLength - 1;   // the '}'
                        string inner = text.Substring(src.OutfitArgStart + 1, src.OutfitArgLength - 2);
                        string trimmed = inner.TrimEnd();
                        string add = string.Join(", ", added);
                        if (trimmed.Trim().Length == 0)
                            edits.Add((src.OutfitArgStart + 1, src.OutfitArgLength - 2, add));
                        else
                        {
                            int at = src.OutfitArgStart + 1 + trimmed.Length;
                            string sep = trimmed.EndsWith(",") ? " " : ", ";
                            edits.Add((at, close - at, sep + add + inner.Substring(trimmed.Length)));
                        }
                    }
                }
            }

            if (inserts.Length > 0)
                edits.Add((src.ScalarInsertAt, 0, AtLineStart(text, src.ScalarInsertAt, inserts.ToString())));

            if (!src.LootLocked)
                AddLootEdits(d, src, edits);

            if (edits.Count == 0) return null;

            // Apply back to front; insertions at the same offset keep their order.
            var ordered = edits.Select((e, i) => (e, i)).OrderByDescending(x => x.e.Start).ThenByDescending(x => x.i).ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                var hi = ordered[i - 1].e; var lo = ordered[i].e;
                if (lo.Start + lo.Length > hi.Start && !(lo.Length == 0 && hi.Length == 0))
                    throw new InvalidOperationException("overlapping edits (internal error) - nothing was written");
            }
            var sb = new StringBuilder(text);
            foreach (var (e, _) in ordered)
            {
                sb.Remove(e.Start, e.Length);
                sb.Insert(e.Start, e.With);
            }
            return sb.ToString();
        }

        private static void AddLootEdits(LuaMonsterData d, RevscriptSource src, List<(int, int, string)> edits)
        {
            var current = new HashSet<MonsterLootEntry>(d.Loot);
            foreach (var e in src.OriginalLoot)
            {
                if (!current.Contains(e))
                {
                    edits.Add((e.BlockStart, e.BlockLength, ""));
                    continue;
                }
                if (e.Locked) continue;
                uint id = ToUInt(e.Id, ushort.MaxValue, "loot item id");
                uint chance = ToUInt(e.Chance, uint.MaxValue, "loot chance");
                uint max = ToUInt(e.MaxCount, uint.MaxValue, "loot max count");
                bool idChanged = id != e.OriginalId;
                bool commentChanged = e.Comment != e.OriginalComment;
                if (idChanged || commentChanged)
                {
                    // A changed id makes the old "-- item name" comment wrong, so it's dropped
                    // unless the user typed a new one.
                    string c = commentChanged ? e.Comment.Trim() : "";
                    edits.Add((e.IdArgStart, e.IdLineEnd - e.IdArgStart, id + ")" + (c.Length > 0 ? " -- " + CommentToFile(c) : "")));
                }
                if (chance != e.OriginalChance)
                    edits.Add((e.ChanceArgStart, e.ChanceArgLength, chance.ToString()));
                if (max != e.OriginalMaxCount)
                {
                    if (e.MaxArgStart >= 0) edits.Add((e.MaxArgStart, e.MaxArgLength, max.ToString()));
                    else edits.Add((e.MaxInsertAt, 0, $"{e.InnerIndent}loot:setMaxCount({max})\n"));
                }
            }

            var added = new StringBuilder();
            foreach (var e in d.Loot.Where(x => x.IsNew))
            {
                uint id = ToUInt(e.Id, ushort.MaxValue, "loot item id");
                uint chance = ToUInt(e.Chance, uint.MaxValue, "loot chance");
                uint max = ToUInt(e.MaxCount, uint.MaxValue, "loot max count");
                if (id == 0) throw new InvalidOperationException("a new loot entry has item id 0");
                string ind = src.LootIndent, inn = src.LootInnerIndent;
                string c = e.Comment.Trim();
                added.Append(ind).Append("do\n");
                added.Append(inn).Append("local loot = Loot()\n");
                added.Append(inn).Append("loot:setId(").Append(id).Append(')').Append(c.Length > 0 ? " -- " + CommentToFile(c) : "").Append('\n');
                added.Append(inn).Append("loot:setChance(").Append(chance).Append(")\n");
                if (max > 1) added.Append(inn).Append("loot:setMaxCount(").Append(max).Append(")\n");
                added.Append(inn).Append("mType:addLoot(loot)\n");
                added.Append(ind).Append("end\n");
            }
            if (added.Length > 0)
                edits.Add((src.LootInsertAt, 0, AtLineStart(src.Text, src.LootInsertAt, added.ToString())));
        }

        // Insertions are whole lines ending in '\n'; at the end of a file without a final newline
        // the newline moves to the front so the last original line stays intact.
        private static string AtLineStart(string text, int at, string lines)
        {
            if (at == text.Length && text.Length > 0 && text[^1] != '\n')
                return "\n" + lines.TrimEnd('\n');
            return lines;
        }

        private static uint ToUInt(decimal v, uint max, string what)
        {
            if (v < 0 || v > max || v != decimal.Truncate(v))
                throw new InvalidOperationException($"{what} {v} is out of range (0..{max})");
            return (uint)v;
        }

        private static bool SameValue(object? a, object? b)
        {
            if (a is string || b is string || a == null || b == null)
                return (a as string ?? "") == (b as string ?? "");
            return a.Equals(b);
        }

        // The file as it would be written (or as it is, when nothing changed), for the preview tab.
        public static string PreviewText(LuaMonsterData d)
        {
            if (d.Revscript == null) return "";
            string text = BuildPatchedText(d) ?? d.Revscript.Text;
            return Encoding.UTF8.GetString(Latin1.GetBytes(text));
        }

        public static bool HasPendingChanges(LuaMonsterData d)
        {
            try { return BuildPatchedText(d) != null; }
            catch (InvalidOperationException) { return true; }
        }

        // Writes the patched text atomically (temp file + rename). The first save also keeps the
        // untouched original as "<file>-bak". Returns false and leaves the file alone on any error.
        public static bool Save(LuaMonsterData d, string path, out bool changed, out string? error)
        {
            changed = false;
            error = null;
            string? patched;
            try { patched = BuildPatchedText(d); }
            catch (InvalidOperationException ex) { error = ex.Message; return false; }
            if (patched == null) return true;

            string? tmp = null;
            try
            {
                byte[] onDisk = File.ReadAllBytes(path);
                if (!onDisk.AsSpan().SequenceEqual(Latin1.GetBytes(d.Revscript!.Text)))
                {
                    error = "the file changed on disk since it was loaded - reload the folder first";
                    return false;
                }
                string bak = path + "-bak";
                if (!File.Exists(bak)) File.Copy(path, bak);

                byte[] bytes = Latin1.GetBytes(patched);
                tmp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".tmp");
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, path, true);
                tmp = null;
                changed = true;

                // Re-read so spans and "original" values describe the new text.
                Attach(d, bytes);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (tmp != null) try { File.Delete(tmp); } catch { }
            }
        }

        // File text is Latin-1 decoded bytes; strings shown to the user are UTF-8.
        private static string FromFile(string s) => Encoding.UTF8.GetString(Latin1.GetBytes(s));
        // Inverse of ToFile's escaping; the parse regex only admits \" \\ and \n.
        private static string Unescape(string s) =>
            Regex.Replace(s, @"\\([""\\n])", m => m.Groups[1].Value == "n" ? "\n" : m.Groups[1].Value);

        private static string CommentToFile(string s) =>
            Latin1.GetString(Encoding.UTF8.GetBytes(s.Replace("\r", " ").Replace("\n", " ")));
        private static string ToFile(string s) =>
            Latin1.GetString(Encoding.UTF8.GetBytes(s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "")));
    }
}
