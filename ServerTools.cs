using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tibia.Protobuf.Appearances;

namespace Assets_Editor
{
    // Shared pieces of the Project Wolf Server Tools (ServerToolsWindow): findings, hashes, reading a legacy dat
    // with explicit flags, the things guard and the export for the owner's approval. Nothing in here touches
    // MainWindow statics or Avalonia, so the console self-test can run it without a UI.

    public enum Severity { Error, Warning, Info }

    public sealed class Finding
    {
        public Severity Severity { get; init; }
        public string Category { get; init; } = "";
        public uint Id { get; init; }
        public string Message { get; init; } = "";

        public string SeverityText => Severity.ToString();
        public string IdText => Id == 0 ? "" : Id.ToString();

        public string ToTsvLine() => $"{Severity}\t{Category}\t{IdText}\t{Message.Replace('\t', ' ').Replace('\n', ' ')}";
    }

    public static class ServerTools
    {
        public const string TsvHeader = "severity\tcategory\tid\tmessage";

        public static string ToTsv(IEnumerable<Finding> findings)
        {
            var sb = new StringBuilder(TsvHeader).Append('\n');
            foreach (var f in findings)
                sb.Append(f.ToTsvLine()).Append('\n');
            return sb.ToString();
        }

        public static string Sha256File(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        // ---- Tibia.otfi (same keys as items_otb.py read_otfi) ------------------------------------------------

        public static Dictionary<string, bool> ReadOtfi(string path)
        {
            var flags = new Dictionary<string, bool> { ["extended"] = false, ["frame-durations"] = false, ["frame-groups"] = false };
            try
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    string key = line[..colon].Trim();
                    if (flags.ContainsKey(key))
                        flags[key] = line[(colon + 1)..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return flags;
        }

        public static PresetSettings PresetFromOtfi(string otfiPath)
        {
            var f = ReadOtfi(otfiPath);
            return new PresetSettings { Extended = f["extended"], FrameDurations = f["frame-durations"], FrameGroups = f["frame-groups"] };
        }

        // ---- legacy dat with explicit flags -------------------------------------------------------------------

        // LegacyAppearance.ReadLegacyDat takes the flags from MainWindow's current preset; this reads a second
        // dat (the "-bak" original, a file on disk) with the flags the caller passes.
        public static Appearances ReadLegacyDat(string path, VersionInfo versionInfo, PresetSettings preset, out uint signature)
        {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream);
            DatInfo info = DatStructure.ReadAppearanceInfo(r);
            signature = info.Signature;
            var result = new Appearances();
            void ReadList(Google.Protobuf.Collections.RepeatedField<Appearance> list, APPEARANCE_TYPE type, int first, int last)
            {
                for (int i = first; i <= last; i++)
                {
                    if (versionInfo.UseRDBytes) r.ReadUInt16();
                    var a = DatStructure.ReadAppearance(r, type, versionInfo, preset);
                    a.Id = (uint)i;
                    list.Add(a);
                }
            }
            ReadList(result.Object, APPEARANCE_TYPE.AppearanceObject, 100, info.ObjectCount);
            ReadList(result.Outfit, APPEARANCE_TYPE.AppearanceOutfit, 1, info.OutfitCount);
            ReadList(result.Effect, APPEARANCE_TYPE.AppearanceEffect, 1, info.EffectCount);
            ReadList(result.Missile, APPEARANCE_TYPE.AppearanceMissile, 1, info.MissileCount);
            if (stream.Position != stream.Length)
                throw new InvalidDataException($"{Path.GetFileName(path)}: {stream.Length - stream.Position} trailing bytes; wrong structure or Tibia.otfi flags");
            return result;
        }

        // ---- things guard -----------------------------------------------------------------------------------

        public static readonly string[] ThingsFiles = ["Tibia.dat", "Tibia.spr", "Tibia.otfi"];

        // <project>/client is the frozen OTCv8 tree when a live <project>/client-mehah sits next to it
        private static readonly Regex ClientTree = new(@"[/\\]client[/\\]", RegexOptions.IgnoreCase);

        public static bool IsFrozenClientTree(string path) => MehahThingsFor(path) != null;

        public static string? MehahThingsFor(string frozenPath)
        {
            string full = Path.GetFullPath(frozenPath);
            var m = ClientTree.Match(full);
            if (!m.Success) return null;
            string sep = full[m.Index].ToString();
            string candidate = full[..m.Index] + sep + "client-mehah" + sep + "data" + sep + "things" + sep + "860";
            return Directory.Exists(candidate) ? candidate : null;
        }

        public sealed class ManifestRow
        {
            public string Name = "";
            public string? Expected;
            public string? Actual;
            public string Status = "";
        }

        // The "files" list of a client update channel manifest. Accepts a build manifest (builds/N.json, format 1,
        // what check_things_manifest.py reads), a gzip of one, or the channel's signed manifest.json (format 2),
        // which points at manifests/<sha>.json.gz next to it.
        public static (int? build, Dictionary<string, string> files) LoadManifestFiles(string manifestPath)
        {
            using JsonDocument doc = JsonDocument.Parse(ReadMaybeGzip(manifestPath));
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("files", out _) && root.TryGetProperty("manifest", out JsonElement inner) && inner.ValueKind == JsonValueKind.String)
            {
                string innerPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? "", inner.GetString()!);
                return LoadManifestFiles(innerPath);
            }
            int? build = root.TryGetProperty("build", out JsonElement b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : null;
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("files", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement f in list.EnumerateArray())
                {
                    if (f.TryGetProperty("path", out JsonElement p) && f.TryGetProperty("sha256", out JsonElement s))
                        files[p.GetString() ?? ""] = (s.GetString() ?? "").ToLowerInvariant();
                }
            }
            return (build, files);
        }

