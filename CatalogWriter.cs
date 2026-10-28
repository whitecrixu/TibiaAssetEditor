using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace Assets_Editor;

// Single place that serializes catalog-content.json and writes asset files to disk.
//
// Tibia's catalog-content.json uses ALL-LOWERCASE property names
// (type/file/spritetype/firstspriteid/lastspriteid/…). The parser is case-sensitive:
// camelCase or PascalCase silently breaks sprite id resolution and the client logs in
// fine but renders no graphics. Every writer of the catalog (DatEditor compile,
// SprEditor, AssetsPublisher) must go through here.
//
// Writes are atomic (temp file in the same directory + File.Move overwrite), and the
// first time a file is replaced its untouched original is kept as "<file>-orig".
public static class CatalogWriter
{
    public const string CatalogFileName = "catalog-content.json";

    private static readonly JsonSerializerSettings Settings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        DefaultValueHandling = DefaultValueHandling.Ignore,
        ContractResolver = new LowercaseContractResolver(),
    };

    public static byte[] Serialize(IEnumerable<MainWindow.Catalog> catalog) =>
        Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(catalog, Formatting.Indented, Settings));

    // Serialize the catalog and write <assetsPath>/catalog-content.json atomically.
    // Returns the bytes written (callers that hash/compress the catalog reuse them).
    public static byte[] Write(string assetsPath, IEnumerable<MainWindow.Catalog> catalog)
    {
        byte[] bytes = Serialize(catalog);
        WriteAllBytesAtomic(Path.Combine(assetsPath, CatalogFileName), bytes);
        return bytes;
    }

    // Write `bytes` to a temp sibling and move it over `path`, so a crash or a full disk
    // never leaves a half-written file behind. Backs up the original once.
    public static void WriteAllBytesAtomic(string path, byte[] bytes)
    {
        BackupOnce(path);
        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); }
                catch (Exception ex) { MainWindow.Log($"Could not remove temp file {tmp}: {ex.Message}", "Warning"); }
            }
        }
    }

    // Same as WriteAllBytesAtomic but the content is produced by a writer callback
    // (e.g. LZMA compression or protobuf WriteTo) instead of an in-memory buffer.
    public static void WriteAtomic(string path, Action<Stream> write)
    {
        using var ms = new MemoryStream();
        write(ms);
        WriteAllBytesAtomic(path, ms.ToArray());
    }

    // Keep the very first version of a file as "<file>-orig". Never overwritten, so the
    // pristine client file survives any number of compiles.
    public static void BackupOnce(string path)
    {
        string backup = path + "-orig";
        if (File.Exists(path) && !File.Exists(backup))
            File.Copy(path, backup);
    }

    private sealed class LowercaseContractResolver : Newtonsoft.Json.Serialization.DefaultContractResolver
    {
        protected override string ResolvePropertyName(string propertyName) => propertyName.ToLower();
    }
}
