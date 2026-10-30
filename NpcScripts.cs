using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Assets_Editor
{
    /// <summary>
    /// TFS 1.x revscript NPCs (<c>local npcType = NpcType("Name")</c>, NpcSystem keyword handlers, shops in
    /// <c>npcType:parameters</c>). Only values that are plain literals are editable; each one remembers where it
    /// sits in the file, and saving replaces just the changed ones, so the rest of the script stays byte-identical.
    /// Shops built by code (ShopModule calls, function results) and texts built by concatenation stay read-only.
    /// </summary>
    public class NpcScript
    {
        // ISO-8859-1 maps every byte to one char and back, so unknown bytes survive a round trip
        public static readonly Encoding FileEncoding = Encoding.Latin1;

        public string Path { get; }
        public string Text { get; private set; }
        public string Name { get; private set; } = "";
        public List<LuaToken> Outfit { get; } = new();
        public LuaToken? HealthNow { get; private set; }
        public LuaToken? HealthMax { get; private set; }
        public LuaToken? WalkInterval { get; private set; }
        public LuaToken? WalkRadius { get; private set; }
        public List<NpcMessage> Messages { get; } = new();
        public List<NpcDialog> Dialogs { get; } = new();
        public NpcShop? Buyable { get; private set; }
        public NpcShop? Sellable { get; private set; }
        /// <summary>Why the shop cannot be edited here (built by code), or null.</summary>
        public string? ShopNote { get; private set; }

        private byte[] _loadedHash;

        public NpcScript(string path, string text)
        {
            Path = path;
            Text = text;
            _loadedHash = Hash(text);
            Parse();
        }

        public static NpcScript Load(string path) => new(path, File.ReadAllText(path, FileEncoding));

        public bool IsNpc => Name.Length > 0;
        public bool HasShop => Buyable != null || Sellable != null;

        public bool Modified =>
            AllTokens().Any(t => t.Modified) || (Buyable?.Modified ?? false) || (Sellable?.Modified ?? false);

        public int OutfitValue(string key) =>
            Outfit.FirstOrDefault(t => t.Key == key)?.IntValue ?? 0;

        private IEnumerable<LuaToken> AllTokens()
        {
            foreach (var t in Outfit) yield return t;
            foreach (var t in new[] { HealthNow, HealthMax, WalkInterval, WalkRadius })
                if (t != null) yield return t;
            foreach (var m in Messages) yield return m.Token;
            foreach (var d in Dialogs) yield return d.Token;
        }

        // ── parsing ───────────────────────────────────────────────────────

        private static readonly Regex NpcTypeRx = new(@"NpcType\(\s*(?<q>[""'])(?<name>(?:\\.|(?!\k<q>).)*)\k<q>\s*\)");
        private static readonly Regex OutfitRx = new(@"npcType:outfit\(\s*\{(?<body>[^}]*)\}");
        private static readonly Regex OutfitPairRx = new(@"(?<key>look[A-Za-z]+)\s*=\s*(?<value>-?\d+)");
        private static readonly Regex HealthRx = new(@"npcType:health\(\s*(?<now>\d+)\s*,\s*(?<max>\d+)\s*\)");
        private static readonly Regex WalkIntervalRx = new(@"npcType:walkInterval\(\s*(?<v>\d+)\s*\)");
        private static readonly Regex WalkRadiusRx = new(@"npcType:walkRadius\(\s*(?<v>\d+)\s*\)");
        private static readonly Regex MessageRx = new(@"setMessage\(\s*(?<kind>MESSAGE_[A-Z_]+)\s*,\s*");
        private static readonly Regex KeywordRx = new(@"(?:addKeyword|\bsay)\(\s*\{(?<keys>[^}]*)\}\s*,\s*");
        private static readonly Regex ShopParamRx = new(@"\[\s*[""']shop_(?<kind>buyable|sellable)[""']\s*\]\s*=\s*");
        private static readonly Regex ShopCallRx = new(@"^(?<fn>[A-Za-z_][\w.]*)\(\s*(?<table>[A-Za-z_]\w*)\s*\)");

        private void Parse()
        {
            Match m = NpcTypeRx.Match(Text);
            if (!m.Success)
                return;
            Name = LuaString.Unescape(m.Groups["name"].Value);

            Match outfit = OutfitRx.Match(Text);
            if (outfit.Success)
                foreach (Match pair in OutfitPairRx.Matches(outfit.Groups["body"].Value))
                {
                    Group value = pair.Groups["value"];
                    Outfit.Add(new LuaToken(outfit.Groups["body"].Index + value.Index, value.Value, LuaTokenKind.Number, pair.Groups["key"].Value));
                }

            if (HealthRx.Match(Text) is { Success: true } health)
            {
                HealthNow = Number(health.Groups["now"], "health");
                HealthMax = Number(health.Groups["max"], "maxHealth");
            }
            if (WalkIntervalRx.Match(Text) is { Success: true } interval)
                WalkInterval = Number(interval.Groups["v"], "walkInterval");
            if (WalkRadiusRx.Match(Text) is { Success: true } radius)
                WalkRadius = Number(radius.Groups["v"], "walkRadius");

            foreach (Match msg in MessageRx.Matches(Text))
                if (!IsInComment(msg.Index) && LuaString.At(Text, msg.Index + msg.Length) is { } token)
                    Messages.Add(new NpcMessage(msg.Groups["kind"].Value, token));

            foreach (Match kw in KeywordRx.Matches(Text))
            {
                if (IsInComment(kw.Index))
                    continue;
                var keywords = LuaString.All(kw.Groups["keys"].Value).Select(LuaString.Unescape).ToList();
                int after = kw.Index + kw.Length;
                LuaToken? text = kw.Value.StartsWith("say") ? LuaString.At(Text, after) : DialogText(after);
                if (text != null && keywords.Count > 0)
                    Dialogs.Add(new NpcDialog(keywords, text, LineOf(kw.Index)));
            }

            foreach (Match shop in ShopParamRx.Matches(Text))
            {
                if (IsInComment(shop.Index))
                    continue;
                int at = shop.Index + shop.Length;
                NpcShop? parsed = LuaString.At(Text, at) is { } literal
                    ? NpcShop.FromString(literal)
                    : ShopCallRx.Match(Text[at..Math.Min(Text.Length, at + 200)]) is { Success: true } call
                        ? NpcShop.FromTable(Text, call.Groups["table"].Value)
                        : null;
                if (parsed == null)
                {
                    ShopNote = "The shop is built by script code; edit the .lua file.";
                    continue;
                }
                if (shop.Groups["kind"].Value == "buyable") Buyable = parsed;
                else Sellable = parsed;
            }
            if (!HasShop && ShopNote == null && Text.Contains("ShopModule"))
                ShopNote = "The shop is built with ShopModule calls; edit the .lua file.";
        }

        private LuaToken Number(Group g, string key) => new(g.Index, g.Value, LuaTokenKind.Number, key);

        // addKeyword(keys, X.say, { ..., text = "..." }): the text is the first `text =` inside the parameter table
        private LuaToken? DialogText(int at)
        {
            Match say = Regex.Match(Text[at..Math.Min(Text.Length, at + 200)], @"^[A-Za-z_][\w]*\.say\s*,\s*\{");
            if (!say.Success)
                return null;
            int i = at + say.Length, depth = 1;
            while (i < Text.Length && depth > 0)
            {
                char c = Text[i];
                if (c == '"' || c == '\'')
                {
                    i = LuaString.End(Text, i);
                    if (i < 0) return null;
                    continue;
                }
                if (c == '{') depth++;
                else if (c == '}') depth--;
                else if (depth == 1 && Regex.Match(Text[i..Math.Min(Text.Length, i + 12)], @"^text\s*=\s*") is { Success: true } key
                         && (i == 0 || !char.IsLetterOrDigit(Text[i - 1]) && Text[i - 1] != '_'))
                    return LuaString.At(Text, i + key.Length);
                i++;
            }
            return null;
        }

        private bool IsInComment(int index)
        {
            int lineStart = Text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
            return Text.AsSpan(lineStart, index - lineStart).Contains("--", StringComparison.Ordinal);
        }

        private int LineOf(int index) => Text.AsSpan(0, index).Count('\n') + 1;

        // ── saving ────────────────────────────────────────────────────────

        /// <summary>The script with every edited value written back; untouched text is kept as is.</summary>
        public string BuildText()
        {
            var edits = AllTokens().Where(t => t.Modified).Select(t => (t.Start, t.Length, t.Source))
                .Concat(Buyable?.Edits() ?? [])
                .Concat(Sellable?.Edits() ?? [])
                .OrderByDescending(e => e.Start).ToList();
            var sb = new StringBuilder(Text);
            int previous = int.MaxValue;
            foreach (var (start, length, replacement) in edits)
            {
                if (start + length > previous)
                    throw new InvalidOperationException($"{Name}: overlapping edits");
                sb.Remove(start, length).Insert(start, replacement);
                previous = start;
            }
            return sb.ToString();
        }

        /// <summary>True when the file still holds what was loaded (nobody else changed it meanwhile).</summary>
        public bool FileUnchanged() =>
            File.Exists(Path) && Hash(File.ReadAllText(Path, FileEncoding)).AsSpan().SequenceEqual(_loadedHash);

        /// <summary>Writes the script (previous version kept as "&lt;file&gt;-bak", which TFS does not load) and
        /// re-reads it so the edit spans match the new text.</summary>
        public void Save()
        {
            if (!FileUnchanged())
                throw new IOException($"{System.IO.Path.GetFileName(Path)} was changed on disk after it was loaded; reload it first.");
            string text = BuildText();
            File.Copy(Path, Path + "-bak", overwrite: true);
            string temp = Path + ".tmp";
            File.WriteAllText(temp, text, FileEncoding);
            File.Move(temp, Path, overwrite: true);
            Reload(text);
        }

        private void Reload(string text)
        {
            Text = text;
            _loadedHash = Hash(text);
            Outfit.Clear();
            Messages.Clear();
            Dialogs.Clear();
            HealthNow = HealthMax = WalkInterval = WalkRadius = null;
            Buyable = Sellable = null;
            ShopNote = null;
            Parse();
        }

        private static byte[] Hash(string text) => SHA256.HashData(FileEncoding.GetBytes(text));
    }

    public enum LuaTokenKind { Number, String }

    /// <summary>A literal in the script: its place in the file and its (possibly edited) source text.</summary>
    public class LuaToken
    {
        public LuaToken(int start, string source, LuaTokenKind kind, string key = "", char quote = '"')
        {
            Start = start;
            Length = source.Length;
            Original = Source = source;
            Kind = kind;
            Key = key;
            Quote = quote;
        }

        public int Start { get; }
        public int Length { get; }
        public string Original { get; }
        /// <summary>The literal as written in the file, quotes and escapes included.</summary>
        public string Source { get; private set; }
        public LuaTokenKind Kind { get; }
        public string Key { get; }
        public char Quote { get; }
        public bool Modified => Source != Original;

        public int IntValue
        {
            get => int.TryParse(Source, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
            set => Source = value.ToString(CultureInfo.InvariantCulture);
        }

        public string StringValue
        {
            get => LuaString.Unescape(Source[1..^1]);
            set
            {
                // keep the original spelling (escapes) while the text is unchanged
                Source = value == LuaString.Unescape(Original[1..^1]) ? Original : LuaString.Quote(value, Quote);
            }
        }
    }

    public record NpcMessage(string Kind, LuaToken Token);

    public record NpcDialog(List<string> Keywords, LuaToken Token, int Line);

    public class ShopItem
    {
        public string Name { get; set; } = "";
        public int ItemId { get; set; }
        public int Price { get; set; }
        /// <summary>Fields after the price (charges / subtype, real name), kept as written.</summary>
        public string Extra { get; set; } = "";

        public ShopItem Clone() => (ShopItem)MemberwiseClone();
        public bool SameAs(ShopItem o) => Name == o.Name && ItemId == o.ItemId && Price == o.Price && Extra == o.Extra;
    }

    /// <summary>
    /// One shop list: either the NpcSystem string "name,id,price[,extra];…" or a Lua table of
    /// {"name", id, price} rows passed through a helper (e.g. <c>shopList(SELLS)</c>).
    /// </summary>
    public class NpcShop
    {
        public List<ShopItem> Items { get; } = new();
        public string? TableName { get; private set; }

        private List<ShopItem> _original = new();
        private LuaToken? _string;
        private int _tableStart, _tableLength;
        private string _tableIndent = "\t";
        private string _tableHeader = "";
        private string _tableFooter = "";
        private string _tableNewline = "\n";

        public bool Modified =>
            Items.Count != _original.Count || Items.Zip(_original).Any(p => !p.First.SameAs(p.Second));

        public static NpcShop? FromString(LuaToken literal)
        {
            var shop = new NpcShop { _string = literal };
            foreach (string entry in literal.StringValue.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] f = entry.Split(',');
                if (f.Length < 3 || !int.TryParse(f[1].Trim(), out int id) || !int.TryParse(f[2].Trim(), out int price))
                    return null;
                shop.Items.Add(new ShopItem { Name = f[0], ItemId = id, Price = price, Extra = string.Join(",", f.Skip(3)) });
            }
            shop.Snapshot();
            return shop;
        }

        private static readonly Regex TableRowRx = new(@"\{\s*(?<q>[""'])(?<name>(?:\\.|(?!\k<q>).)*)\k<q>\s*,\s*(?<id>\d+)\s*,\s*(?<price>\d+)\s*(?:,\s*(?<extra>[^}]*?))?\s*\}");

        public static NpcShop? FromTable(string text, string table)
        {
            Match head = Regex.Match(text, @"local\s+" + Regex.Escape(table) + @"\s*=\s*\{");
            if (!head.Success)
                return null;
            int open = head.Index + head.Length - 1, close = MatchingBrace(text, open);
            if (close < 0)
                return null;
            var shop = new NpcShop { TableName = table, _tableStart = open + 1, _tableLength = close - open - 1 };
            string body = text.Substring(shop._tableStart, shop._tableLength);
            int firstNewline = body.IndexOf('\n');
            shop._tableHeader = firstNewline >= 0 ? body[..(firstNewline + 1)] : "";
            int lastNewline = body.LastIndexOf('\n');
            shop._tableFooter = lastNewline > firstNewline ? body[(lastNewline + 1)..] : "";
            shop._tableNewline = body.Contains("\r\n") ? "\r\n" : "\n";
            foreach (Match row in TableRowRx.Matches(body))
            {
                shop.Items.Add(new ShopItem
                {
                    Name = LuaString.Unescape(row.Groups["name"].Value),
                    ItemId = int.Parse(row.Groups["id"].Value),
                    Price = int.Parse(row.Groups["price"].Value),
                    Extra = row.Groups["extra"].Value,
                });
                if (shop.Items.Count == 1)
                {
                    int lineStart = body.LastIndexOf('\n', row.Index) + 1;
                    shop._tableIndent = body[lineStart..row.Index];
                }
            }
            shop.Snapshot();
            return shop;
        }

        private void Snapshot() => _original = Items.Select(i => i.Clone()).ToList();

        internal IEnumerable<(int Start, int Length, string Text)> Edits()
        {
            if (!Modified)
                yield break;
            if (_string != null)
            {
                _string.StringValue = string.Join(";", Items.Select(i =>
                    $"{i.Name},{i.ItemId},{i.Price}" + (i.Extra.Length > 0 ? "," + i.Extra : "")));
                yield return (_string.Start, _string.Length, _string.Source);
            }
            else
            {
                var sb = new StringBuilder(_tableHeader);
                foreach (ShopItem i in Items)
                    sb.Append(_tableIndent).Append('{').Append(LuaString.Quote(i.Name, '"')).Append(", ")
                      .Append(i.ItemId).Append(", ").Append(i.Price)
                      .Append(i.Extra.Length > 0 ? ", " + i.Extra : "").Append("},").Append(_tableNewline);
                sb.Append(_tableFooter);
                yield return (_tableStart, _tableLength, sb.ToString());
            }
        }

        private static int MatchingBrace(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' || c == '\'')
                {
                    i = LuaString.End(text, i) - 1;
                    if (i < 0) return -1;
                }
                else if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
                    i = text.IndexOf('\n', i) is var nl && nl < 0 ? text.Length : nl;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return i;
            }
            return -1;
        }
    }

    /// <summary>Lua short strings ("..." / '...'): finding, unescaping and quoting.</summary>
    public static class LuaString
    {
        /// <summary>The string literal starting at <paramref name="index"/> (after optional spaces), or null.</summary>
        public static LuaToken? At(string text, int index)
        {
            while (index < text.Length && (text[index] == ' ' || text[index] == '\t'))
                index++;
            if (index >= text.Length || (text[index] != '"' && text[index] != '\''))
                return null;
            int end = End(text, index);
            if (end < 0)
                return null;
            // a literal followed by ".." is part of a concatenation: not a plain value
            int next = end;
            while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
            if (next + 1 < text.Length && text[next] == '.' && text[next + 1] == '.')
                return null;
            return new LuaToken(index, text[index..end], LuaTokenKind.String, quote: text[index]);
        }

        /// <summary>Index just past the closing quote of the literal at <paramref name="start"/>, or -1.</summary>
        public static int End(string text, int start)
        {
            char quote = text[start];
            for (int i = start + 1; i < text.Length; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '\n') return -1;
                if (text[i] == quote) return i + 1;
            }
            return -1;
        }

        public static IEnumerable<string> All(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '"' || text[i] == '\'')
                {
                    int end = End(text, i);
                    if (end < 0) yield break;
                    yield return text[(i + 1)..(end - 1)];
                    i = end - 1;
                }
        }

        public static string Unescape(string s)
        {
            if (!s.Contains('\\'))
                return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char c = s[++i];
                switch (c)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'a': sb.Append('\a'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'v': sb.Append('\v'); break;
                    default:
                        if (char.IsDigit(c))
                        {
                            int j = i, value = 0;
                            while (j < s.Length && j < i + 3 && char.IsDigit(s[j])) value = value * 10 + (s[j++] - '0');
                            sb.Append((char)value);
                            i = j - 1;
                        }
                        else sb.Append(c); // \\ \" \' and anything else
                        break;
                }
            }
            return sb.ToString();
        }

        public static string Quote(string value, char quote)
        {
            var sb = new StringBuilder(value.Length + 2).Append(quote);
            foreach (char c in value)
            {
                if (c == '\\' || c == quote) sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            return sb.Append(quote).ToString();
        }
    }

    /// <summary>All NPC scripts of a folder (recursive) and the shop checks across them.</summary>
    public static class NpcRegistry
    {
        public static List<NpcScript> Scripts { get; private set; } = new();
        public static string? Folder { get; private set; }

        public static int LoadFolder(string folder)
        {
            var list = new List<NpcScript>();
            // exact extension: on Windows "*.lua" also matches the "-bak" copies ("x.lua-bak")
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    var script = NpcScript.Load(file);
                    if (script.IsNpc) list.Add(script);
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"NPC {file}: {ex.Message}", "Warning");
                }
            }
            Scripts = list;
            Folder = folder;
            return list.Count;
        }

        /// <summary>
        /// items.xml names by id, from the data/items/items.xml above <paramref name="folder"/> (an NPC folder sits in
        /// data/scripts/npcs or data/npc); null when there is none.
        /// </summary>
        public static Dictionary<int, string>? LoadItemNames(string folder, out string? path)
        {
            path = null;
            for (var dir = new DirectoryInfo(folder); dir != null; dir = dir.Parent)
            {
                string candidate = System.IO.Path.Combine(dir.FullName, "items", "items.xml");
                if (File.Exists(candidate)) { path = candidate; break; }
            }
            if (path == null)
                return null;
            var names = new Dictionary<int, string>();
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore };
            using var reader = System.Xml.XmlReader.Create(path, settings);
            while (reader.Read())
            {
                if (reader.NodeType != System.Xml.XmlNodeType.Element || reader.Name != "item" || reader.GetAttribute("name") is not { } name)
                    continue;
                if (int.TryParse(reader.GetAttribute("id"), out int id))
                    names[id] = name;
                else if (int.TryParse(reader.GetAttribute("fromid"), out int from) && int.TryParse(reader.GetAttribute("toid"), out int to))
                    for (int i = from; i <= to && i - from < 100000; i++) names[i] = name;
            }
            return names;
        }

        public record Finding(string Category, int ItemId, string Item, string Details, string Npcs);

        /// <summary>
        /// Shop problems across all NPCs: items one NPC sells for less than another buys them for, the same item at
        /// different prices, and ids/names that do not match the client or items.xml (when given).
        /// </summary>
        public static List<Finding> CheckShops(IReadOnlyList<NpcScript> scripts,
            Func<int, bool>? itemExists = null, Func<int, string?>? itemName = null)
        {
            var findings = new List<Finding>();
            var sold = new Dictionary<int, List<(NpcScript Npc, ShopItem Item)>>();   // player buys from the NPC
            var bought = new Dictionary<int, List<(NpcScript Npc, ShopItem Item)>>(); // player sells to the NPC
            foreach (var npc in scripts)
            {
                foreach (var item in npc.Buyable?.Items ?? [])
                    (sold.TryGetValue(item.ItemId, out var l) ? l : sold[item.ItemId] = new()).Add((npc, item));
                foreach (var item in npc.Sellable?.Items ?? [])
                    (bought.TryGetValue(item.ItemId, out var l) ? l : bought[item.ItemId] = new()).Add((npc, item));
            }

            static string Names(IEnumerable<(NpcScript Npc, ShopItem Item)> rows) =>
                string.Join(", ", rows.Select(r => $"{r.Npc.Name} ({r.Item.Price})").Distinct());

            foreach (var (id, sellers) in sold)
            {
                var cheapest = sellers.MinBy(s => s.Item.Price);
                if (bought.TryGetValue(id, out var buyers) && buyers.MaxBy(b => b.Item.Price) is var best && best.Item.Price > cheapest.Item.Price)
                    findings.Add(new Finding("Free gold", id, cheapest.Item.Name,
                        $"buy for {cheapest.Item.Price} from {cheapest.Npc.Name}, sell for {best.Item.Price} to {best.Npc.Name}: +{best.Item.Price - cheapest.Item.Price} each",
                        Names(sellers.Concat(buyers))));
            }

            void Spread(Dictionary<int, List<(NpcScript Npc, ShopItem Item)>> rows, string what)
            {
                foreach (var (id, list) in rows)
                {
                    int min = list.Min(r => r.Item.Price), max = list.Max(r => r.Item.Price);
                    if (min != max)
                        findings.Add(new Finding("Different prices", id, list[0].Item.Name, $"{what} for {min} – {max}", Names(list.OrderBy(r => r.Item.Price))));
                }
            }
            Spread(sold, "NPCs sell it");
            Spread(bought, "NPCs buy it");

            foreach (var (id, list) in sold.Concat(bought).GroupBy(kv => kv.Key).Select(g => (g.Key, g.SelectMany(kv => kv.Value).ToList())))
            {
                if (itemExists != null && !itemExists(id))
                    findings.Add(new Finding("Unknown item id", id, list[0].Item.Name, "not in the loaded client", Names(list)));
                else if (itemName?.Invoke(id) is { } name)
                {
                    var wrong = list.Where(r => !string.Equals(r.Item.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (wrong.Count > 0)
                        findings.Add(new Finding("Name differs from items.xml", id, name,
                            "shop says " + string.Join(" / ", wrong.Select(r => $"\"{r.Item.Name}\"").Distinct()), Names(wrong)));
                }
            }

            return findings.OrderBy(f => f.Category switch { "Free gold" => 0, "Unknown item id" => 1, "Different prices" => 2, _ => 3 })
                .ThenBy(f => f.ItemId).ToList();
        }
    }
}
