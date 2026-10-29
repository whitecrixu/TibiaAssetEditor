using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using Tibia.Protobuf.Appearances;
using static Assets_Editor.OTB;

namespace Assets_Editor
{
    // What the checker needs from a client's things, copied out of the (live, editable) Appearances on the UI
    // thread so the report can run on a background thread.
    public sealed class ThingsSnapshot
    {
        public sealed class ItemInfo
        {
            public string StackClass = "common";   // ground / border / bottom / top / common (items_otb.py CLASSES)
            public bool MultiUse;                   // dat attribute 7 "Use with ..."
            public bool ForceUse;
            public bool Empty;                      // every sprite id 0
        }

        public sealed class OutfitInfo
        {
            public uint PatternZ;                   // first frame group; 2 = has the mounted (riding) layer
            public bool Empty;
        }

        public Dictionary<uint, ItemInfo> Items { get; } = [];
        public Dictionary<uint, OutfitInfo> Outfits { get; } = [];
        public uint LastItemId { get; private set; }
        public uint LastOutfitId { get; private set; }

        public static ThingsSnapshot From(Appearances appearances)
        {
            var s = new ThingsSnapshot();
            foreach (Appearance a in appearances.Object)
            {
                var f = a.Flags;
                // items_otb.py dat_items: the first of attribute 0 ground, 1 border (clip), 2 bottom, 3 top
                string cls = f == null ? "common"
                    : f.Bank != null ? "ground"
                    : f.Clip ? "border"
                    : f.Bottom ? "bottom"
                    : f.Top ? "top"
                    : "common";
                s.Items[a.Id] = new ItemInfo { StackClass = cls, MultiUse = f?.Multiuse == true, ForceUse = f?.Forceuse == true, Empty = IsEmpty(a) };
                s.LastItemId = Math.Max(s.LastItemId, a.Id);
            }
            foreach (Appearance a in appearances.Outfit)
            {
                uint pz = a.FrameGroup.Count > 0 && a.FrameGroup[0].SpriteInfo != null ? a.FrameGroup[0].SpriteInfo.PatternZ : 1;
                s.Outfits[a.Id] = new OutfitInfo { PatternZ = pz, Empty = IsEmpty(a) };
                s.LastOutfitId = Math.Max(s.LastOutfitId, a.Id);
            }
            return s;
        }

        private static bool IsEmpty(Appearance a)
        {
            foreach (FrameGroup g in a.FrameGroup)
                if (g.SpriteInfo != null)
                    foreach (uint id in g.SpriteInfo.SpriteId)
                        if (id != 0) return false;
            return true;
        }
    }

    public sealed class ConsistencyInput
    {
        public ThingsSnapshot Things = new();
        public string? ServerRoot;      // directory that contains data/
        public string? OtbPath;         // default <root>/data/items/items.otb
        public string? ItemsXmlPath;    // default <root>/data/items/items.xml
        public string? OutfitsXmlPath;  // default <root>/data/XML/outfits.xml
        public string? MonsterDir;      // default <root>/data/monster

        public string? Resolve(string? explicitPath, params string[] relative) =>
            explicitPath ?? (ServerRoot == null ? null : Path.Combine([ServerRoot, .. relative]));
    }

    public static class ConsistencyChecker
    {
        public const string CatStack = "dat-otb stack";
        public const string CatUse = "dat-otb use";
        public const string CatForceUse = "dat-otb forceuse";
        public const string CatOtbIds = "otb ids";
        public const string CatDatOnly = "dat without otb";
        public const string CatItemsXml = "items.xml";
        public const string CatMonsters = "monsters";
        public const string CatOutfits = "outfits.xml";
        public const string CatMounts = "outfits mount";
        public const string CatFiles = "files";

