using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SkiaSharp;

namespace Assets_Editor
{
    public class Sprite
    {
        public Sprite()
        {
            ID = 0;
            Size = 0;
            CompressedPixels = null;
            Transparent = false;
        }

        public const byte DefaultSize = 32;
        public const ushort RGBPixelsDataSize = 3072;
        public const ushort ARGBPixelsDataSize = 4096;

        public uint ID { get; set; }
        public uint Size { get; set; }
        public byte[]? CompressedPixels { get; set; }
        public bool Transparent { get; set; }
        // png of the sprite once it has been decoded; null until then (a 511k sprite file would
        // otherwise allocate half a million empty streams at load)
        public MemoryStream? MemoryStream { get; set; }

        private static byte[] BlankRGBSprite = new byte[RGBPixelsDataSize];
        private static byte[] BlankARGBSprite = new byte[ARGBPixelsDataSize];

        public byte[] GetPixels()
        {
            if (CompressedPixels == null || CompressedPixels.Length != Size)
                return BlankARGBSprite;

            int read = 0, write = 0, pos = 0;
            int transparentPixels = 0, coloredPixels = 0;
            int length = CompressedPixels.Length;
            byte bitPerPixel = (byte)(Transparent ? 4 : 3);
            byte[] pixels = new byte[ARGBPixelsDataSize];

            for (read = 0; read < length; read += 4 + (bitPerPixel * coloredPixels))
            {
                transparentPixels = CompressedPixels[pos++] | CompressedPixels[pos++] << 8;
                coloredPixels = CompressedPixels[pos++] | CompressedPixels[pos++] << 8;

                if (write + (transparentPixels * 4) <= pixels.Length)
                {
                    for (int i = 0; i < transparentPixels; i++)
                    {
                        pixels[write++] = 0x00;
                        pixels[write++] = 0x00;
                        pixels[write++] = 0x00;
                        pixels[write++] = 0x00;
                    }
                }

                if (write + (coloredPixels * 4) <= pixels.Length && pos + (coloredPixels * (Transparent ? 4 : 3)) <= CompressedPixels.Length)
                {
                    for (int i = 0; i < coloredPixels; i++)
                    {
                        byte r = CompressedPixels[pos++];
                        byte g = CompressedPixels[pos++];
                        byte b = CompressedPixels[pos++];
                        byte a = Transparent ? CompressedPixels[pos++] : (byte)0xFF;
                        pixels[write++] = b;
                        pixels[write++] = g;
                        pixels[write++] = r;
                        pixels[write++] = a;
                    }
                }
            }

            while (write < ARGBPixelsDataSize)
            {
                pixels[write++] = 0x00;
                pixels[write++] = 0x00;
                pixels[write++] = 0x00;
                pixels[write++] = 0x00;
            }

            return pixels;
        }

