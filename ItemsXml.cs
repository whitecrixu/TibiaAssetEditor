using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Media.Imaging;

namespace Assets_Editor
{
    /// <summary>
    /// Loads and edits OpenTibia server-side items.xml.
    /// </summary>
    public class ItemEntry : INotifyPropertyChanged
    {
        // Identity / header attributes on <item>
        public ushort Id { get; set; }
        public string Article { get; set; } = "";
        public string Name { get; set; } = "";
        public string Plural { get; set; } = "";

        // Well-known typed attributes — common OpenTibia keys.
        // All of these are serialized as <attribute key="..." value="..."/> children.
        public string Description { get; set; } = "";
        public string Type        { get; set; } = "";   // bucket, key, magicfield, teleport, door, depot, ...
        public string SlotType    { get; set; } = "";   // head, body, legs, feet, necklace, ring, ammo, weapon, shield, two-handed
        public string WeaponType  { get; set; } = "";   // sword, axe, club, distance, wand, ammunition
        public string ShootType   { get; set; } = "";   // bolt, arrow, fire, energy, poisonarrow, ...
        public string Effect      { get; set; } = "";
        public string CorpseType  { get; set; } = "";   // venom, blood, undead, fire, energy
        public string FluidSource { get; set; } = "";   // water, blood, beer, slime, lemonade, milk, mana, life, oil, urine, coconut, wine, mud, fruitjuice, lava, rum

        public int Weight         { get; set; }         // stored ×100 in XML (e.g. 1.00kg = "100")
        public int Attack         { get; set; }
        public int Defense        { get; set; }
        public int ExtraDef       { get; set; }
        public int Armor          { get; set; }
        public int Range          { get; set; }
        public int Charges        { get; set; }
        public int Duration       { get; set; }
        public int DecayTo        { get; set; }
        public int ContainerSize  { get; set; }
        public int MaxHitChance   { get; set; }
        public int BreakChance    { get; set; }
        public int MaxTextLen     { get; set; }
        public int WriteOnceItemId{ get; set; }
        public int RotateTo       { get; set; }
        public int Speed          { get; set; }         // boots of haste etc
        public int RequiredLevel  { get; set; }
        public int RequiredMagLevel { get; set; }
        public int Wrapable       { get; set; }
        public int Unwrapable     { get; set; }

        // Skill bonuses
        public int SkillSword  { get; set; }
        public int SkillAxe    { get; set; }
        public int SkillClub   { get; set; }
        public int SkillDist   { get; set; }
        public int SkillShield { get; set; }
        public int SkillFish   { get; set; }
        public int MagicPoints { get; set; }

        // Bool-style attributes (serialized "1"/"0" or "yes"/"no")
        public bool StopDuration    { get; set; }
        public bool ShowCount       { get; set; }
        public bool ShowAttributes  { get; set; }
        public bool Readable        { get; set; }
        public bool Writeable       { get; set; }
        public bool AllowDistRead   { get; set; }
        public bool ReplaceAble     { get; set; }
        public bool BlockProjectile { get; set; }
        public bool MoveAble        { get; set; } = true;
        public bool PickupAble      { get; set; } = true;

        // Catch-all for anything this editor doesn't know about — preserved on save.
        public Dictionary<string, string> CustomAttributes { get; set; } = new();

