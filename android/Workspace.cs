using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Tibia.Protobuf.Appearances;
using static Assets_Editor.OTB;

namespace Assets_Editor;

/// <summary>
/// The files being edited. Android gives apps no paths into shared folders, so a picked folder is copied
/// into the app's own folder (the workspace) and edited there with the same code as the desktop editor;
/// "Save" writes the workspace files and then copies the changed ones back into the picked folder, after
/// keeping a "-bak" copy of each original there.
/// </summary>
public static class Workspace
{
    public static string Root { get; set; } = "";
    public static string ClientDir => Path.Combine(Root, "client");
    public static string ServerDir => Path.Combine(Root, "server");

    public static readonly string[] ClientFiles = ["Tibia.dat", "Tibia.spr", "Tibia.otfi"];
    public static readonly string[] ServerFiles = ["items.otb", "items.xml"];

    public static Appearances? Things { get; private set; }
    public static uint DatSignature { get; private set; }
    public static KnownClients.ClientVersion? Client { get; private set; }
    public static int Structure { get; private set; } = 5;
    public static VersionInfo? Version { get; private set; }
    public static SpriteStorage? Sprites { get; private set; }

    public static List<ServerItem>? OtbItems { get; private set; }
    private static OTBReader? _otbHeader;
    private static Dictionary<ushort, ServerItem> _byClientId = new();

    public static bool DatDirty { get; set; }
    public static bool OtbDirty { get; set; }
    public static bool XmlDirty { get; set; }
    public static bool HasChanges => DatDirty || OtbDirty || XmlDirty;

    public static bool ClientLoaded => Things != null && Sprites != null;
    public static bool ServerLoaded => OtbItems != null || ItemsXml.IsLoaded;

    // ---- settings: the picked folders, kept as storage bookmarks -------------------------------------------

    public sealed class SettingsData
    {
        public string? ClientBookmark { get; set; }
        public string? ClientFolderName { get; set; }
        public string? ServerBookmark { get; set; }
        public string? ServerFolderName { get; set; }
        public string? SkippedUpdate { get; set; }
    }

    public static SettingsData Settings { get; private set; } = new();

    private static string SettingsPath => AppPaths.UserFile("settings.json");