        public SKBitmap GetBitmap()
        {
            var bitmap = new SKBitmap(DefaultSize, DefaultSize, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            byte[] pixels = GetPixels();
            if (pixels != null && pixels.Length == ARGBPixelsDataSize)
            {
                IntPtr ptr = bitmap.GetPixels();
                Marshal.Copy(pixels, 0, ptr, pixels.Length);
            }
            return bitmap;
        }

        public static void CreateBlankSprite()
        {
            for (short i = 0; i < RGBPixelsDataSize; i++)
                BlankRGBSprite[i] = 0x11;
            for (short i = 0; i < ARGBPixelsDataSize; i++)
                BlankARGBSprite[i] = 0x11;
        }

        public static byte[] CompressBitmap(SKBitmap bitmap, bool transparent)
        {
            if (bitmap == null)
                throw new ArgumentNullException(nameof(bitmap));

            byte[] pixels = new byte[ARGBPixelsDataSize];
            var info = new SKImageInfo(DefaultSize, DefaultSize, SKColorType.Bgra8888, SKAlphaType.Unpremul);

            using var resized = bitmap.Resize(info, SKFilterQuality.None);
            var srcBitmap = resized ?? bitmap;

            IntPtr ptr = srcBitmap.GetPixels();
            Marshal.Copy(ptr, pixels, 0, ARGBPixelsDataSize);

            return CompressPixelsBGRA(pixels, transparent);
        }

        public static byte[] CompressPixelsBGRA(byte[] pixels, bool transparent)
        {
            if (pixels == null)
                throw new ArgumentNullException(nameof(pixels));
            if (pixels.Length != ARGBPixelsDataSize)
                throw new Exception("Invalid pixels data size");

            using var writer = new BinaryWriter(new MemoryStream());
            int alphaCount = 0;
            ushort chunkSize = 0;
            long coloredPos = 0, finishOffset = 0;
            int length = pixels.Length / 4;
            int index = 0, read;

            while (index < length)
            {
                chunkSize = 0;
                while (index < length)
                {
                    read = (index * 4) + 3;
                    if (pixels[read] != 0) break;
                    alphaCount++;
                    chunkSize++;
                    index++;
                }

                if (alphaCount < length && index < length)
                {
                    writer.Write(chunkSize);
                    coloredPos = writer.BaseStream.Position;
                    writer.BaseStream.Seek(2, SeekOrigin.Current);
                    chunkSize = 0;

                    while (index < length)
                    {
                        read = index * 4;
                        byte blue = pixels[read++];
                        byte green = pixels[read++];
                        byte red = pixels[read++];
                        byte alpha = pixels[read++];

                        if (alpha == 0) break;

                        writer.Write(red);
                        writer.Write(green);
                        writer.Write(blue);
                        if (transparent) writer.Write(alpha);

                        chunkSize++;
                        index++;
                    }

                    finishOffset = writer.BaseStream.Position;
                    writer.BaseStream.Seek(coloredPos, SeekOrigin.Begin);
                    writer.Write(chunkSize);
                    writer.BaseStream.Seek(finishOffset, SeekOrigin.Begin);
                }
            }

            return ((MemoryStream)writer.BaseStream).ToArray();
        }

        public static byte[] CompressPixelsARGB(byte[] pixels, bool transparent)
        {
            if (pixels == null)
                throw new ArgumentNullException(nameof(pixels));
            if (pixels.Length != ARGBPixelsDataSize)
                throw new Exception("Invalid pixels data size");

            using var writer = new BinaryWriter(new MemoryStream());
            int alphaCount = 0;
            ushort chunkSize = 0;
            long coloredPos = 0, finishOffset = 0;
            int length = pixels.Length / 4;
            int index = 0, read;

            while (index < length)
            {
                chunkSize = 0;
                while (index < length)
                {
                    read = index * 4;
                    byte alpha = pixels[read];
                    if (alpha != 0) break;
                    alphaCount++;
                    chunkSize++;
                    index++;
                }

                if (alphaCount < length && index < length)
                {
                    writer.Write(chunkSize);
                    coloredPos = writer.BaseStream.Position;
                    writer.BaseStream.Seek(2, SeekOrigin.Current);
                    chunkSize = 0;

                    while (index < length)
                    {
                        read = index * 4;
                        byte alpha = pixels[read++];
                        byte red = pixels[read++];
                        byte green = pixels[read++];
                        byte blue = pixels[read++];

                        if (alpha == 0) break;

                        writer.Write(red);
                        writer.Write(green);
                        writer.Write(blue);
                        if (transparent) writer.Write(alpha);

                        chunkSize++;
                        index++;
                    }

                    finishOffset = writer.BaseStream.Position;
                    writer.BaseStream.Seek(coloredPos, SeekOrigin.Begin);
                    writer.Write(chunkSize);
                    writer.BaseStream.Seek(finishOffset, SeekOrigin.Begin);
                }
            }

            return ((MemoryStream)writer.BaseStream).ToArray();
        }

        public static byte[] UncompressPixelsBGRA(byte[] compressedPixels, bool transparent)
        {
            if (compressedPixels == null)
                throw new ArgumentNullException(nameof(compressedPixels));

            int read = 0, write = 0, pos = 0;
            int transparentPixels, coloredPixels = 0;
            int length = compressedPixels.Length;
            byte bitPerPixel = (byte)(transparent ? 4 : 3);
            byte[] pixels = new byte[ARGBPixelsDataSize];

            for (read = 0; read < length; read += 4 + (bitPerPixel * coloredPixels))
            {
                transparentPixels = compressedPixels[pos++] | compressedPixels[pos++] << 8;
                coloredPixels = compressedPixels[pos++] | compressedPixels[pos++] << 8;

                for (int i = 0; i < transparentPixels; i++)
                {
                    pixels[write++] = 0x00;
                    pixels[write++] = 0x00;
                    pixels[write++] = 0x00;
                    pixels[write++] = 0x00;
                }

                for (int i = 0; i < coloredPixels; i++)
                {
                    byte r = compressedPixels[pos++];
                    byte g = compressedPixels[pos++];
                    byte b = compressedPixels[pos++];
                    byte a = transparent ? compressedPixels[pos++] : (byte)0xFF;
                    pixels[write++] = b;
                    pixels[write++] = g;
                    pixels[write++] = r;
                    pixels[write++] = a;
                }
            }

            while (write < ARGBPixelsDataSize)
            {
                pixels[write++] = 0x00;
                pixels[write++] = 0x00;
                pixels[write++] = 0x00;
                pixels[write++] = 0x00;
            }

            return pixels;
        }

        public static byte[] UncompressPixelsARGB(byte[] compressedPixels, bool transparent)
        {
            if (compressedPixels == null)
                throw new ArgumentNullException(nameof(compressedPixels));

            int read = 0, write = 0, pos = 0;
            int transparentPixels, coloredPixels = 0;
            int length = compressedPixels.Length;
            byte bitPerPixel = (byte)(transparent ? 4 : 3);
            byte[] pixels = new byte[ARGBPixelsDataSize];

            for (read = 0; read < length; read += 4 + (bitPerPixel * coloredPixels))
            {
                transparentPixels = compressedPixels[pos++] | compressedPixels[pos++] << 8;
                coloredPixels = compressedPixels[pos++] | compressedPixels[pos++] << 8;

                for (int i = 0; i < transparentPixels; i++)
                {
                    pixels[write++] = 0x00;
                    pixels[write++] = 0x00;
                    pixels[write++] = 0x00;
                    pixels[write++] = 0x00;
                }

                for (int i = 0; i < coloredPixels; i++)
                {
                    byte r = compressedPixels[pos++];
                    byte g = compressedPixels[pos++];
                    byte b = compressedPixels[pos++];
                    byte a = transparent ? compressedPixels[pos++] : (byte)0xFF;
                    pixels[write++] = a;
                    pixels[write++] = r;
                    pixels[write++] = g;
                    pixels[write++] = b;
                }
            }

            while (write < ARGBPixelsDataSize)
            {
                pixels[write++] = 0x00;
                pixels[write++] = 0x00;
                pixels[write++] = 0x00;
                pixels[write++] = 0x00;
            }

            return pixels;
        }

        public static async Task CompileSpritesAsync(string file, SpriteStorage spriteStorage, bool transparency, uint signature, IProgress<int> progress, bool extended = true)
        {
            await Task.Run(() => spriteStorage.Compile(file, signature, transparency, extended, progress));
        }
    }


