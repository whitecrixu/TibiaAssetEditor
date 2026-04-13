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
            MemoryStream = new MemoryStream();
        }

        public const byte DefaultSize = 32;
        public const ushort RGBPixelsDataSize = 3072;
        public const ushort ARGBPixelsDataSize = 4096;

        public uint ID { get; set; }
        public uint Size { get; set; }
        public byte[]? CompressedPixels { get; set; }
        public bool Transparent { get; set; }
        public MemoryStream MemoryStream { get; set; }

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
            await Task.Run(() =>
            {
                int percentageComplete = 0;
                int currentPercentage = 0;
                int FullProgress = spriteStorage.SprLists.Count;
                string tempFile = file + ".tmp";
                using BinaryWriter writer = new(new FileStream(tempFile, FileMode.Create));
                uint totalPics = (uint)spriteStorage.SprLists.Count - 1;

                writer.Write(signature);
                if (extended)
                    writer.Write(totalPics);
                else
                    writer.Write(Convert.ToUInt16(totalPics));

                byte headSize = extended ? (byte)8 : (byte)6;
                int addressPosition = headSize;
                int address = (int)((totalPics * 4) + headSize);

                for (uint id = 1; id <= totalPics; id++)
                {
                    writer.Seek(addressPosition, SeekOrigin.Begin);

                    using var stream = spriteStorage.getSpriteStream(id);
                    using var skBmp = SKBitmap.Decode(stream);
                    var bytes = CompressBitmap(skBmp, transparency);

                    if (bytes.Length > 0)
                    {
                        writer.Write((uint)address);
                        writer.Seek(address, SeekOrigin.Begin);
                        writer.Write((byte)0xFF);
                        writer.Write((byte)0x00);
                        writer.Write((byte)0xFF);
                        writer.Write((short)bytes.Length);
                        if (bytes.Length > 0)
                            writer.Write(bytes);
                    }
                    else
                    {
                        writer.Write((uint)0);
                        writer.Seek(address, SeekOrigin.Begin);
                    }

                    address = (int)writer.BaseStream.Position;
                    addressPosition += 4;

                    percentageComplete = (int)(id * 100 / FullProgress);
                    if (percentageComplete > currentPercentage)
                    {
                        progress?.Report(percentageComplete);
                        currentPercentage = percentageComplete;
                    }
                }

                writer.Close();
                File.Move(tempFile, file, overwrite: true);
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            });
        }
    }

    public class SpriteStorage
    {
        public string SprPath { get; set; } = "";
        public uint Signature;
        public bool Transparency;
        public IProgress<int>? ProgressListener { get; set; }
        public Dictionary<uint, Sprite> Sprites { get; set; }
        public ConcurrentDictionary<int, MemoryStream> SprLists { get; set; }

        public SpriteStorage()
        {
            Sprites = new Dictionary<uint, Sprite>();
            SprLists = new ConcurrentDictionary<int, MemoryStream>();
            Transparency = false;
            Sprite blankSpr = new Sprite { ID = 0, CompressedPixels = Array.Empty<byte>() };
            using var bmp = blankSpr.GetBitmap();
            using var image = SKImage.FromBitmap(bmp);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            blankSpr.MemoryStream = new MemoryStream(data.ToArray());
            blankSpr.CompressedPixels = null;
            Sprites[0] = blankSpr;
            SprLists[0] = blankSpr.MemoryStream;
        }

        public SpriteStorage(string path, bool transparency, IProgress<int>? progressListener = null)
        {
            SprPath = path;
            Sprites = [];
            SprLists = [];
            Transparency = transparency;
            ProgressListener = progressListener;
        }

        public void LoadSprites()
        {
            Sprite.CreateBlankSprite();
            using FileStream fileStream = new(SprPath, FileMode.Open, FileAccess.Read);
            using BinaryReader reader = new(fileStream);

            Signature = reader.ReadUInt32();
            bool isExtended = MainWindow.GetCurrentPreset()?.Extended ?? false;
            uint totalPics = isExtended ? reader.ReadUInt32() : reader.ReadUInt16();

            Sprite blankSpr = new() { ID = 0, CompressedPixels = [] };
            using var bmp = blankSpr.GetBitmap();
            using var image = SKImage.FromBitmap(bmp);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            blankSpr.MemoryStream = new MemoryStream(data.ToArray());
            blankSpr.CompressedPixels = null;
            Sprites[0] = blankSpr;
            SprLists[0] = blankSpr.MemoryStream;

            int lastReportedProgress = -1;
            for (uint i = 0; i < totalPics; ++i)
            {
                Sprite sprite = new() { ID = i + 1, Transparent = Transparency };
                Sprites[sprite.ID] = sprite;
                SprLists[(int)sprite.ID] = sprite.MemoryStream;
                int progress = (int)(i * 100 / totalPics);
                if (progress != lastReportedProgress && ProgressListener != null)
                {
                    ProgressListener.Report(progress);
                    lastReportedProgress = progress;
                }
            }
        }

        public MemoryStream getSpriteStream(uint id)
        {
            if (SprLists[(int)id] != null && SprLists[(int)id].Length > 0)
                return SprLists[(int)id];

            bool isExtended = MainWindow.GetCurrentPreset()?.Extended ?? false;

            Sprite sprite = Sprites[id];
            using FileStream fileStream = new(SprPath, FileMode.Open, FileAccess.Read);
            using BinaryReader reader = new(fileStream);

            reader.BaseStream.Seek((isExtended ? 8 : 6) + (id - 1) * 4, SeekOrigin.Begin);
            uint index = reader.ReadUInt32() + 3;
            reader.BaseStream.Seek(index, SeekOrigin.Begin);
            sprite.Size = reader.ReadUInt16();

            if (sprite.Size > 0)
            {
                sprite.CompressedPixels = reader.ReadBytes((int)sprite.Size);
                using var bmp = sprite.GetBitmap();
                using var img = SKImage.FromBitmap(bmp);
                using var dat = img.Encode(SKEncodedImageFormat.Png, 100);
                sprite.MemoryStream = new MemoryStream(dat.ToArray());
                SprLists[(int)id] = sprite.MemoryStream;
            }

            return SprLists[(int)id];
        }
    }
}