        // UI-only, not serialized: thumbnail from appearance.dat (if loaded) and display name.
        private Bitmap? _image;
        public Bitmap? Image
        {
            get => _image;
            set { if (_image != value) { _image = value; OnChanged(nameof(Image)); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public override string ToString() => string.IsNullOrEmpty(Name) ? $"(item {Id})" : $"{Id}: {Name}";
    }

    public static class ItemsXml
    {
        public static Dictionary<ushort, ItemEntry> Items { get; private set; } = new();
        public static string? LoadedPath { get; private set; }
        public static bool IsLoaded => Items.Count > 0;

        // Known attribute keys we promote to typed fields on ItemEntry.
        private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "description","type","slottype","weapontype","shoottype","effect","corpsetype","fluidsource",
            "weight","attack","defense","extradef","armor","range","charges","duration","decayto",
            "containersize","maxhitchance","breakchance","maxtextlen","writeonceitemid","rotateto",
            "speed","level","maglevel","wrapable","unwrapable",
            "skillsword","skillaxe","skillclub","skilldist","skillshield","skillfish","magicpoints",
            "stopduration","showcount","showattributes","readable","writeable","allowdistread",
            "replaceable","blockprojectile","moveable","pickupable",
        };

        private static bool ToBool(string v) =>
            v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("true", StringComparison.OrdinalIgnoreCase);

        private static int ToInt(string v) => int.TryParse(v, out var n) ? n : 0;

        public static bool Load(string path)
        {
            if (!File.Exists(path)) return false;

            try
            {
                Items.Clear();
                LoadedPath = path;

                var doc = XDocument.Load(path);
                var root = doc.Root;
                if (root == null) return false;

                foreach (var el in root.Elements("item"))
                {
                    var idAttr = el.Attribute("id");
                    var fromAttr = el.Attribute("fromid");
                    var toAttr = el.Attribute("toid");

                    // Parse all <attribute key="..." value="..."/> children
                    var allAttrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var attrEl in el.Elements("attribute"))
                    {
                        string? k = attrEl.Attribute("key")?.Value;
                        string? v = attrEl.Attribute("value")?.Value;
                        if (!string.IsNullOrEmpty(k)) allAttrs[k] = v ?? "";
                    }

                    string article = el.Attribute("article")?.Value ?? "";
                    string name = el.Attribute("name")?.Value ?? "";
                    string plural = el.Attribute("plural")?.Value ?? "";

                    ItemEntry BuildEntry(ushort id)
                    {
                        var entry = new ItemEntry
                        {
                            Id = id,
                            Article = article,
                            Name = name,
                            Plural = plural,
                        };
                        ApplyAttributes(entry, allAttrs);
                        return entry;
                    }

                    if (idAttr != null && ushort.TryParse(idAttr.Value, out ushort id))
                        Items[id] = BuildEntry(id);
                    else if (fromAttr != null && toAttr != null &&
                        ushort.TryParse(fromAttr.Value, out ushort from) &&
                        ushort.TryParse(toAttr.Value, out ushort to))
                    {
                        for (ushort i = from; i <= to; i++)
                            if (!Items.ContainsKey(i))
                                Items[i] = BuildEntry(i);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"Failed to load items.xml: {ex.Message}");
                return false;
            }
        }

        private static void ApplyAttributes(ItemEntry e, Dictionary<string, string> attrs)
        {
            foreach (var kv in attrs)
            {
                string k = kv.Key.ToLowerInvariant();
                string v = kv.Value;
                switch (k)
                {
                    case "description":    e.Description    = v; break;
                    case "type":           e.Type           = v; break;
                    case "slottype":       e.SlotType       = v; break;
                    case "weapontype":     e.WeaponType     = v; break;
                    case "shoottype":      e.ShootType      = v; break;
                    case "effect":         e.Effect         = v; break;
                    case "corpsetype":     e.CorpseType     = v; break;
                    case "fluidsource":    e.FluidSource    = v; break;

                    case "weight":         e.Weight         = ToInt(v); break;
                    case "attack":         e.Attack         = ToInt(v); break;
                    case "defense":        e.Defense        = ToInt(v); break;
                    case "extradef":       e.ExtraDef       = ToInt(v); break;
                    case "armor":          e.Armor          = ToInt(v); break;
                    case "range":          e.Range          = ToInt(v); break;
                    case "charges":        e.Charges        = ToInt(v); break;
                    case "duration":       e.Duration       = ToInt(v); break;
                    case "decayto":        e.DecayTo        = ToInt(v); break;
                    case "containersize":  e.ContainerSize  = ToInt(v); break;
                    case "maxhitchance":   e.MaxHitChance   = ToInt(v); break;
                    case "breakchance":    e.BreakChance    = ToInt(v); break;
                    case "maxtextlen":     e.MaxTextLen     = ToInt(v); break;
                    case "writeonceitemid":e.WriteOnceItemId= ToInt(v); break;
                    case "rotateto":       e.RotateTo       = ToInt(v); break;
                    case "speed":          e.Speed          = ToInt(v); break;
                    case "level":          e.RequiredLevel  = ToInt(v); break;
                    case "maglevel":       e.RequiredMagLevel = ToInt(v); break;
                    case "wrapable":       e.Wrapable       = ToInt(v); break;
                    case "unwrapable":     e.Unwrapable     = ToInt(v); break;

                    case "skillsword":     e.SkillSword     = ToInt(v); break;
                    case "skillaxe":       e.SkillAxe       = ToInt(v); break;
                    case "skillclub":      e.SkillClub      = ToInt(v); break;
                    case "skilldist":      e.SkillDist      = ToInt(v); break;
                    case "skillshield":    e.SkillShield    = ToInt(v); break;
                    case "skillfish":      e.SkillFish      = ToInt(v); break;
                    case "magicpoints":    e.MagicPoints    = ToInt(v); break;

                    case "stopduration":   e.StopDuration    = ToBool(v); break;
                    case "showcount":      e.ShowCount       = ToBool(v); break;
                    case "showattributes": e.ShowAttributes  = ToBool(v); break;
                    case "readable":       e.Readable        = ToBool(v); break;
                    case "writeable":      e.Writeable       = ToBool(v); break;
                    case "allowdistread":  e.AllowDistRead   = ToBool(v); break;
                    case "replaceable":    e.ReplaceAble     = ToBool(v); break;
                    case "blockprojectile":e.BlockProjectile = ToBool(v); break;
                    case "moveable":       e.MoveAble        = ToBool(v); break;
                    case "pickupable":     e.PickupAble      = ToBool(v); break;

                    default:
                        e.CustomAttributes[kv.Key] = v; // preserve unknown keys
                        break;
                }
            }
        }

        public static bool Save(string? path = null)
        {
            path ??= LoadedPath;
            if (string.IsNullOrEmpty(path)) return false;

            try
            {
                var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null));
                var root = new XElement("items");
                doc.Add(root);

                foreach (var kv in Items.OrderBy(k => k.Key))
                {
                    var entry = kv.Value;
                    var el = new XElement("item", new XAttribute("id", entry.Id));
                    if (!string.IsNullOrEmpty(entry.Article)) el.Add(new XAttribute("article", entry.Article));
                    if (!string.IsNullOrEmpty(entry.Name))    el.Add(new XAttribute("name", entry.Name));
                    if (!string.IsNullOrEmpty(entry.Plural))  el.Add(new XAttribute("plural", entry.Plural));

                    WriteAttrs(el, entry);

                    root.Add(el);
                }

                using var writer = XmlWriter.Create(path, new XmlWriterSettings
                {
                    Indent = true,
                    IndentChars = "\t",
                    Encoding = System.Text.Encoding.UTF8,
                    OmitXmlDeclaration = false,
                });
                doc.Save(writer);
                return true;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"Failed to save items.xml: {ex.Message}");
                return false;
            }
        }