    public class SpriteStorage
    {
        private string _sprPath = "";
        public string SprPath
        {
            get => _sprPath;
            set
            {
                lock (_fileLock)
                {
                    if (_sprPath != value)
                        CloseFileLocked();
                    _sprPath = value;
                }
            }
        }
        public uint Signature;
        public bool Transparency;
        public IProgress<int>? ProgressListener { get; set; }
        // guarded by SyncRoot: Compile reads it from a worker thread
        public Dictionary<uint, Sprite> Sprites { get; set; }
        // png cache; a key exists for every sprite id (0..SpriteCount), the value stays null until
        // the sprite is first decoded
        public ConcurrentDictionary<int, MemoryStream> SprLists { get; set; }

        // sprites changed in the editor since the file was loaded or last compiled;
        // everything else is copied byte-for-byte from SprPath on compile
        public HashSet<uint> Modified { get; } = [];
        public uint FileSpriteCount { get; private set; }
        private bool _fileExtended;

        // Sprites is a plain Dictionary: every access that may race with Compile locks this
        public object SyncRoot { get; } = new();

        // one shared read-only handle for raw sprite reads (opening a FileStream per sprite is far
        // too slow with 511k sprites); reopened whenever SprPath changes or Compile replaces the file
        private readonly object _fileLock = new();
        private BinaryReader? _reader;

        // edit generation of every modified id, so a compile only clears what it actually wrote
        private readonly Dictionary<uint, long> _modifiedGeneration = [];
        private long _generation;

        private static byte[]? _blankPng;

