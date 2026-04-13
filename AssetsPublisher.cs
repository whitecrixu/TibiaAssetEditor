using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Google.Protobuf;
using Newtonsoft.Json.Linq;
using Tibia.Protobuf.Appearances;

namespace Assets_Editor;

// Rewrites the Tibia client integrity chain after assets have been edited.
//
// Tibia/Canary validates local assets through three files in the client root:
//   - assets/catalog-content.json  — asset id ranges per file
//   - assets.json                  — per-file unpacked/packed sha256 + size
//   - assets.json.sha256           — sha256 of assets.json itself
//
// When we rewrite appearances.dat or add/modify sprite sheets, all three must be
// rebuilt or the client refuses to start with "Update failed".
//
// This publisher:
//   1. Serializes the in-memory Appearances protobuf, writes it under a new content-
//      addressed name `appearances-<hash>.dat`, and produces the matching .dat.lzma.
//   2. Updates catalog-content.json so the appearances entry points to the new file.
//   3. Walks every file referenced by catalog-content.json, hashes what's on disk,
//      and upserts the corresponding assets.json entry (packedhash/packedsize, and
//      unpackedhash/unpackedsize for .dat files).
//   4. Recomputes sha256(assets.json) and writes assets.json.sha256.
public static class AssetsPublisher
{
    public sealed record Result(string NewAppearancesFile, int FilesHashed, string TibiaRoot);