        private static byte[] ReadMaybeGzip(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B)
            {
                using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                gz.CopyTo(ms);
                return ms.ToArray();
            }
            return data;
        }

        // Same logic as client-mehah/tools/wolfots/check_things_manifest.py: every data/things/860/Tibia.* entry of
        // the manifest must have the SHA-256 of the file in the things directory; a name the manifest does not list
        // is not compared. `hashes` lets the caller reuse hashes it already computed (name -> sha).
        public static List<ManifestRow> CompareWithManifest(string thingsDir, string manifestPath, IReadOnlyDictionary<string, string>? hashes = null)
        {
            var (_, files) = LoadManifestFiles(manifestPath);
            var rows = new List<ManifestRow>();
            foreach (string name in ThingsFiles)
            {
                var row = new ManifestRow { Name = name };
                files.TryGetValue("data/things/860/" + name, out row.Expected);
                string local = Path.Combine(thingsDir, name);
                if (hashes != null && hashes.TryGetValue(name, out string? h))
                    row.Actual = h;
                else if (File.Exists(local))
                    row.Actual = Sha256File(local);
                if (row.Expected == null) row.Status = "not in manifest";
                else if (row.Actual == null) row.Status = "missing locally";
                else row.Status = row.Expected == row.Actual ? "same" : "DIFFERENT";
                rows.Add(row);
            }
            return rows;
        }

        public static int? ManifestBuild(string manifestPath)
        {
            try { return LoadManifestFiles(manifestPath).build; }
            catch (Exception) { return null; }
        }

        // ---- export for owner approval (server files inbox layout) -------------------------------------------

        // Port of TFS-1.5-Downgrades-8.60/tools/newera_files/newera_files/checks.py forbidden_reason(); the size
        // limit (256 MB) and the 2000-files-per-apply limit are from FILES_BRIEF.md / tools/newera_files/README.md.
        public static readonly string[] AllowedExtensions = [".lua", ".xml", ".otbm", ".otb", ".json", ".txt", ".png"];
        private static readonly string[] ForbiddenNames = ["config.lua*", "key*.pem", "*.sql"];
        private static readonly string[] ForbiddenDirs = ["data/logs/", "data/reports/"];
        public const long MaxUploadSize = 256L * 1024 * 1024;
        public const int MaxFilesPerApply = 2000;
        private const int MaxPath = 512;

        public static string? ForbiddenReason(string rel, long? maxSize = MaxUploadSize, long? size = null)
        {
            if (string.IsNullOrEmpty(rel) || rel.StartsWith('/') || rel.Contains('\\'))
                return "not a relative path";
            string[] parts = rel.Split('/');
            if (parts.Any(p => p is "" or "." or ".."))
                return "'.' or '..' in the path";
            if (parts.Any(p => p.StartsWith('.')))
                return "hidden files and folders are not accepted";
            if (rel.Any(ch => ch < 32 || ch == 127))
                return "control characters in the name";
            if (Encoding.UTF8.GetByteCount(rel) > MaxPath)
                return $"the path is longer than {MaxPath} bytes";
            string name = parts[^1];
            foreach (string pattern in ForbiddenNames)
                if (GlobMatch(name.ToLowerInvariant(), pattern))
                    return $"{pattern} files are never taken from uploads";
            if (parts[0] != "data" || parts.Length < 2)
                return "only files under data/ are accepted";
            foreach (string prefix in ForbiddenDirs)
                if (rel.StartsWith(prefix, StringComparison.Ordinal))
                    return $"{prefix} is written by the server itself";
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
                return $"file type {(ext.Length > 0 ? ext : "(none)")} is not accepted (allowed: {string.Join(" ", AllowedExtensions)})";
            if (maxSize != null && size != null && size > maxSize)
                return $"larger than {maxSize / (1024 * 1024)} MB";
            return null;
        }

        // fnmatch with only '*' (all patterns above use only that)
        private static bool GlobMatch(string text, string pattern)
        {
            string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(text, regex, RegexOptions.Singleline);
        }

        // Port of checks.py reload_parts()/needs(): what an applied file needs to take effect.
        public static string Needs(string rel)
        {
            string ext = Path.GetExtension(rel).ToLowerInvariant();
            if (ext is ".txt" or ".png") return "none";
            if (rel.StartsWith("data/migrations/")) return "restart";
            if (rel == "data/global.lua" || rel.StartsWith("data/lib/")) return "reload (global, scripts)";
            if (rel == "data/server_config.lua" || rel.StartsWith("data/scripts/") || rel.StartsWith("data/monster/")) return "reload (scripts)";
            if (rel.StartsWith("data/events/")) return "reload (events)";
            if (rel.StartsWith("data/npc/")) return "reload (npcs)";
            if (rel == "data/XML/quests.xml") return "reload (quests)";
            if (rel.StartsWith("data/raids/")) return "reload (raids)";
            if (rel.StartsWith("data/chatchannels/")) return "reload (chat)";
            return "restart";
        }

        public sealed class ExportRequest
        {
            public string ServerRoot = "";               // directory that contains data/
            public List<string> ServerFiles = [];        // absolute paths, each under ServerRoot/data
            public string? ThingsDir;                    // directory with Tibia.dat/.spr/.otfi, null = no client part
            public string OutputDir = "";
        }

        public sealed class ExportResult
        {
            public List<string> Lines = [];
            public List<string> Refused = [];
            public int Copied;
            public bool Ok => Refused.Count == 0;
        }

        // Writes OutputDir/server/data/... (the inbox layout: upload server/ 's content into inbox/), OutputDir/client/
        // Tibia.*, SHA256SUMS (sha256sum -c format, paths relative to OutputDir) and README.txt with the order.
        public static ExportResult ExportForApproval(ExportRequest req, IProgress<string>? progress = null)
        {
            var result = new ExportResult();
            string serverRoot = req.ServerRoot.Length > 0 ? Path.GetFullPath(req.ServerRoot) : "";
            var plan = new List<(string src, string rel, string needs)>();
            if (serverRoot.Length == 0 && req.ServerFiles.Count > 0)
                result.Refused.Add("server files without a server root");
            else foreach (string file in req.ServerFiles.Distinct())
            {
                string full = Path.GetFullPath(file);
                string rel = Path.GetRelativePath(serverRoot, full).Replace(Path.DirectorySeparatorChar, '/');
                var fi = new FileInfo(full);
                string? reason = !fi.Exists ? "file not found"
                    : fi.Attributes.HasFlag(FileAttributes.ReparsePoint) ? "symlinks are not accepted"
                    : ForbiddenReason(rel, MaxUploadSize, fi.Length);
                if (reason != null)
                    result.Refused.Add($"{rel}: {reason}");
                else
                    plan.Add((full, rel, Needs(rel)));
            }
            if (plan.Count > MaxFilesPerApply)
                result.Refused.Add($"{plan.Count} server files; at most {MaxFilesPerApply} per apply");
            var client = new List<string>();
            if (req.ThingsDir != null)
            {
                foreach (string name in ThingsFiles)
                {
                    string src = Path.Combine(req.ThingsDir, name);
                    if (File.Exists(src)) client.Add(src);
                    else result.Refused.Add($"client/{name}: not found in {req.ThingsDir}");
                }
                if (IsFrozenClientTree(req.ThingsDir))
                    result.Refused.Add($"client things come from the frozen OTCv8 tree ({req.ThingsDir}); use client-mehah/data/things/860");
            }
            if (!result.Ok)
                return result;
            if (plan.Count == 0 && client.Count == 0)
            {
                result.Refused.Add("nothing to export");
                return result;
            }

            string outDir = Path.GetFullPath(req.OutputDir);
            if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
            {
                result.Refused.Add($"{outDir} is not empty; pick a new or empty folder");
                return result;
            }
            Directory.CreateDirectory(outDir);

            var sums = new List<string>();
            foreach (var (src, rel, _) in plan)
            {
                string dst = Path.Combine(outDir, "server", rel.Replace('/', Path.DirectorySeparatorChar));
                sums.Add($"{CopyVerified(src, dst)}  server/{rel}");
                result.Copied++;
                progress?.Report($"server/{rel}");
            }
            foreach (string src in client)
            {
                string name = Path.GetFileName(src);
                sums.Add($"{CopyVerified(src, Path.Combine(outDir, "client", name))}  client/{name}");
                result.Copied++;
                progress?.Report($"client/{name}");
            }
            File.WriteAllText(Path.Combine(outDir, "SHA256SUMS"), string.Join("\n", sums) + "\n");
            File.WriteAllText(Path.Combine(outDir, "README.txt"), BuildReadme(plan, client, req));
            result.Lines.AddRange(sums);
            return result;
        }

        private static string CopyVerified(string src, string dst)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            string tmp = dst + ".tmp";
            File.Copy(src, tmp, true);
            string a = Sha256File(src), b = Sha256File(tmp);
            if (a != b)
            {
                File.Delete(tmp);
                throw new IOException($"{src} changed while it was copied");
            }
            File.Move(tmp, dst, true);
            return a;
        }

        private static string BuildReadme(List<(string src, string rel, string needs)> plan, List<string> client, ExportRequest req)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Project Wolf server files for the owner's approval");
            sb.AppendLine($"Prepared {DateTime.Now:yyyy-MM-dd HH:mm} with Tibia MultiTool Editor (Project Wolf Server Tools).");
            sb.AppendLine($"Server tree: {req.ServerRoot}");
            if (req.ThingsDir != null) sb.AppendLine($"Client things: {req.ThingsDir}");
            sb.AppendLine("Check the copies first: sha256sum -c SHA256SUMS");
            sb.AppendLine();
            sb.AppendLine("ORDER (do not swap the steps):");
            int step = 1;
            if (client.Count > 0)
            {
                sb.AppendLine($"{step++}. Publish the client update channel build FIRST. client/ holds data/things/860/Tibia.dat, Tibia.spr");
                sb.AppendLine("   and Tibia.otfi; build a new channel build that carries them, then confirm with");
                sb.AppendLine("   python3 -I client-mehah/tools/wolfots/check_things_manifest.py <these things> <channel>/builds/<N>.json");
                sb.AppendLine("   An older client that receives an item id or looktype it does not know cannot parse that packet, so the");
                sb.AppendLine("   players' clients must have the new things before the server sends them.");
            }
            if (plan.Count > 0)
            {
                sb.AppendLine($"{step++}. Then upload the server files: the CONTENT of server/ goes into the server files inbox/ with the");
                sb.AppendLine("   same folders (inbox/data/items/items.otb, ...). Approve them in the admin panel.");
                bool restart = plan.Any(p => p.needs == "restart");
                if (restart)
                    sb.AppendLine($"{step++}. Restart the server (\"restart when empty\"): items.otb / items.xml / outfits.xml are only read at start.");
                else
                    sb.AppendLine($"{step++}. Reload as listed below; no restart needed.");
            }
            sb.AppendLine();
            if (plan.Count > 0)
            {
                sb.AppendLine("Server files (what each needs after the apply):");
                foreach (var (_, rel, needs) in plan.OrderBy(p => p.rel, StringComparer.Ordinal))
                    sb.AppendLine($"  server/{rel}  -> {needs}");
            }
            if (client.Count > 0)
            {
                sb.AppendLine("Client files (update channel, data/things/860/):");
                foreach (string c in client)
                    sb.AppendLine($"  client/{Path.GetFileName(c)}");
            }
            return sb.ToString();
        }
    }
}