        public uint SpriteCount => (uint)Math.Max(0, SprLists.Count - 1);

        public void MarkModified(uint id)
        {
            lock (Modified)
            {
                Modified.Add(id);
                _modifiedGeneration[id] = ++_generation;
            }
        }

        public SpriteStorage()
        {
            Sprites = new Dictionary<uint, Sprite>();
            SprLists = new ConcurrentDictionary<int, MemoryStream>();
            Transparency = false;
            AddBlankSpriteZero();
        }

        public SpriteStorage(string path, bool transparency, IProgress<int>? progressListener = null)
        {
            _sprPath = path;
            Sprites = [];
            SprLists = [];
            Transparency = transparency;
            ProgressListener = progressListener;
        }

        private void AddBlankSpriteZero()
        {
            Sprite blankSpr = new() { ID = 0, MemoryStream = new MemoryStream(BlankPng(), false) };
            Sprites[0] = blankSpr;
            SprLists[0] = blankSpr.MemoryStream;
        }

        private static byte[] BlankPng()
        {
            return _blankPng ??= EncodePng(null, false);
        }

        // png of a sprite given its compressed pixels (null/empty = fully transparent)
        public static byte[] EncodePng(byte[]? compressedPixels, bool transparent)
        {
            Sprite sprite = new() { CompressedPixels = compressedPixels ?? [], Transparent = transparent };
            sprite.Size = (uint)sprite.CompressedPixels.Length;
            using var bmp = sprite.GetBitmap();
            using var image = SKImage.FromBitmap(bmp);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        // re-encodes compressed pixels between the RGB and RGBA (transparent) spr layouts
        public static byte[] ConvertCompressed(byte[] compressedPixels, bool fromTransparent, bool toTransparent)
        {
            if (fromTransparent == toTransparent || compressedPixels.Length == 0)
                return compressedPixels;
            return Sprite.CompressPixelsBGRA(Sprite.UncompressPixelsBGRA(compressedPixels, fromTransparent), toTransparent);
        }

        public void LoadSprites()
        {
            LoadSprites(MainWindow.GetCurrentPreset()?.Extended ?? false);
        }

        public void LoadSprites(bool isExtended)
        {
            Sprite.CreateBlankSprite();
            uint totalPics;
            lock (_fileLock)
            {
                CloseFileLocked();
                using FileStream fileStream = new(SprPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using BinaryReader reader = new(fileStream);
                Signature = reader.ReadUInt32();
                totalPics = isExtended ? reader.ReadUInt32() : reader.ReadUInt16();
                _fileExtended = isExtended;
                FileSpriteCount = totalPics;
            }
            lock (Modified)
            {
                Modified.Clear();
                _modifiedGeneration.Clear();
            }

            lock (SyncRoot)
            {
                Sprites.Clear();
                SprLists.Clear();
                AddBlankSpriteZero();

                int lastReportedProgress = -1;
                for (uint i = 0; i < totalPics; ++i)
                {
                    Sprite sprite = new() { ID = i + 1, Transparent = Transparency };
                    Sprites[sprite.ID] = sprite;
                    // decoded lazily by getSpriteStream
                    SprLists[(int)sprite.ID] = null!;
                    int progress = (int)(i * 100 / totalPics);
                    if (progress != lastReportedProgress && ProgressListener != null)
                    {
                        ProgressListener.Report(progress);
                        lastReportedProgress = progress;
                    }
                }
            }
        }

        // Returns a fresh png stream for the sprite on every call: callers hand it to SKBitmap.Decode,
        // which disposes it on Linux. Only the bytes are cached (ToArray works on a disposed MemoryStream).
        public MemoryStream getSpriteStream(uint id)
        {
            if (SprLists.TryGetValue((int)id, out MemoryStream? cached) && cached != null) {
                byte[] bytes = cached.ToArray();
                if (bytes.Length > 0)
                    return new MemoryStream(bytes, false);
            }

            byte[] png;
            try
            {
                png = EncodePng(GetCompressedPixels(id), Transparency);
            }
            catch
            {
                // a corrupt record must not take the whole list down
                png = BlankPng();
            }

            // only ids that exist get cached: adding a key would change the sprite count
            if (SprLists.ContainsKey((int)id))
                SprLists[(int)id] = new MemoryStream(png, false);
            return new MemoryStream(png, false);
        }

        // Compressed pixels of a sprite in this storage's layout (Transparency), or null when the
        // sprite is empty. Edited sprites come from memory, everything else from the file.
        public byte[]? GetCompressedPixels(uint id)
        {
            if (id == 0)
                return null;

            lock (SyncRoot)
            {
                if (Sprites.TryGetValue(id, out Sprite? sprite) && sprite.CompressedPixels != null)
                {
                    byte[] pixels = sprite.CompressedPixels;
                    if (pixels.Length == 0)
                        return null;
                    return ConvertCompressed(pixels, sprite.Transparent, Transparency);
                }
            }

            return id <= FileSpriteCount ? ReadRawSprite(id) : null;
        }

        // Appends a new sprite (compressed in this storage's layout; null = empty) and returns its id.
        public uint AddSprite(byte[]? compressedPixels)
        {
            byte[] png = EncodePng(compressedPixels, Transparency);
            uint id;
            lock (SyncRoot)
            {
                // ids are contiguous: the next one is the current count
                id = (uint)SprLists.Count;
                Sprite sprite = new()
                {
                    ID = id,
                    Transparent = Transparency,
                    CompressedPixels = compressedPixels ?? [],
                    MemoryStream = new MemoryStream(png, false),
                };
                sprite.Size = (uint)sprite.CompressedPixels.Length;
                Sprites[id] = sprite;
                SprLists[(int)id] = sprite.MemoryStream;
                MarkModified(id);
            }
            return id;
        }

        // Replaces the pixels of an existing sprite (compressed in this storage's layout; null = empty).
        public void SetSprite(uint id, byte[]? compressedPixels)
        {
            if (id == 0)
                throw new InvalidOperationException("Sprite 0 is the reserved empty sprite.");

            byte[] png = EncodePng(compressedPixels, Transparency);
            lock (SyncRoot)
            {
                if (!Sprites.ContainsKey(id))
                    throw new KeyNotFoundException($"Sprite {id} does not exist.");
                Sprite sprite = new()
                {
                    ID = id,
                    Transparent = Transparency,
                    CompressedPixels = compressedPixels ?? [],
                    MemoryStream = new MemoryStream(png, false),
                };
                sprite.Size = (uint)sprite.CompressedPixels.Length;
                Sprites[id] = sprite;
                SprLists[(int)id] = sprite.MemoryStream;
                MarkModified(id);
            }
        }

        public void CloseFile()
        {
            lock (_fileLock)
                CloseFileLocked();
        }

        private void CloseFileLocked()
        {
            _reader?.Dispose();
            _reader = null;
        }

        // compressed pixel data of sprite id as stored in SprPath, or null for an empty sprite
        private byte[]? ReadRawSprite(uint id)
        {
            lock (_fileLock)
            {
                _reader ??= new BinaryReader(new FileStream(SprPath, FileMode.Open, FileAccess.Read, FileShare.Read));
                return ReadRawSprite(_reader, id);
            }
        }

        private byte[]? ReadRawSprite(BinaryReader reader, uint id)
        {
            reader.BaseStream.Seek((_fileExtended ? 8 : 6) + (id - 1) * 4, SeekOrigin.Begin);
            uint address = reader.ReadUInt32();
            if (address == 0)
                return null;

            // 3 bytes colour key, u16 size, pixels
            reader.BaseStream.Seek(address + 3, SeekOrigin.Begin);
            ushort size = reader.ReadUInt16();
            return size > 0 ? reader.ReadBytes(size) : null;
        }

        // Writes the sprite file. Unchanged sprites are copied verbatim from SprPath (fast and lossless);
        // only edited sprites, or sprites beyond the loaded file, are re-encoded. Works from a snapshot
        // taken up front, so the editor may keep reading (and even editing) while it runs.
        public void Compile(string file, uint signature, bool transparency, bool extended, IProgress<int>? progress)
        {
            uint totalPics;
            uint fileSpriteCount = FileSpriteCount;
            bool fileTransparency = Transparency;
            long generation;
            HashSet<uint> modified;
            var edited = new Dictionary<uint, (byte[] pixels, bool transparent)>();
            var cachedPngs = new Dictionary<uint, byte[]>();

            lock (SyncRoot)
            {
                totalPics = SpriteCount;
                lock (Modified)
                {
                    modified = [.. Modified];
                    generation = _generation;
                }

                void Capture(uint id)
                {
                    if (Sprites.TryGetValue(id, out Sprite? sprite) && sprite.CompressedPixels != null)
                        edited[id] = (sprite.CompressedPixels, sprite.Transparent);
                    else if (SprLists.TryGetValue((int)id, out MemoryStream? png) && png != null)
                        cachedPngs[id] = png.ToArray();
                }

                foreach (uint id in modified)
                    if (id >= 1 && id <= totalPics)
                        Capture(id);
                for (uint id = fileSpriteCount + 1; id <= totalPics; id++)
                    if (!modified.Contains(id))
                        Capture(id);
            }

            if (!extended && totalPics > ushort.MaxValue)
                throw new InvalidOperationException($"{totalPics} sprites do not fit a non-extended spr (max {ushort.MaxValue}). Enable 'Extended'.");

            string tempFile = file + ".tmp";
            int headSize = extended ? 8 : 6;
            try
            {
                using (var source = File.Exists(SprPath) ? new BinaryReader(new FileStream(SprPath, FileMode.Open, FileAccess.Read, FileShare.Read)) : null)
                using (var writer = new BinaryWriter(new FileStream(tempFile, FileMode.Create, FileAccess.Write)))
                {
                    writer.Write(signature);
                    if (extended)
                        writer.Write(totalPics);
                    else
                        writer.Write((ushort)totalPics);

                    // address table is filled in as we go, data follows it
                    uint[] addresses = new uint[totalPics];
                    writer.Write(new byte[totalPics * 4]);

                    int lastReported = -1;
                    for (uint id = 1; id <= totalPics; id++)
                    {
                        byte[]? bytes = null;
                        if (edited.TryGetValue(id, out var edit))
                        {
                            bytes = ConvertCompressed(edit.pixels, edit.transparent, transparency);
                        }
                        else if (cachedPngs.TryGetValue(id, out byte[]? png))
                        {
                            using var skBmp = SKBitmap.Decode(png);
                            bytes = skBmp != null ? Sprite.CompressBitmap(skBmp, transparency) : null;
                        }
                        else if (source != null && id <= fileSpriteCount)
                        {
                            bytes = ReadRawSprite(source, id);
                            if (bytes != null)
                                bytes = ConvertCompressed(bytes, fileTransparency, transparency);
                        }

                        if (bytes != null && bytes.Length > 0)
                        {
                            if (bytes.Length > ushort.MaxValue)
                                throw new InvalidOperationException($"Sprite {id} is too large ({bytes.Length} bytes).");
                            addresses[id - 1] = (uint)writer.BaseStream.Position;
                            writer.Write((byte)0xFF);
                            writer.Write((byte)0x00);
                            writer.Write((byte)0xFF);
                            writer.Write((ushort)bytes.Length);
                            writer.Write(bytes);
                        }

                        int percent = (int)((long)id * 100 / totalPics);
                        if (percent != lastReported)
                        {
                            progress?.Report(percent);
                            lastReported = percent;
                        }
                    }

                    writer.Seek(headSize, SeekOrigin.Begin);
                    foreach (uint address in addresses)
                        writer.Write(address);
                }

                // the shared reader must not hold the file that is being replaced
                lock (_fileLock)
                {
                    CloseFileLocked();
                    File.Move(tempFile, file, overwrite: true);

                    // the written file is now the source of truth for raw reads
                    _sprPath = file;
                    Signature = signature;
                    Transparency = transparency;
                    _fileExtended = extended;
                    FileSpriteCount = totalPics;
                }
            }
            catch
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                throw;
            }

            // forget only what was written: edits made while compiling stay pending
            lock (SyncRoot)
            {
                foreach (var (id, edit) in edited)
                {
                    if (Sprites.TryGetValue(id, out Sprite? sprite) && ReferenceEquals(sprite.CompressedPixels, edit.pixels))
                    {
                        sprite.CompressedPixels = null;
                        sprite.Size = 0;
                        sprite.Transparent = transparency;
                    }
                }
                lock (Modified)
                {
                    foreach (uint id in modified)
                    {
                        if (_modifiedGeneration.TryGetValue(id, out long g) && g <= generation)
                        {
                            Modified.Remove(id);
                            _modifiedGeneration.Remove(id);
                        }
                    }
                }
            }
        }
    }
}
