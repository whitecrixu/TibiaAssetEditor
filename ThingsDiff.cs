using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Tibia.Protobuf.Appearances;

namespace Assets_Editor
{
    public enum ThingCategory { Item, Outfit, Effect, Missile }

    public sealed class ThingChange
    {
        public ThingCategory Category { get; init; }
        public uint Id { get; init; }
        public string Kind { get; init; } = "";     // new / removed / art / drawing / gameplay
        public string Details { get; init; } = "";

        public string CategoryText => Category.ToString();
    }

    public sealed class ChecklistStep
    {
        public string Title = "";
        public string Why = "";
        public List<string> Commands = [];
    }

    public sealed class ThingsDiffResult
    {
        public const string KindNew = "new", KindRemoved = "removed", KindArt = "art", KindDrawing = "drawing", KindGameplay = "gameplay";

        public string OriginalPath { get; init; } = "";
        public List<ThingChange> Changes { get; } = [];

        // what the follow-up checklist keys on
        public bool MinimapChanged { get; internal set; }
        public bool GroundChanged { get; internal set; }
        public bool OtbMirroredDrawingChanged { get; internal set; } // elevation / light / minimap: items.otb keeps a copy

        public bool HasChanges => Changes.Count > 0;

        public IEnumerable<uint> Ids(ThingCategory c, string kind) => Changes.Where(x => x.Category == c && x.Kind == kind).Select(x => x.Id).Distinct().OrderBy(x => x);
        public int Count(ThingCategory c, string kind) => Ids(c, kind).Count();
        public bool Any(ThingCategory c, params string[] kinds) => Changes.Any(x => x.Category == c && kinds.Contains(x.Kind));