    public static void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                Settings = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch (Exception ex)
        {
            AppLog.Add("Settings: " + ex.Message);
        }
    }

    public static void SaveSettings()
    {
        try
        {
            AppPaths.WriteAllTextAtomic(SettingsPath, JsonSerializer.Serialize(Settings));
        }
        catch (Exception ex)
        {
            AppLog.Add("Settings: " + ex.Message);
        }
    }

    // ---- loading ----------------------------------------------------------------------------------------

    public static bool HasClientFiles => File.Exists(Path.Combine(ClientDir, "Tibia.dat")) && File.Exists(Path.Combine(ClientDir, "Tibia.spr"));
    public static bool HasServerFiles => ServerFiles.Any(f => File.Exists(Path.Combine(ServerDir, f)));

    public static void LoadClient(IProgress<int>? progress = null)
    {
        string dat = Path.Combine(ClientDir, "Tibia.dat");
        string spr = Path.Combine(ClientDir, "Tibia.spr");
        string otfi = Path.Combine(ClientDir, "Tibia.otfi");

        uint signature;
        using (var r = new BinaryReader(File.OpenRead(dat)))
            signature = DatStructure.ReadAppearanceInfo(r).Signature;

        // the otfi describes the file exactly; without one, the official client's flags come first
        KnownClients.ClientVersion? client = KnownClients.FromDat(signature);
        PresetSettings first = File.Exists(otfi)
            ? ReadOtfi(otfi)
            : new PresetSettings { Extended = client?.Extended ?? true, FrameDurations = client?.FrameDurations ?? true, FrameGroups = client?.FrameGroups ?? true };

        var (legacy, preset) = DetectLayout(dat, client?.Structure, first)
            ?? throw new InvalidDataException($"Tibia.dat (signature 0x{signature:X8}) does not match any known structure and layout flags.");
        int structure = preset.Version;
        AppLog.Add($"Tibia.dat 0x{signature:X8}: {(client != null ? "Tibia " + client.Name : "unknown client")}, read as {MainWindow.datStructure.GetVersionInfo(structure).Name}, " +
                   $"extended={preset.Extended}, frame durations={preset.FrameDurations}, frame groups={preset.FrameGroups}.");
        MainWindow.Preset = preset;

        var storage = new SpriteStorage(spr, preset.Transparent, progress);
        storage.LoadSprites(preset.Extended);

        Sprites?.CloseFile();
        Things = legacy.Appearances;
        DatSignature = legacy.Signature;
        Client = client;
        Structure = structure;
        Version = MainWindow.datStructure.GetVersionInfo(structure);
        Sprites = storage;
        MainWindow.MainSprStorage = storage;
        DatDirty = false;
        AppLog.Add($"Client loaded: {Things.Object.Count} items, {Things.Outfit.Count} outfits, {Things.Effect.Count} effects, {Things.Missile.Count} missiles, {storage.SpriteCount} sprites.");
    }

    /// <summary>
    /// Like the desktop loader: the known structure for the signature with the otfi flags first, then every
    /// structure with those flags, then every combination of the three layout flags. A read must end exactly
    /// at the end of the file, so a wrong guess is never accepted.
    /// </summary>
    private static (LegacyAppearance, PresetSettings)? DetectLayout(string dat, int? knownStructure, PresetSettings flags)
    {
        var structures = MainWindow.datStructure.GetAllVersions().Select(v => v.Structure).Distinct()
            .OrderBy(s => s == knownStructure ? 0 : 1)
            .ThenByDescending(s => s == 5) // 8.60 next, then the rest
            .ToList();

        var combos = new List<(bool extended, bool durations, bool groups)> { (flags.Extended, flags.FrameDurations, flags.FrameGroups) };
        for (int c = 0; c < 8; c++)
        {
            var combo = ((c & 1) != 0, (c & 2) != 0, (c & 4) != 0);
            if (!combos.Contains(combo))
                combos.Add(combo);
        }

        foreach (var (extended, durations, groups) in combos)
            foreach (int structure in structures)
            {
                var preset = new PresetSettings
                {
                    Version = structure,
                    Extended = extended,
                    FrameDurations = durations,
                    FrameGroups = groups,
                    Transparent = flags.Transparent,
                };
                try
                {
                    var legacy = new LegacyAppearance();
                    legacy.ReadLegacyDat(dat, structure, preset);
                    return (legacy, preset);
                }
                catch
                {
                    // wrong guess, try the next one
                }
            }
        return null;
    }

    // the keys of a Tibia.otfi
    private static PresetSettings ReadOtfi(string path)
    {
        var preset = new PresetSettings();
        foreach (string line in File.ReadAllLines(path))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string key = line[..colon].Trim();
            bool value = line[(colon + 1)..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            switch (key)
            {
                case "extended": preset.Extended = value; break;
                case "frame-durations": preset.FrameDurations = value; break;
                case "frame-groups": preset.FrameGroups = value; break;
                case "transparency": preset.Transparent = value; break;
            }
        }
        return preset;
    }

    public static void LoadServer()
    {
        string otb = Path.Combine(ServerDir, "items.otb");
        string xml = Path.Combine(ServerDir, "items.xml");

        OtbItems = null;
        _otbHeader = null;
        _byClientId = new();
        if (File.Exists(otb))
        {
            var reader = new OTBReader();
            if (!reader.Read(otb))
                throw new InvalidDataException("items.otb could not be read.");
            _otbHeader = reader;
            OtbItems = reader.Items;
            foreach (ServerItem item in OtbItems)
                _byClientId.TryAdd(item.ClientId, item);
        }

        if (File.Exists(xml) && !ItemsXml.Load(xml))
            throw new InvalidDataException("items.xml could not be read.");

        OtbDirty = XmlDirty = false;
        AppLog.Add($"Server loaded: {OtbItems?.Count ?? 0} items.otb entries, {ItemsXml.Items.Count} items.xml entries.");
    }

    public static ServerItem? ServerItemFor(uint clientId) =>
        _byClientId.TryGetValue((ushort)clientId, out var item) ? item : null;

    // without an otb, server id = client id; with one the mapping is taken from it
    public static ushort ServerIdFor(uint clientId) => ServerItemFor(clientId)?.ServerId ?? (ushort)clientId;

    public static string? ItemName(uint clientId) => ItemsXml.GetName(ServerIdFor(clientId));

    // ---- import: picked folder -> workspace --------------------------------------------------------------

    /// <summary>Finds the folder that holds the given files: the picked one or one of its subfolders.</summary>
    public static async Task<IStorageFolder?> FindFolderWith(IStorageFolder folder, string requiredFile, int depth = 4)
    {
        var subfolders = new List<IStorageFolder>();
        await foreach (IStorageItem item in folder.GetItemsAsync())
        {
            if (item is IStorageFile && item.Name.Equals(requiredFile, StringComparison.OrdinalIgnoreCase))
                return folder;
            if (item is IStorageFolder sub)
                subfolders.Add(sub);
        }
        if (depth == 0)
            return null;

        // likely places first: data/things/<version>, data/items
        subfolders = subfolders.OrderBy(f => f.Name is "data" or "things" or "items" or "860" ? 0 : 1).ToList();
        foreach (IStorageFolder sub in subfolders)
        {
            IStorageFolder? found = await FindFolderWith(sub, requiredFile, depth - 1);
            if (found != null)
                return found;
        }
        return null;
    }

    public static async Task<Dictionary<string, IStorageFile>> FilesIn(IStorageFolder folder, IEnumerable<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, IStorageFile>(StringComparer.OrdinalIgnoreCase);
        await foreach (IStorageItem item in folder.GetItemsAsync())
            if (item is IStorageFile file && wanted.Contains(file.Name))
                found[file.Name] = file;
        return found;
    }

    public static async Task CopyIn(IStorageFolder folder, string targetDir, string[] names, IProgress<(string file, int percent)>? progress)
    {
        Directory.CreateDirectory(targetDir);
        var files = await FilesIn(folder, names);
        foreach (string name in names)
        {
            string target = Path.Combine(targetDir, name);
            if (!files.TryGetValue(name, out IStorageFile? file))
            {
                File.Delete(target);
                continue;
            }

            string temp = target + ".part";
            await using (Stream source = await file.OpenReadAsync())
            await using (FileStream output = File.Create(temp))
            {
                long total = (await file.GetBasicPropertiesAsync()).Size is ulong size ? (long)size : 0;
                byte[] buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total > 0)
                        progress?.Report((name, (int)(done * 100 / total)));
                }
            }
            File.Move(temp, target, overwrite: true);
            File.Delete(target + "-bak");
        }
    }

    // ---- save: workspace files, then the picked folder ----------------------------------------------------

    /// <summary>Writes the changed files into the workspace. Returns the names of the files written.</summary>
    public static List<(string dir, string name)> SaveLocal()
    {
        var written = new List<(string, string)>();

        if (DatDirty && Things != null)
        {
            string dat = Path.Combine(ClientDir, "Tibia.dat");
            KeepLocalBackup(dat);
            if (!LegacyAppearance.WriteLegacyDat(dat, DatSignature, Things, Structure))
                throw new IOException("Tibia.dat was not written (see the log).");

            // read it back with the same flags: a dat the client cannot read must never leave the phone
            var check = new LegacyAppearance();
            check.ReadLegacyDat(dat, Structure, MainWindow.Preset);
            if (check.Appearances.Object.Count != Things.Object.Count || check.Appearances.Outfit.Count != Things.Outfit.Count)
                throw new IOException("Tibia.dat verification failed.");
            DatDirty = false;
            written.Add((ClientDir, "Tibia.dat"));
        }

        if (OtbDirty && OtbItems != null && _otbHeader != null)
        {
            string otb = Path.Combine(ServerDir, "items.otb");
            KeepLocalBackup(otb);
            var writer = new OTBWriter(OtbItems)
            {
                MajorVersion = _otbHeader.MajorVersion,
                MinorVersion = _otbHeader.MinorVersion,
                BuildNumber = _otbHeader.BuildNumber,
                ClientVersion = _otbHeader.ClientVersion,
            };
            if (!writer.Write(otb))
                throw new IOException("items.otb was not written: " + writer.LastError);
            OtbDirty = false;
            written.Add((ServerDir, "items.otb"));
        }

        if (XmlDirty && ItemsXml.IsLoaded)
        {
            string xml = Path.Combine(ServerDir, "items.xml");
            KeepLocalBackup(xml);
            if (!ItemsXml.Save(xml))
                throw new IOException("items.xml was not written (see the log).");
            XmlDirty = false;
            written.Add((ServerDir, "items.xml"));
        }
        return written;
    }

    // the first original of each workspace file, so a bad save can be undone on the phone
    private static void KeepLocalBackup(string path)
    {
        string backup = path + "-bak";
        if (File.Exists(path) && !File.Exists(backup))
            File.Copy(path, backup);
    }

    /// <summary>Copies workspace files into the picked folder, keeping the folder's previous version as "-bak".</summary>
    public static async Task CopyOut(IStorageFolder folder, string sourceDir, IEnumerable<string> names)
    {
        var list = names.ToList();
        var existing = await FilesIn(folder, list.SelectMany(n => new[] { n, n + "-bak" }));
        foreach (string name in list)
        {
            if (existing.TryGetValue(name, out IStorageFile? current))
            {
                byte[] previous;
                await using (Stream s = await current.OpenReadAsync())
                {
                    using var ms = new MemoryStream();
                    await s.CopyToAsync(ms);
                    previous = ms.ToArray();
                }
                await WriteFile(folder, name + "-bak", existing.GetValueOrDefault(name + "-bak"), previous);
            }

            byte[] data = await File.ReadAllBytesAsync(Path.Combine(sourceDir, name));
            await WriteFile(folder, name, current, data);
        }
    }

    private static async Task WriteFile(IStorageFolder folder, string name, IStorageFile? existing, byte[] data)
    {
        // a document provider may keep old bytes past the new end when a file is opened for writing,
        // so an existing file is replaced by a new one instead of being overwritten
        if (existing != null)
            await existing.DeleteAsync();
        IStorageFile file = await folder.CreateFileAsync(name)
            ?? throw new IOException($"Cannot create {name} in the chosen folder.");
        if (!file.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"The folder created {file.Name} instead of {name}.");
        await using Stream output = await file.OpenWriteAsync();
        await output.WriteAsync(data);
        await output.FlushAsync();
    }
}