        // Allow-lists copied EXACTLY from TFS-1.5-Downgrades-8.60/tools/items_otb/items_otb.py (KNOWN, KNOWN_USE).
        // The dat<->otb stack/use check below is a port of that script's `check --dat` (mismatches() and
        // use_mismatches()): same keys (the node's server id, looked up as a client id - the server keeps them equal),
        // same skips (no server id, group 14 deprecated, id not in the dat) and the same classes. Keep the two in sync.
        public static readonly IReadOnlyDictionary<uint, string> KnownStack = new Dictionary<uint, string>
        {
            [44563] = "dat common, otb bottom (Crystal: bottom); 12 placements on crystal.otbm, each only ground + this item, so both orders draw the same",
            [51751] = "dat ground, otb common (Crystal: a pickupable item, not ground) - the client dat is wrong; never on the map",
            [51752] = "as 51751", [51753] = "as 51751", [51754] = "as 51751", [51755] = "as 51751",
            [51978] = "bounty talisman: dat ground, otb common (Crystal: pickupable) - client dat wrong; never on the map",
            [53810] = "dat bottom, otb common; newer than Crystal's appearances, never on the map",
            [53811] = "as 53810", [53816] = "as 53810", [53817] = "as 53810", [53818] = "as 53810", [54257] = "as 53810", [54259] = "as 53810",
            [54235] = "dat border, otb top; newer than Crystal's appearances, never on the map",
        };

        public static readonly IReadOnlyDictionary<uint, string> KnownUse = new Dictionary<uint, string>
        {
            [5911] = "red piece of cloth: useable in items.otb, no action registered",
            [32724] = "spectral gold nugget: useable in items.otb, no action registered",
            [52357] = "stag shield: useable in items.otb, no action registered",
        };

        private const byte GroupGround = 1, GroupDeprecated = 14;

        public static string OtbClass(ServerItem item)
        {
            if (item.RawGroup == GroupGround) return "ground";
            if (item.HasStackOrder)
                return item.StackOrder switch { TileStackOrder.Border => "border", TileStackOrder.Bottom => "bottom", _ => "top" };
            return "common";
        }

        public static List<Finding> Run(ConsistencyInput input, IProgress<(string stage, double fraction)>? progress = null, CancellationToken ct = default)
        {
            var findings = new List<Finding>();
            var things = input.Things;

            // ---- items.otb ----
            progress?.Report(("items.otb", 0.0));
            string? otbPath = input.Resolve(input.OtbPath, "data", "items", "items.otb");
            Dictionary<uint, ServerItem>? otb = null;
            if (otbPath == null || !File.Exists(otbPath))
                findings.Add(new Finding { Severity = Severity.Error, Category = CatFiles, Message = $"items.otb not found ({otbPath ?? "no server folder"})" });
            else
            {
                var reader = new OTBReader();
                if (!reader.Read(otbPath))
                    findings.Add(new Finding { Severity = Severity.Error, Category = CatFiles, Message = $"{otbPath} could not be read" });
                else
                {
                    otb = new Dictionary<uint, ServerItem>(reader.Items.Count);
                    foreach (ServerItem item in reader.Items)
                    {
                        if (otb.ContainsKey(item.ServerId))
                            findings.Add(new Finding { Severity = Severity.Error, Category = CatOtbIds, Id = item.ServerId, Message = "server id defined twice in items.otb" });
                        otb[item.ServerId] = item;
                    }
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(("dat <-> items.otb", 0.1));
                    CheckDatOtb(things, reader.Items, findings);
                }
            }
            ct.ThrowIfCancellationRequested();

            // ---- items.xml ----
            progress?.Report(("items.xml", 0.3));
            string? itemsXml = input.Resolve(input.ItemsXmlPath, "data", "items", "items.xml");
            if (itemsXml != null && File.Exists(itemsXml))
                CheckItemsXml(itemsXml, otb, findings, ct);
            else
                findings.Add(new Finding { Severity = Severity.Warning, Category = CatFiles, Message = $"items.xml not found ({itemsXml ?? "no server folder"})" });
            ct.ThrowIfCancellationRequested();

            // ---- monsters ----
            string? monsterDir = input.Resolve(input.MonsterDir, "data", "monster");
            if (monsterDir != null && Directory.Exists(monsterDir))
                CheckMonsters(monsterDir, things, otb, findings, progress, ct);
            else
                findings.Add(new Finding { Severity = Severity.Warning, Category = CatFiles, Message = $"monster folder not found ({monsterDir ?? "no server folder"})" });
            ct.ThrowIfCancellationRequested();

            // ---- outfits.xml ----
            progress?.Report(("outfits.xml", 0.95));
            string? outfitsXml = input.Resolve(input.OutfitsXmlPath, "data", "XML", "outfits.xml");
            if (outfitsXml != null && File.Exists(outfitsXml))
                CheckOutfitsXml(outfitsXml, things, findings);
            else
                findings.Add(new Finding { Severity = Severity.Warning, Category = CatFiles, Message = $"outfits.xml not found ({outfitsXml ?? "no server folder"})" });

            progress?.Report(("done", 1.0));
            return findings
                .OrderBy(f => f.Severity)
                .ThenBy(f => f.Category, StringComparer.Ordinal)
                .ThenBy(f => f.Id)
                .ToList();
        }