        public string Summary()
        {
            var sb = new StringBuilder();
            foreach (ThingCategory c in Enum.GetValues<ThingCategory>())
            {
                sb.Append($"{c}s: {Count(c, KindNew)} new, {Count(c, KindArt)} art changed, {Count(c, KindGameplay)} gameplay flags, " +
                          $"{Count(c, KindDrawing)} drawing flags");
                int removed = Count(c, KindRemoved);
                if (removed > 0) sb.Append($", {removed} removed");
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        public string ToTsv()
        {
            var sb = new StringBuilder("category\tid\tkind\tdetails\n");
            foreach (var c in Changes)
                sb.Append($"{c.Category}\t{c.Id}\t{c.Kind}\t{c.Details.Replace('\t', ' ')}\n");
            return sb.ToString();
        }

        public string ToJson()
        {
            var summary = new Dictionary<string, object>();
            foreach (ThingCategory c in Enum.GetValues<ThingCategory>())
                summary[c.ToString().ToLowerInvariant()] = new Dictionary<string, uint[]>
                {
                    [KindNew] = Ids(c, KindNew).ToArray(),
                    [KindRemoved] = Ids(c, KindRemoved).ToArray(),
                    [KindArt] = Ids(c, KindArt).ToArray(),
                    [KindGameplay] = Ids(c, KindGameplay).ToArray(),
                    [KindDrawing] = Ids(c, KindDrawing).ToArray(),
                };
            var doc = new Dictionary<string, object>
            {
                ["original"] = OriginalPath,
                ["created"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                ["minimap_changed"] = MinimapChanged,
                ["ground_changed"] = GroundChanged,
                ["summary"] = summary,
                ["changes"] = Changes.Select(c => new Dictionary<string, object>
                {
                    ["category"] = c.Category.ToString().ToLowerInvariant(),
                    ["id"] = c.Id,
                    ["kind"] = c.Kind,
                    ["details"] = c.Details,
                }).ToList(),
            };
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }

        // The follow-up checklist from client-mehah/tools/wolfots/things860/README.md ("After a release that ships
        // these files, regenerate what reads the client's things"), with only the steps that the changes need. thingsDir is the client-mehah things directory.
        public List<ChecklistStep> Checklist(string thingsDir, string? serverRoot)
        {
            var steps = new List<ChecklistStep>();
            if (!HasChanges) return steps;

            string things = Path.GetFullPath(thingsDir);
            if (ServerTools.IsFrozenClientTree(things))
                things = ServerTools.MehahThingsFor(things) ?? things;
            string dat = Path.Combine(things, "Tibia.dat");
            string? clientRoot = ClientRootOf(things);
            string client = clientRoot ?? "<client-mehah>";
            string server = serverRoot ?? GuessServerRoot(clientRoot) ?? "<TFS-1.5-Downgrades-8.60>";
            string site = GuessSiteRoot(clientRoot) ?? "<wolfaac>";

            bool newIds = Changes.Any(c => c.Kind == KindNew);
            var newItems = Ids(ThingCategory.Item, KindNew).ToList();
            var artItems = Ids(ThingCategory.Item, KindArt).ToList();
            var gameplayItems = Ids(ThingCategory.Item, KindGameplay).ToList();
            var drawingItems = Ids(ThingCategory.Item, KindDrawing).ToList();
            var outfitIds = Changes.Where(c => c.Category == ThingCategory.Outfit && c.Kind is KindNew or KindArt).Select(c => c.Id).Distinct().OrderBy(x => x).ToList();
            bool itemsTouched = Any(ThingCategory.Item, KindNew, KindArt, KindGameplay, KindDrawing, KindRemoved);

            steps.Add(new ChecklistStep
            {
                Title = "Client update channel build with the new things (publish it FIRST)",
                Why = "The updater ships data/things/860 only through a channel build; " +
                      (newIds ? "new ids: an older client that receives an id or looktype it does not know cannot parse the packet, so the clients must have them before the server sends them (newitems.md)."
                              : "players see the change only after they update."),
                Commands =
                [
                    $"# build the next channel build from {client}/dist/wolfupdate with {things}",
                    $"python3 -I {client}/tools/wolfots/check_things_manifest.py {things} <channel>/wolfupdate/builds/<N>.json   # must say the sprites match",
                    "# keep a rollback copy of the previous Tibia.dat/.spr/.otfi (+ SHA256SUMS) before installing",
                ],
            });

            if (itemsTouched)
            {
                var cmds = new List<string>();
                if (newItems.Count > 0)
                    cmds.Add($"cd {server} && python3 -I tools/items_otb/items_otb.py append --dat {dat} --out /tmp/items.otb.new   # {newItems.Count} new item id(s), then review and --in-place");
                var rederive = gameplayItems.Concat(artItems).Concat(OtbMirroredDrawingChanged ? drawingItems : []).Where(id => !newItems.Contains(id)).Distinct().OrderBy(x => x).ToList();
                if (rederive.Count > 0)
                    cmds.Add($"cd {server} && python3 -I tools/items_otb/items_otb.py rederive --dat {dat} --ids {string.Join(",", rederive)} --out /tmp/items.otb.new");
                cmds.Add($"cd {server} && python3 -I tools/items_otb/items_otb.py check --dat {dat}   # must end with \"0 unexpected difference(s)\"");
                steps.Add(new ChecklistStep
                {
                    Title = "Server items.otb",
                    Why = (newItems.Count > 0 ? "New client item ids need items.otb nodes (server id = client id). " : "") +
                          (gameplayItems.Count > 0 ? "Gameplay flags changed: the server's flags/stack order come from items.otb, rederive them from the dat. " : "") +
                          (artItems.Count > 0 ? "Art changed: the node's sprite hash follows the art (the engine never reads it; rederive keeps the file in step). " : "") +
                          (OtbMirroredDrawingChanged ? "Elevation / light / minimap colour changed: items.otb keeps a copy of them (DERIVATION in items_otb.py). " : "") +
                          "items.otb needs a server restart after the upload.",
                    Commands = cmds,
                });
                if (newItems.Count > 0)
                    steps.Add(new ChecklistStep
                    {
                        Title = "Server items.xml names for the new items",
                        Why = "Names (Crystal/Canary or the market name), weight, slot, container size; items.xml needs a restart.",
                        Commands = [$"# {server}/data/items/items.xml: ids {Compact(newItems)}"],
                    });
            }

            if (MinimapChanged)
                steps.Add(new ChecklistStep
                {
                    Title = "Minimap colours: server colours + map tiles, client world map",
                    Why = "Minimap colours of items changed (things860-regen.md).",
                    Commands =
                    [
                        $"cd {server} && python3 -I tools/newera_files/tools/extract_minimap_colors.py {dat}",
                        $"cd {server} && nice -n 19 ionice -c3 python3 tools/newera_files/newera-files map --publish   # site map tiles",
                        $"rm {server}/tools/wolf-control/server/data/map/.stamp   # Wolf Control / Sentinel tiles rebuild on the next timer run",
                        $"cd {client} && python3 -I tools/wolfots/world_minimap/generate.py --dat {dat}   # data/minimap/world.otmm (ships in the channel build)",
                    ],
                });

            if (GroundChanged)
                steps.Add(new ChecklistStep
                {
                    Title = "Client footstep materials",
                    Why = "Ground items changed (new grounds, ground speed or ground flag).",
                    Commands = [$"cd {client} && python3 -I tools/wolfots/sounds/make_materials.py --things {things}   # data/sounds/materials.json"],
                });

            var siteItems = newItems.Concat(artItems).Distinct().OrderBy(x => x).ToList();
            if (siteItems.Count > 0)
                steps.Add(new ChecklistStep
                {
                    Title = "Website item pictures",
                    Why = "Item art is new or changed. Pass the client-mehah tree: the script's default reads the frozen OTCv8 tree." +
                          (artItems.Count > 0 ? " Existing pictures are served immutable: bump the version prefix and copy the set before the code deploy (things860-regen.md)." : ""),
                    Commands = [$"cd {site} && python3 scripts/item_sprites.py --client {client} --assets data/things/860 --ids {string.Join(",", siteItems)} --apply"],
                });

            if (outfitIds.Count > 0)
                steps.Add(new ChecklistStep
                {
                    Title = "Website outfit sprites",
                    Why = "Outfits are new or their art changed (things860 README, riding frames / newitems).",
                    Commands = [$"cd {site} && python3 scripts/outfit_sprites.py --client {client} --assets data/things/860 --ids {string.Join(",", outfitIds)} --apply"],
                });

            var newOutfits = Ids(ThingCategory.Outfit, KindNew).ToList();
            if (newOutfits.Count > 0)
                steps.Add(new ChecklistStep
                {
                    Title = "Server outfits.xml (player outfits only)",
                    Why = "New looktypes; monster looks need no entry. outfits.xml needs a restart.",
                    Commands = [$"# {server}/data/XML/outfits.xml: looktypes {Compact(newOutfits)}"],
                });

            if (Any(ThingCategory.Effect, KindNew, KindArt) || Any(ThingCategory.Missile, KindNew, KindArt))
                steps.Add(new ChecklistStep
                {
                    Title = "Effects / missiles",
                    Why = "Only the client channel build is needed; new ids are usable by the server only after it.",
                    Commands = [],
                });

            steps.Add(new ChecklistStep
            {
                Title = "Order of the release",
                Why = "Client channel build and web bundle first, server files (items.otb/items.xml/outfits.xml + restart) second.",
                Commands = ["# Project Wolf Server Tools > Export for approval writes the staging folder (server/data/..., client/Tibia.*, SHA256SUMS, README.txt)"],
            });
            return steps;
        }

        public static string ChecklistText(List<ChecklistStep> steps)
        {
            if (steps.Count == 0) return "No changes: nothing to regenerate.";
            var sb = new StringBuilder();
            int n = 1;
            foreach (var s in steps)
            {
                sb.AppendLine($"{n++}. {s.Title}");
                if (s.Why.Length > 0) sb.AppendLine($"   {s.Why}");
                foreach (string c in s.Commands) sb.AppendLine($"   {c}");
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        private static string Compact(IEnumerable<uint> ids) =>
            string.Join(", ", ConsistencyChecker.Ranges(ids).Select(r => r.first == r.last ? $"{r.first}" : $"{r.first}-{r.last}"));

        public static string? ClientRootOf(string thingsDir)
        {
            var d = new DirectoryInfo(thingsDir);
            if (d.Name == "860" && d.Parent?.Name == "things" && d.Parent.Parent?.Name == "data")
                return d.Parent.Parent.Parent?.FullName;
            return null;
        }

        public static string? GuessServerRoot(string? clientRoot)
        {
            string? parent = clientRoot == null ? null : Path.GetDirectoryName(clientRoot);
            if (parent == null) return null;
            string candidate = Path.Combine(parent, "TFS-1.5-Downgrades-8.60");
            return Directory.Exists(candidate) ? candidate : null;
        }

        private static string? GuessSiteRoot(string? clientRoot)
        {
            // wolfaac/scripts/item_sprites.py defaults to ROOT.parent / <project> / "client": the site is the project's sibling
            string? project = clientRoot == null ? null : Path.GetDirectoryName(clientRoot);
            string? parent = project == null ? null : Path.GetDirectoryName(project);
            if (parent == null) return null;
            string candidate = Path.Combine(parent, "wolfaac");
            return Directory.Exists(candidate) ? candidate : null;
        }
    }

    public static class ThingsDiff
    {
        // README: "drawing attributes (displacement, elevation, light, minimap colour, animate-always)"; plus flags
        // that only change how a thing is drawn. Everything else is gameplay (stack order, blocking, use, market...).
        private static readonly HashSet<string> DrawingFlags =
        [
            "shift", "height", "light", "automap", "animate_always", "translucent", "lying_object", "dont_hide",
            "topeffect", "no_movement_animation", "wingsOffset", "transparencylevel",
        ];
        private static readonly HashSet<string> OtbMirrored = ["height", "light", "automap"];

        // For the legacy editor after Compile: ThingsDiff.Compute(MainWindow.appearances, MainWindow._datPath + "-bak",
        // loadedVersion.Structure, MainWindow.GetCurrentPreset()).
        public static ThingsDiffResult Compute(Appearances current, string originalDatPath, int structure, PresetSettings preset, IEnumerable<uint>? modifiedSprites = null) =>
            Compute(current, originalDatPath, MainWindow.datStructure.GetVersionInfo(structure), preset, modifiedSprites);

        public static ThingsDiffResult Compute(Appearances current, string originalDatPath, VersionInfo versionInfo, PresetSettings preset, IEnumerable<uint>? modifiedSprites = null)
        {
            Appearances original = ServerTools.ReadLegacyDat(originalDatPath, versionInfo, preset, out _);
            return Compare(original, current, originalDatPath, modifiedSprites);
        }

        public static ThingsDiffResult Compare(Appearances original, Appearances current, string originalLabel, IEnumerable<uint>? modifiedSprites = null)
        {
            var result = new ThingsDiffResult { OriginalPath = originalLabel };
            var touched = modifiedSprites == null ? null : new HashSet<uint>(modifiedSprites);
            CompareList(ThingCategory.Item, original.Object, current.Object, result, touched);
            CompareList(ThingCategory.Outfit, original.Outfit, current.Outfit, result, touched);
            CompareList(ThingCategory.Effect, original.Effect, current.Effect, result, touched);
            CompareList(ThingCategory.Missile, original.Missile, current.Missile, result, touched);
            return result;
        }

        private static void CompareList(ThingCategory cat, IList<Appearance> before, IList<Appearance> after, ThingsDiffResult result, HashSet<uint>? touched)
        {
            var old = new Dictionary<uint, Appearance>(before.Count);
            foreach (var a in before) old[a.Id] = a;
            var seen = new HashSet<uint>();
            foreach (Appearance now in after.OrderBy(a => a.Id))
            {
                seen.Add(now.Id);
                if (!old.TryGetValue(now.Id, out Appearance? was))
                {
                    bool empty = !now.FrameGroup.Any(g => g.SpriteInfo != null && g.SpriteInfo.SpriteId.Any(s => s != 0));
                    result.Changes.Add(new ThingChange { Category = cat, Id = now.Id, Kind = ThingsDiffResult.KindNew, Details = empty ? "empty placeholder" : Describe(now) });
                    if (cat == ThingCategory.Item)
                    {
                        if (now.Flags?.Automap != null && now.Flags.Automap.Color != 0) result.MinimapChanged = true;
                        if (now.Flags?.Bank != null) result.GroundChanged = true;
                    }
                    continue;
                }

                string art = ArtDifference(was, now, touched);
                if (art.Length > 0)
                    result.Changes.Add(new ThingChange { Category = cat, Id = now.Id, Kind = ThingsDiffResult.KindArt, Details = art });

                var drawing = new List<string>();
                var gameplay = new List<string>();
                foreach (string field in FlagDifferences(was.Flags, now.Flags))
                {
                    (DrawingFlags.Contains(field) ? drawing : gameplay).Add(field);
                    if (cat != ThingCategory.Item) continue;
                    if (field == "automap") result.MinimapChanged = true;
                    if (field == "bank") result.GroundChanged = true;
                    if (OtbMirrored.Contains(field)) result.OtbMirroredDrawingChanged = true;
                }
                if ((was.Name ?? "") != (now.Name ?? ""))
                    gameplay.Add("market name");
                if (drawing.Count > 0)
                    result.Changes.Add(new ThingChange { Category = cat, Id = now.Id, Kind = ThingsDiffResult.KindDrawing, Details = string.Join(", ", drawing) });
                if (gameplay.Count > 0)
                    result.Changes.Add(new ThingChange { Category = cat, Id = now.Id, Kind = ThingsDiffResult.KindGameplay, Details = string.Join(", ", gameplay) });
            }
            foreach (var a in before)
                if (!seen.Contains(a.Id))
                    result.Changes.Add(new ThingChange { Category = cat, Id = a.Id, Kind = ThingsDiffResult.KindRemoved, Details = "not in the edited things" });
        }

        private static string Describe(Appearance a)
        {
            var g = a.FrameGroup.FirstOrDefault()?.SpriteInfo;
            if (g == null) return "";
            return $"{g.PatternWidth}x{g.PatternHeight}, layers {g.PatternLayers}, patterns {g.PatternX}x{g.PatternY}x{g.PatternZ}, phases {g.PatternFrames}, groups {a.FrameGroup.Count}";
        }

        // What the dat writes for a thing's look: frame groups, size, patterns, phases, animation, sprite ids.
        private static string ArtDifference(Appearance was, Appearance now, HashSet<uint>? touched)
        {
            var parts = new List<string>();
            if (was.FrameGroup.Count != now.FrameGroup.Count)
                parts.Add($"frame groups {was.FrameGroup.Count} -> {now.FrameGroup.Count}");
            else
            {
                for (int i = 0; i < was.FrameGroup.Count; i++)
                {
                    FrameGroup a = was.FrameGroup[i], b = now.FrameGroup[i];
                    SpriteInfo x = a.SpriteInfo ?? new SpriteInfo(), y = b.SpriteInfo ?? new SpriteInfo();
                    string g = was.FrameGroup.Count > 1 ? $"group {i} " : "";
                    if (a.FixedFrameGroup != b.FixedFrameGroup) parts.Add($"{g}type");
                    if (x.PatternWidth != y.PatternWidth || x.PatternHeight != y.PatternHeight || (x.PatternWidth > 1 || x.PatternHeight > 1) && x.PatternSize != y.PatternSize)
                        parts.Add($"{g}size {x.PatternWidth}x{x.PatternHeight} -> {y.PatternWidth}x{y.PatternHeight}");
                    if (x.PatternLayers != y.PatternLayers || x.PatternX != y.PatternX || x.PatternY != y.PatternY || x.PatternZ != y.PatternZ)
                        parts.Add($"{g}patterns {x.PatternLayers}/{x.PatternX}x{x.PatternY}x{x.PatternZ} -> {y.PatternLayers}/{y.PatternX}x{y.PatternY}x{y.PatternZ}");
                    if (x.PatternFrames != y.PatternFrames)
                        parts.Add($"{g}phases {x.PatternFrames} -> {y.PatternFrames}");
                    else if (x.PatternFrames > 1 && !AnimationEquals(x.Animation, y.Animation))
                        parts.Add($"{g}animation");
                    if (!x.SpriteId.SequenceEqual(y.SpriteId))
                    {
                        int differ = x.SpriteId.Count != y.SpriteId.Count ? Math.Max(x.SpriteId.Count, y.SpriteId.Count)
                            : x.SpriteId.Zip(y.SpriteId).Count(p => p.First != p.Second);
                        parts.Add($"{g}sprite ids ({differ})");
                    }
                    else if (touched != null && y.SpriteId.Any(touched.Contains))
                        parts.Add($"{g}sprite pixels");
                }
            }
            return string.Join(", ", parts);
        }

        private static bool AnimationEquals(SpriteAnimation? a, SpriteAnimation? b)
        {
            if (a == null || b == null) return a == b;
            if (a.AnimationMode != b.AnimationMode || a.LoopCount != b.LoopCount || a.DefaultStartPhase != b.DefaultStartPhase) return false;
            if (a.SpritePhase.Count != b.SpritePhase.Count) return false;
            for (int i = 0; i < a.SpritePhase.Count; i++)
                if (a.SpritePhase[i].DurationMin != b.SpritePhase[i].DurationMin || a.SpritePhase[i].DurationMax != b.SpritePhase[i].DurationMax)
                    return false;
            return true;
        }

        // Proto field names of AppearanceFlags that differ. An unset scalar equals its default (the dat reader only
        // ever sets flags to true), a message field compares by value.
        public static IEnumerable<string> FlagDifferences(AppearanceFlags? a, AppearanceFlags? b)
        {
            a ??= new AppearanceFlags();
            b ??= new AppearanceFlags();
            foreach (FieldDescriptor field in AppearanceFlags.Descriptor.Fields.InFieldNumberOrder())
            {
                object? x = field.Accessor.GetValue(a), y = field.Accessor.GetValue(b);
                bool same = field.IsRepeated
                    ? ((IList)x!).Cast<object>().SequenceEqual(((IList)y!).Cast<object>())
                    : Equals(x, y);
                if (!same) yield return field.Name;
            }
        }
    }
}