        private static void WriteAttrs(XElement el, ItemEntry e)
        {
            void AddStr(string k, string v) { if (!string.IsNullOrEmpty(v)) el.Add(new XElement("attribute", new XAttribute("key", k), new XAttribute("value", v))); }
            void AddInt(string k, int v)    { if (v != 0) el.Add(new XElement("attribute", new XAttribute("key", k), new XAttribute("value", v.ToString()))); }
            void AddBool(string k, bool v, bool def = false) { if (v != def) el.Add(new XElement("attribute", new XAttribute("key", k), new XAttribute("value", v ? "1" : "0"))); }

            AddStr("description", e.Description);
            AddStr("type",        e.Type);
            AddStr("slotType",    e.SlotType);
            AddStr("weaponType",  e.WeaponType);
            AddStr("shootType",   e.ShootType);
            AddStr("effect",      e.Effect);
            AddStr("corpseType",  e.CorpseType);
            AddStr("fluidSource", e.FluidSource);

            AddInt("weight",         e.Weight);
            AddInt("attack",         e.Attack);
            AddInt("defense",        e.Defense);
            AddInt("extraDef",       e.ExtraDef);
            AddInt("armor",          e.Armor);
            AddInt("range",          e.Range);
            AddInt("charges",        e.Charges);
            AddInt("duration",       e.Duration);
            AddInt("decayTo",        e.DecayTo);
            AddInt("containerSize",  e.ContainerSize);
            AddInt("maxHitChance",   e.MaxHitChance);
            AddInt("breakChance",    e.BreakChance);
            AddInt("maxTextLen",     e.MaxTextLen);
            AddInt("writeOnceItemId",e.WriteOnceItemId);
            AddInt("rotateTo",       e.RotateTo);
            AddInt("speed",          e.Speed);
            AddInt("level",          e.RequiredLevel);
            AddInt("magLevel",       e.RequiredMagLevel);
            AddInt("wrapable",       e.Wrapable);
            AddInt("unwrapable",     e.Unwrapable);

            AddInt("skillSword",  e.SkillSword);
            AddInt("skillAxe",    e.SkillAxe);
            AddInt("skillClub",   e.SkillClub);
            AddInt("skillDist",   e.SkillDist);
            AddInt("skillShield", e.SkillShield);
            AddInt("skillFish",   e.SkillFish);
            AddInt("magicpoints", e.MagicPoints);

            AddBool("stopDuration",    e.StopDuration);
            AddBool("showCount",       e.ShowCount);
            AddBool("showAttributes",  e.ShowAttributes);
            AddBool("readable",        e.Readable);
            AddBool("writeable",       e.Writeable);
            AddBool("allowDistRead",   e.AllowDistRead);
            AddBool("replaceable",     e.ReplaceAble);
            AddBool("blockprojectile", e.BlockProjectile);
            AddBool("moveable",        e.MoveAble, def: true);
            AddBool("pickupable",      e.PickupAble, def: true);

            // Custom attributes unknown to the editor
            foreach (var attr in e.CustomAttributes.OrderBy(a => a.Key))
                el.Add(new XElement("attribute",
                    new XAttribute("key", attr.Key),
                    new XAttribute("value", attr.Value)));
        }

        public static string? GetName(uint id) => Items.TryGetValue((ushort)id, out var e) ? e.Name : null;
    }
}