        // ---- dat <-> items.otb -------------------------------------------------------------------------------

        private static void CheckDatOtb(ThingsSnapshot things, List<ServerItem> items, List<Finding> findings)
        {
            int forceUseDatOnly = 0;
            var otbIds = new HashSet<uint>();
            foreach (ServerItem node in items)
            {
                otbIds.Add(node.ServerId);
                if (node.ServerId == 0 || node.RawGroup == GroupDeprecated)
                    continue;

                if (node.ClientId != node.ServerId)
                    findings.Add(new Finding { Severity = Severity.Warning, Category = CatOtbIds, Id = node.ServerId,
                        Message = $"server id {node.ServerId} has client id {node.ClientId} (they are expected to be equal)" });
                if (!things.Items.ContainsKey(node.ClientId))
                    findings.Add(new Finding { Severity = Severity.Error, Category = CatOtbIds, Id = node.ServerId,
                        Message = $"client id {node.ClientId} is not an item of the loaded dat (last item {things.LastItemId})" });

                // items_otb.py: `if sid is None or node.group == GROUP_DEPRECATED or sid not in client: continue`
                if (!things.Items.TryGetValue(node.ServerId, out var dat))
                    continue;

                string server = OtbClass(node);
                if (dat.StackClass != server)
                {
                    bool known = KnownStack.TryGetValue(node.ServerId, out string? why);
                    findings.Add(new Finding { Severity = known ? Severity.Info : Severity.Error, Category = CatStack, Id = node.ServerId,
                        Message = $"client {dat.StackClass}, server {server}: " + (known ? "known: " + why : "NEW - the client draws this item in another place of the tile stack") });
                }

                bool useable = node.MultiUse; // items.otb FLAG_USEABLE = 1 << 4
                if (dat.MultiUse != useable)
                {
                    bool known = KnownUse.TryGetValue(node.ServerId, out string? why);
                    findings.Add(new Finding { Severity = known ? Severity.Info : Severity.Error, Category = CatUse, Id = node.ServerId,
                        Message = $"client {(dat.MultiUse ? "use-with" : "use")}, server {(useable ? "useable" : "plain")}: " +
                                  (known ? "known: " + why : "NEW - \"You cannot use this object\" for this item's action") });
                }

                // not part of items_otb.py: ItemEditor never sets FLAG 26 (DERIVATION: "never 25/26"), so a dat
                // force-use item without it is normal; an otb force-use flag the dat does not have is not.
                if (node.ForceUse && !dat.ForceUse)
                    findings.Add(new Finding { Severity = Severity.Warning, Category = CatForceUse, Id = node.ServerId, Message = "items.otb has force use, the dat has not" });
                else if (dat.ForceUse && !node.ForceUse)
                    forceUseDatOnly++;
            }
            if (forceUseDatOnly > 0)
                findings.Add(new Finding { Severity = Severity.Info, Category = CatForceUse,
                    Message = $"{forceUseDatOnly} dat force-use item(s) without the items.otb flag (normal: ItemEditor never writes it, items_otb.py DERIVATION)" });

            // dat items with no items.otb node: ranges, so a new block of ids is one row
            var missing = things.Items.Keys.Where(id => !otbIds.Contains(id)).OrderBy(id => id).ToList();
            foreach (var (first, last) in Ranges(missing))
            {
                bool tail = first > (otbIds.Count > 0 ? otbIds.Max() : 0);
                findings.Add(new Finding { Severity = Severity.Warning, Category = CatDatOnly, Id = first,
                    Message = (first == last ? $"client item {first}" : $"client items {first}-{last} ({last - first + 1})") + " have no items.otb node" +
                              (tail ? " (append them: tools/items_otb/items_otb.py append --dat ...)" : "") });
            }
        }