    public static async Task<Result> PublishAsync(
        Appearances appearances,
        string assetsPath,
        Action<string>? progress = null)
    {
        if (appearances == null) throw new ArgumentNullException(nameof(appearances));
        if (string.IsNullOrEmpty(assetsPath) || !Directory.Exists(assetsPath))
            throw new DirectoryNotFoundException($"assets path not found: {assetsPath}");

        // Tibia root is the parent of the assets folder.
        string tibiaRoot = Directory.GetParent(assetsPath.TrimEnd('/', '\\'))?.FullName
                           ?? throw new InvalidOperationException("can't find Tibia client root");
        string assetsJsonPath = Path.Combine(tibiaRoot, "assets.json");
        string assetsShaPath  = Path.Combine(tibiaRoot, "assets.json.sha256");
        if (!File.Exists(assetsJsonPath))
            throw new FileNotFoundException("assets.json not found alongside the assets/ folder", assetsJsonPath);

        return await Task.Run(() =>
        {
            progress?.Invoke("Serializing appearances…");
            byte[] datBytes;
            using (var ms = new MemoryStream())
            {
                appearances.WriteTo(ms);
                datBytes = ms.ToArray();
            }

            string newUnpackedHash = Sha256Hex(datBytes);
            string newDatName = $"appearances-{newUnpackedHash}.dat";
            string newDatPath = Path.Combine(assetsPath, newDatName);

            progress?.Invoke($"Writing {newDatName}…");
            File.WriteAllBytes(newDatPath, datBytes);

            // Remove any stale appearances-*.dat that doesn't match the new one.
            foreach (var stale in Directory.GetFiles(assetsPath, "appearances-*.dat"))
                if (!string.Equals(Path.GetFileName(stale), newDatName, StringComparison.Ordinal))
                    TryDelete(stale);

            // Produce the .dat.lzma sibling so the url entry references a real file
            // (the client updater uses it, and we need packedhash/packedsize for assets.json).
            string newDatLzmaPath = newDatPath + ".lzma";
            progress?.Invoke($"Compressing {newDatName}.lzma…");
            using (var inMs = new MemoryStream(datBytes))
                LZMA.CompressFileLZMA(inMs, newDatLzmaPath);
            foreach (var stale in Directory.GetFiles(assetsPath, "appearances-*.dat.lzma"))
                if (!string.Equals(Path.GetFileName(stale), newDatName + ".lzma", StringComparison.Ordinal))
                    TryDelete(stale);

            // ── Update catalog-content.json ──────────────────────────────────
            progress?.Invoke("Updating catalog-content.json…");
            var appEntry = MainWindow.catalog.FirstOrDefault(c => c.Type == "appearances");
            if (appEntry != null)
            {
                appEntry.File = newDatName;
            }
            else
            {
                MainWindow.catalog.Insert(0, new MainWindow.Catalog { Type = "appearances", File = newDatName });
            }
            string catalogPath = Path.Combine(assetsPath, "catalog-content.json");
            string catalogLzmaPath = catalogPath + ".lzma";
            // Tibia's catalog-content.json uses ALL-LOWERCASE property names
            // (type/file/spritetype/firstspriteid/lastspriteid/…). The parser is
            // case-sensitive: camelCase or PascalCase silently breaks sprite
            // resolution and the client renders no graphics after login.
            var catalogSettings = new Newtonsoft.Json.JsonSerializerSettings
            {
                NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore,
                ContractResolver = new LowercasePropertyResolver(),
            };
            byte[] catalogBytes = System.Text.Encoding.UTF8.GetBytes(
                Newtonsoft.Json.JsonConvert.SerializeObject(MainWindow.catalog, Newtonsoft.Json.Formatting.Indented, catalogSettings));
            File.WriteAllBytes(catalogPath, catalogBytes);

            progress?.Invoke("Compressing catalog-content.json.lzma…");
            using (var inMs = new MemoryStream(catalogBytes))
                LZMA.CompressFileLZMA(inMs, catalogLzmaPath);

            string catalogUnpackedHash = Sha256Hex(catalogBytes);
            long catalogUnpackedSize = catalogBytes.LongLength;
            byte[] catalogLzmaBytes = File.ReadAllBytes(catalogLzmaPath);
            string catalogPackedHash = Sha256Hex(catalogLzmaBytes);
            long catalogPackedSize = catalogLzmaBytes.LongLength;

            // ── Rebuild assets.json ──────────────────────────────────────────
            progress?.Invoke("Loading assets.json…");
            var root = JObject.Parse(File.ReadAllText(assetsJsonPath));
            var files = (JArray)root["files"]!;

            // Index existing entries by localfile for fast upsert + lookup.
            var byLocal = new System.Collections.Generic.Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var tok in files)
                if (tok is JObject jo && (string?)jo["localfile"] is string lf)
                    byLocal[lf] = jo;

            // Appearances entry — always rewritten.
            string newAppearancesLocal = $"assets/{newDatName}";
            string newAppearancesUrl   = $"assets/{newDatName}.lzma";
            long unpackedSize = datBytes.LongLength;
            byte[] lzmaBytes = File.ReadAllBytes(newDatLzmaPath);
            string packedHash = Sha256Hex(lzmaBytes);
            long packedSize = lzmaBytes.LongLength;

            // Drop the old appearances entry (any url starting with assets/appearances-).
            for (int i = files.Count - 1; i >= 0; i--)
            {
                if (files[i] is JObject jo && ((string?)jo["url"])?.StartsWith("assets/appearances-", StringComparison.Ordinal) == true)
                    files.RemoveAt(i);
            }
            files.Add(new JObject
            {
                ["url"]           = newAppearancesUrl,
                ["unpackedhash"]  = newUnpackedHash,
                ["unpackedsize"]  = unpackedSize,
                ["packedhash"]    = packedHash,
                ["packedsize"]    = packedSize,
                ["localfile"]     = newAppearancesLocal,
            });

            // catalog-content.json has its own entry in assets.json with unpackedhash.
            const string catalogLocal = "assets/catalog-content.json";
            const string catalogUrl   = "assets/catalog-content.json.lzma";
            if (byLocal.TryGetValue(catalogLocal, out var catalogExisting))
            {
                catalogExisting["url"]          = catalogUrl;
                catalogExisting["unpackedhash"] = catalogUnpackedHash;
                catalogExisting["unpackedsize"] = catalogUnpackedSize;
                catalogExisting["packedhash"]   = catalogPackedHash;
                catalogExisting["packedsize"]   = catalogPackedSize;
                catalogExisting["localfile"]    = catalogLocal;
            }
            else
            {
                var entry = new JObject
                {
                    ["url"]          = catalogUrl,
                    ["unpackedhash"] = catalogUnpackedHash,
                    ["unpackedsize"] = catalogUnpackedSize,
                    ["packedhash"]   = catalogPackedHash,
                    ["packedsize"]   = catalogPackedSize,
                    ["localfile"]    = catalogLocal,
                };
                files.Add(entry);
                byLocal[catalogLocal] = entry;
            }

            // ── Sprite sheets: reconcile packedhash/packedsize against disk ──
            // Only for catalog entries whose file actually changed (size mismatch against
            // the existing assets.json entry). We still hash everything that's missing.
            progress?.Invoke("Reconciling sprite sheet hashes…");
            int hashed = 1; // +appearances
            foreach (var cat in MainWindow.catalog)
            {
                if (cat.Type != "sprite") continue;
                if (string.IsNullOrEmpty(cat.File)) continue;

                string localfile = $"assets/{cat.File}";
                string diskPath = Path.Combine(assetsPath, cat.File);
                if (!File.Exists(diskPath)) continue;

                var fi = new FileInfo(diskPath);
                byLocal.TryGetValue(localfile, out var existing);
                long existingSize = (long?)existing?["packedsize"] ?? -1;

                if (existing != null && existingSize == fi.Length)
                {
                    // Size matches — trust the existing packedhash. Fast path.
                    continue;
                }

                byte[] bytes = File.ReadAllBytes(diskPath);
                string ph = Sha256Hex(bytes);
                hashed++;

                if (existing == null)
                {
                    var entry = new JObject
                    {
                        ["url"]        = localfile,
                        ["unpack"]     = false,
                        ["packedhash"] = ph,
                        ["packedsize"] = fi.Length,
                        ["localfile"]  = localfile,
                    };
                    files.Add(entry);
                    byLocal[localfile] = entry;
                }
                else
                {
                    existing["packedhash"] = ph;
                    existing["packedsize"] = fi.Length;
                }
            }

            progress?.Invoke("Writing assets.json…");
            // Tibia's assets.json is compact (no pretty-printing). Match that exactly so
            // the size/hash is stable between runs.
            string newAssetsJson = root.ToString(Newtonsoft.Json.Formatting.None);
            File.WriteAllText(assetsJsonPath, newAssetsJson);

            progress?.Invoke("Writing assets.json.sha256…");
            string assetsHash = Sha256Hex(System.Text.Encoding.UTF8.GetBytes(newAssetsJson));
            File.WriteAllText(assetsShaPath, assetsHash);

            return new Result(newDatName, hashed, tibiaRoot);
        });
    }

    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private class LowercasePropertyResolver : Newtonsoft.Json.Serialization.DefaultContractResolver
    {
        protected override string ResolvePropertyName(string propertyName) => propertyName.ToLower();
    }
}