        public static IEnumerable<(uint first, uint last)> Ranges(IEnumerable<uint> sortedIds)
        {
            uint? start = null, prev = null;
            foreach (uint id in sortedIds)
            {
                if (start == null) { start = prev = id; continue; }
                if (id == prev + 1) { prev = id; continue; }
                yield return (start.Value, prev!.Value);
                start = prev = id;
            }
            if (start != null) yield return (start.Value, prev!.Value);
        }

        // ---- items.xml <-> items.otb (checks.py _check_items_xml, per item) -----------------------------------

        private static void CheckItemsXml(string path, Dictionary<uint, ServerItem>? otb, List<Finding> findings, CancellationToken ct)
        {
            uint maxId = otb != null && otb.Count > 0 ? otb.Keys.Max() : 65535;
            var seen = new HashSet<uint>();
            int count = 0, lowFluidIds = 0;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, XmlResolver = null };
            try
            {
                using var reader = XmlReader.Create(path, settings);
                var info = (IXmlLineInfo)reader;
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.Name != "item") continue;
                    if ((++count & 1023) == 0) ct.ThrowIfCancellationRequested();
                    int line = info.LineNumber;
                    string? idText = reader.GetAttribute("id"), fromText = reader.GetAttribute("fromid"), toText = reader.GetAttribute("toid");
                    string name = reader.GetAttribute("name") ?? "";
                    string where = $"line {line}" + (name.Length > 0 ? $" \"{name}\"" : "");
                    uint first, last;
                    if (idText != null)
                    {
                        if (!TryId(idText, out first))
                        {
                            findings.Add(new Finding { Severity = Severity.Error, Category = CatItemsXml, Message = $"bad item id \"{idText}\" ({where})" });
                            continue;
                        }
                        last = first;
                    }
                    else if (fromText == null || toText == null)
                    {
                        findings.Add(new Finding { Severity = Severity.Error, Category = CatItemsXml, Message = $"an <item> without id or fromid/toid ({where})" });
                        continue;
                    }
                    else if (!TryId(fromText, out first) || !TryId(toText, out last) || first > last)
                    {
                        findings.Add(new Finding { Severity = Severity.Error, Category = CatItemsXml, Message = $"bad fromid/toid {fromText}-{toText} ({where})" });
                        continue;
                    }

                    var notInOtb = new List<uint>();
                    var deprecated = new List<uint>();
                    for (uint id = first; id <= last; id++)
                    {
                        if (!seen.Add(id))
                            findings.Add(new Finding { Severity = Severity.Warning, Category = CatItemsXml, Id = id, Message = $"id {id} defined twice ({where})" });
                        if (otb == null) continue;
                        if (!otb.TryGetValue(id, out ServerItem? node)) notInOtb.Add(id);
                        else if (node.RawGroup == GroupDeprecated) deprecated.Add(id);
                    }
                    string range = first == last ? "" : $" (range {first}-{last})";
                    // ids below 100 are no items (client item ids start at 100); TFS reads them as fluid names
                    var lowIds = notInOtb.Where(id => id < 100).ToList();
                    if (lowIds.Count > 0)
                    {
                        lowFluidIds += lowIds.Count;
                        notInOtb.RemoveAll(id => id < 100);
                    }
                    foreach (var (a, b) in Ranges(notInOtb))
                        findings.Add(new Finding { Severity = Severity.Warning, Category = CatItemsXml, Id = a,
                            Message = (a == b ? $"id {a}" : $"ids {a}-{b}") + (a > maxId ? $" not in items.otb (last id {maxId})" : " not in items.otb") + $"{range}, {where}" });
                    foreach (var (a, b) in Ranges(deprecated))
                        findings.Add(new Finding { Severity = Severity.Info, Category = CatItemsXml, Id = a,
                            Message = (a == b ? $"id {a} is" : $"ids {a}-{b} are") + $" deprecated in items.otb{range}, {where}" });
                }
            }
            catch (XmlException ex)
            {
                findings.Add(new Finding { Severity = Severity.Error, Category = CatItemsXml, Message = $"XML error: {ex.Message}" });
            }
            if (count == 0)
                findings.Add(new Finding { Severity = Severity.Error, Category = CatItemsXml, Message = "items.xml defines no items" });
            if (lowFluidIds > 0)
                findings.Add(new Finding { Severity = Severity.Info, Category = CatItemsXml,
                    Message = $"{lowFluidIds} id(s) below 100 (fluid names, not items.otb nodes)" });
            findings.Add(new Finding { Severity = Severity.Info, Category = CatItemsXml, Message = $"{count} <item> entries, {seen.Count} ids" });
        }

        private static bool TryId(string text, out uint id)
        {
            text = text.Trim();
            bool ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out id)
                : uint.TryParse(text, out id);
            return ok && id >= 1 && id <= 65535;
        }

        // ---- monsters (revscript mType: files) ---------------------------------------------------------------

        private static readonly Regex MonsterName = new(@"Game\.createMonsterType\(\s*[""']([^""']+)[""']", RegexOptions.Compiled);
        private static readonly Regex CorpseId = new(@"mType:corpseId\(\s*(\d+)\s*\)", RegexOptions.Compiled);
        private static readonly Regex OutfitCall = new(@"mType:outfit\(\s*\{([^}]*)\}\s*\)", RegexOptions.Compiled);
        private static readonly Regex LookType = new(@"\blookType\s*=\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex LookTypeEx = new(@"\blookTypeEx\s*=\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex LootId = new(@"\b\w+:setId\(\s*(\d+)\s*\)", RegexOptions.Compiled);

        private static void CheckMonsters(string dir, ThingsSnapshot things, Dictionary<uint, ServerItem>? otb, List<Finding> findings,
            IProgress<(string, double)>? progress, CancellationToken ct)
        {
            string[] files = Directory.GetFiles(dir, "*.lua", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            int monsters = 0;
            for (int i = 0; i < files.Length; i++)
            {
                if ((i & 63) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(($"monsters {i}/{files.Length}", 0.35 + 0.6 * i / Math.Max(1, files.Length)));
                }
                string text;
                try { text = File.ReadAllText(files[i]); }
                catch (IOException ex)
                {
                    findings.Add(new Finding { Severity = Severity.Warning, Category = CatMonsters, Message = $"{files[i]}: {ex.Message}" });
                    continue;
                }
                var nameMatch = MonsterName.Match(text);
                if (!nameMatch.Success) continue; // libraries, helpers
                monsters++;
                string rel = Path.GetRelativePath(dir, files[i]).Replace(Path.DirectorySeparatorChar, '/');
                string who = $"{nameMatch.Groups[1].Value} ({rel})";

                var corpse = CorpseId.Match(text);
                if (corpse.Success && uint.TryParse(corpse.Groups[1].Value, out uint corpseId) && corpseId != 0)
                    CheckItemRef(corpseId, "corpse", who, things, otb, findings);

                var outfit = OutfitCall.Match(text);
                if (!outfit.Success)
                    findings.Add(new Finding { Severity = Severity.Info, Category = CatMonsters, Message = $"{who}: no mType:outfit" });
                else
                {
                    string body = outfit.Groups[1].Value;
                    var lt = LookType.Match(body);
                    var ltx = LookTypeEx.Match(body);
                    uint look = lt.Success ? uint.Parse(lt.Groups[1].Value) : 0;
                    uint lookEx = ltx.Success ? uint.Parse(ltx.Groups[1].Value) : 0;
                    if (look != 0)
                    {
                        if (!things.Outfits.TryGetValue(look, out var o))
                            findings.Add(new Finding { Severity = Severity.Error, Category = CatMonsters, Id = look,
                                Message = $"{who}: lookType {look} is not an outfit of the dat (last {things.LastOutfitId})" });
                        else if (o.Empty)
                            findings.Add(new Finding { Severity = Severity.Warning, Category = CatMonsters, Id = look,
                                Message = $"{who}: lookType {look} has no sprites in the dat (invisible)" });
                    }
                    else if (lookEx != 0)
                        CheckItemRef(lookEx, "lookTypeEx", who, things, otb, findings);
                    else
                        findings.Add(new Finding { Severity = Severity.Info, Category = CatMonsters, Message = $"{who}: outfit without lookType/lookTypeEx (invisible)" });
                }

                var lootSeen = new HashSet<uint>();
                foreach (Match m in LootId.Matches(text))
                    if (uint.TryParse(m.Groups[1].Value, out uint lootId) && lootSeen.Add(lootId))
                        CheckItemRef(lootId, "loot", who, things, otb, findings);
            }
            findings.Add(new Finding { Severity = Severity.Info, Category = CatMonsters, Message = $"{monsters} monster file(s) checked in {dir}" });
        }

        private static void CheckItemRef(uint serverId, string what, string who, ThingsSnapshot things, Dictionary<uint, ServerItem>? otb, List<Finding> findings)
        {
            uint clientId = serverId;
            if (otb != null)
            {
                if (!otb.TryGetValue(serverId, out ServerItem? node))
                {
                    findings.Add(new Finding { Severity = Severity.Error, Category = CatMonsters, Id = serverId, Message = $"{who}: {what} item {serverId} is not in items.otb" });
                    return;
                }
                if (node.RawGroup == GroupDeprecated)
                {
                    findings.Add(new Finding { Severity = Severity.Warning, Category = CatMonsters, Id = serverId, Message = $"{who}: {what} item {serverId} is deprecated in items.otb" });
                    return;
                }
                clientId = node.ClientId;
            }
            if (!things.Items.TryGetValue(clientId, out var item))
                findings.Add(new Finding { Severity = Severity.Error, Category = CatMonsters, Id = serverId, Message = $"{who}: {what} item {serverId} (client id {clientId}) is not in the dat" });
            else if (item.Empty)
                findings.Add(new Finding { Severity = Severity.Warning, Category = CatMonsters, Id = serverId, Message = $"{who}: {what} item {serverId} has no sprites in the dat (invisible)" });
        }

        // ---- outfits.xml <-> dat outfits ---------------------------------------------------------------------

        private static void CheckOutfitsXml(string path, ThingsSnapshot things, List<Finding> findings)
        {
            var seen = new HashSet<(string type, uint look)>();
            int count = 0;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, XmlResolver = null };
                using var reader = XmlReader.Create(path, settings);
                var info = (IXmlLineInfo)reader;
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.Name != "outfit") continue;
                    count++;
                    string name = reader.GetAttribute("name") ?? "";
                    string type = reader.GetAttribute("type") ?? "";
                    string where = $"\"{name}\" type {type}, line {info.LineNumber}";
                    if (!uint.TryParse(reader.GetAttribute("looktype"), out uint look) || look == 0)
                    {
                        findings.Add(new Finding { Severity = Severity.Error, Category = CatOutfits, Message = $"bad looktype \"{reader.GetAttribute("looktype")}\" ({where})" });
                        continue;
                    }
                    if (!seen.Add((type, look)))
                        findings.Add(new Finding { Severity = Severity.Warning, Category = CatOutfits, Id = look, Message = $"looktype {look} listed twice for type {type} ({where})" });
                    if (!things.Outfits.TryGetValue(look, out var o))
                    {
                        findings.Add(new Finding { Severity = Severity.Error, Category = CatOutfits, Id = look,
                            Message = $"looktype {look} is not an outfit of the dat (last {things.LastOutfitId}); clients cannot parse it ({where})" });
                        continue;
                    }
                    if (o.Empty)
                        findings.Add(new Finding { Severity = Severity.Warning, Category = CatOutfits, Id = look, Message = $"looktype {look} has no sprites in the dat ({where})" });
                    // MOUNTS_SPEC.md 0 / things860 README "Riding frames": a rider sits only when the outfit has a
                    // second pattern-Z (mounted) layer; with pattern Z 1 Mehah draws the standing figure on the mount
                    if (o.PatternZ < 2)
                    {
                        findings.Add(new Finding { Severity = Severity.Info, Category = CatMounts, Id = look,
                            Message = $"looktype {look} has no mounted layer (pattern Z {o.PatternZ}): a rider would stand on the mount ({where})" });
                    }
                }
            }
            catch (XmlException ex)
            {
                findings.Add(new Finding { Severity = Severity.Error, Category = CatOutfits, Message = $"XML error: {ex.Message}" });
            }
            if (count == 0)
                findings.Add(new Finding { Severity = Severity.Error, Category = CatOutfits, Message = "outfits.xml defines no outfits" });
        }
    }
}
