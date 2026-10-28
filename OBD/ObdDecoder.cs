using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SkiaSharp;
using Tibia.Protobuf.Appearances;

namespace Assets_Editor;

public class ObdDecoder
{
    private static VersionInfo? Version1098;
    public static void SetDatStructure(VersionInfo versionInfo) {
        Version1098 = versionInfo;
    }
    public static VersionInfo? GetDatStructure() {
        return Version1098;
    }

    private static Appearance ReadAppearance1098(BinaryReader r, APPEARANCE_TYPE type)
    {
        Appearance appearance = new() {
            AppearanceType = type,
            Flags = new()
        };

        DatStructure.ReadAppearanceAttr(appearance, r, Version1098);

        return appearance;
    }

    private static bool WriteAppearance1098(BinaryWriter w, Appearance item)
    {
        DatStructure.WriteAppearanceAttr(w, item, Version1098);
        return true;
    }
    // Load a 32×32 ARGB pixel buffer for the given sprite id, for writing into an .obd.
    //
    // Modern clients keep sprites lazy-loaded from sheet files (MainWindow.getSpriteStream),
    // while legacy clients use MainSprStorage (keyed by id in Sprites + SprLists). We try
    // the modern lookup first and fall back to the legacy storage. In either case we end
    // up with a PNG-encoded MemoryStream which we decode into a 32×32 BGRA SKBitmap and
    // re-serialize as uncompressed ARGB pixels (the on-disk OBD payload).
    private static byte[] LoadSpritePixels(uint spriteId)
    {
        MemoryStream? ms = MainWindow.getSpriteStream((int)spriteId);
        if (ms == null || ms.Length == 0)
        {
            try { ms = MainWindow.MainSprStorage?.getSpriteStream(spriteId); }
            catch { ms = null; }
        }

        // Decode from bytes: the stream is the cached one in SprLists and
        // SKBitmap.Decode(Stream) would dispose it.
        SKBitmap? bmp = ms != null ? SKBitmap.Decode(ms.ToArray()) : null;
        if (bmp == null)
        {
            // Return a fully-transparent 32×32 tile so the export still succeeds.
            return new byte[Sprite.ARGBPixelsDataSize];
        }

        // OBD tiles are always 32×32. Resize if the sprite is a bigger composite
        // (this matches how legacy imports expect per-tile payloads).
        if (bmp.Width != 32 || bmp.Height != 32)
        {
            using var resized = bmp.Resize(new SKImageInfo(32, 32, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);
            if (resized != null)
            {
                bmp.Dispose();
                bmp = resized.Copy();
            }
        }

        byte[] pixels = Sprite.UncompressPixelsARGB(Sprite.CompressBitmap(bmp, true), true);
        bmp.Dispose();
        return pixels;
    }

    private static Appearance DecodeV2(BinaryReader reader, ref ConcurrentDictionary<int, MemoryStream> list)
    {
        ushort clientVersion = reader.ReadUInt16();
        byte category = reader.ReadByte();
        uint texturePos = reader.ReadUInt32();
        Appearance appearance = ReadAppearance1098(reader, (APPEARANCE_TYPE)category);

        //FIXED_FRAME_GROUP
        FrameGroup frameGroup = new() {
            SpriteInfo = new(),

            FixedFrameGroup = FIXED_FRAME_GROUP.OutfitIdle
        };

        frameGroup.SpriteInfo.PatternWidth = reader.ReadByte();
        frameGroup.SpriteInfo.PatternHeight = reader.ReadByte();

        if (frameGroup.SpriteInfo.PatternWidth > 1 || frameGroup.SpriteInfo.PatternHeight > 1)
            frameGroup.SpriteInfo.PatternSize = reader.ReadByte();
        else
            frameGroup.SpriteInfo.PatternSize = 32;

        frameGroup.SpriteInfo.PatternLayers = reader.ReadByte();

        frameGroup.SpriteInfo.PatternX = reader.ReadByte();

        frameGroup.SpriteInfo.PatternY = reader.ReadByte();

        frameGroup.SpriteInfo.PatternZ = reader.ReadByte();

        frameGroup.SpriteInfo.PatternFrames = reader.ReadByte();
        
        if (frameGroup.SpriteInfo.PatternFrames > 1)
        {
            SpriteAnimation spriteAnimation = new();
            spriteAnimation.AnimationMode = (ANIMATION_ANIMATION_MODE)reader.ReadByte();
            spriteAnimation.LoopCount = reader.ReadUInt32();
            spriteAnimation.DefaultStartPhase = reader.ReadByte();

            for (int k = 0; k < frameGroup.SpriteInfo.PatternFrames; k++)
            {
                SpritePhase spritePhase = new();
                spritePhase.DurationMin = reader.ReadUInt32();
                spritePhase.DurationMax = reader.ReadUInt32();
                spriteAnimation.SpritePhase.Add(spritePhase);
            }
            frameGroup.SpriteInfo.Animation = spriteAnimation;
        }

        int NumSprites = (int)(frameGroup.SpriteInfo.PatternWidth * frameGroup.SpriteInfo.PatternHeight * frameGroup.SpriteInfo.PatternLayers * frameGroup.SpriteInfo.PatternX * frameGroup.SpriteInfo.PatternY * frameGroup.SpriteInfo.PatternZ * frameGroup.SpriteInfo.PatternFrames);
        for (var x = 0; x < NumSprites; x++)
        {
            var spriteId = reader.ReadUInt32();
            frameGroup.SpriteInfo.SpriteId.Add(spriteId);
            var dataSize = Sprite.ARGBPixelsDataSize;

            byte[] pixels = reader.ReadBytes(dataSize);

            Sprite spr = new() {
                Transparent = true,
                CompressedPixels = Sprite.CompressPixelsARGB(pixels, true)
            };
            spr.Size = (uint)spr.CompressedPixels.Length;
            using SKBitmap _bmp = spr.GetBitmap();
            MemoryStream memoryStream = new();
            using (SKImage img = SKImage.FromBitmap(_bmp))
            using (SKData data = img.Encode(SKEncodedImageFormat.Png, 100))
                data.SaveTo(memoryStream);
            memoryStream.Position = 0;
            spr.CompressedPixels = null;
            list.TryAdd((int)spriteId, memoryStream);
        }
        
        appearance.FrameGroup.Add(frameGroup);
        return appearance;
    }
    private static Appearance DecodeV3(BinaryReader reader, ref ConcurrentDictionary<int, MemoryStream> list)
    {
        ushort clientVersion = reader.ReadUInt16();
        byte category = reader.ReadByte();
        uint texturePos = reader.ReadUInt32();
        Appearance appearance = ReadAppearance1098(reader, (APPEARANCE_TYPE)category);

        byte FrameGroupCount = 1;
        if (appearance.AppearanceType == APPEARANCE_TYPE.AppearanceOutfit)
            FrameGroupCount = reader.ReadByte();

        for (int i = 0; i < FrameGroupCount; i++)
        {
            //FIXED_FRAME_GROUP
            FrameGroup frameGroup = new() {
                SpriteInfo = new()
            };
            if (appearance.AppearanceType == APPEARANCE_TYPE.AppearanceOutfit)
            {
                byte FrameGroupType = reader.ReadByte();
                frameGroup.FixedFrameGroup = (FIXED_FRAME_GROUP)FrameGroupType;
            }
            else
                frameGroup.FixedFrameGroup = FIXED_FRAME_GROUP.OutfitIdle;

            frameGroup.SpriteInfo.PatternWidth = reader.ReadByte();
            frameGroup.SpriteInfo.PatternHeight = reader.ReadByte();

            if (frameGroup.SpriteInfo.PatternWidth > 1 || frameGroup.SpriteInfo.PatternHeight > 1)
                frameGroup.SpriteInfo.PatternSize = reader.ReadByte();
            else
                frameGroup.SpriteInfo.PatternSize = 32;

            frameGroup.SpriteInfo.PatternLayers = reader.ReadByte();

            frameGroup.SpriteInfo.PatternX = reader.ReadByte();

            frameGroup.SpriteInfo.PatternY = reader.ReadByte();

            frameGroup.SpriteInfo.PatternZ = reader.ReadByte();

            frameGroup.SpriteInfo.PatternFrames = reader.ReadByte();

            if (frameGroup.SpriteInfo.PatternFrames > 1)
            {
                SpriteAnimation spriteAnimation = new SpriteAnimation();
                spriteAnimation.AnimationMode = (ANIMATION_ANIMATION_MODE)reader.ReadByte();
                spriteAnimation.LoopCount = reader.ReadUInt32();
                spriteAnimation.DefaultStartPhase = reader.ReadByte();

                for (int k = 0; k < frameGroup.SpriteInfo.PatternFrames; k++)
                {
                    SpritePhase spritePhase = new SpritePhase();
                    spritePhase.DurationMin = reader.ReadUInt32();
                    spritePhase.DurationMax = reader.ReadUInt32();
                    spriteAnimation.SpritePhase.Add(spritePhase);
                }
                frameGroup.SpriteInfo.Animation = spriteAnimation;

            }

            int NumSprites = (int)(frameGroup.SpriteInfo.PatternWidth * frameGroup.SpriteInfo.PatternHeight * frameGroup.SpriteInfo.PatternLayers * frameGroup.SpriteInfo.PatternX * frameGroup.SpriteInfo.PatternY * frameGroup.SpriteInfo.PatternZ * frameGroup.SpriteInfo.PatternFrames);
            for (var x = 0; x < NumSprites; x++)
            {
                var spriteId = reader.ReadUInt32();
                frameGroup.SpriteInfo.SpriteId.Add(spriteId);
                var dataSize = reader.ReadUInt32();
                if (dataSize > Sprite.ARGBPixelsDataSize)
                    throw new Exception("Invalid sprite data size.");

                byte[] pixels = reader.ReadBytes((int)dataSize);

                Sprite spr = new() {
                    Transparent = true,
                    CompressedPixels = Sprite.CompressPixelsARGB(pixels, true)
                };
                spr.Size = (uint)spr.CompressedPixels.Length;
                using SKBitmap _bmp = spr.GetBitmap();
                MemoryStream memoryStream = new();
                using (SKImage img = SKImage.FromBitmap(_bmp))
                using (SKData data = img.Encode(SKEncodedImageFormat.Png, 100))
                    data.SaveTo(memoryStream);
                memoryStream.Position = 0;
                spr.CompressedPixels = null;
                list.TryAdd((int)spriteId, memoryStream);
            }

            appearance.FrameGroup.Add(frameGroup);
        }
        return appearance;
    }

    public static Appearance Load(string path, ref ConcurrentDictionary<int, MemoryStream> list)
    {
        /*
            IMPORTANT: Currently the import feature loads using 1098 structure
            this means that when you have some old dat version open (eg. 7.6) and you import outfit with addons / frame groups
            the algorithm may get the frames wrong
            this loader may require additional function for conversion of the loaded asset

            recommendations for potential "sprite downgrade" algorithm:
            1. if no pattern_z:
            - remove blend layers, combine addons, ignore mounted state

            2. if no frame durations:
            - assume 100ms on every frame

            3. if no frame groups:
            - loaded outfit has walk animation: use walk animation
            - 2 frames + 1 frame idle -> combine to idle framegroup
            - 8 frames -> take 1 frame from idle, then take first 7 frames from walk

            - loaded outfit has no walk animation: use idle animation

            4. if no addons (dat structure < 4 - add some xml property):
            - combine all addons to the outfit layer

            5. if no mounts:
            - does not need handling at all (?)
        */

        try
        {
            using BinaryReader br = new(new FileStream(path, FileMode.Open));
            byte[] bytes = br.ReadBytes((int)br.BaseStream.Length);
            br.Close();
            if (bytes == null)
            {
                return null;
            }
            bytes = LZMA.Uncompress(bytes);
            using BinaryReader reader = new(new MemoryStream(bytes));
            ushort version = reader.ReadUInt16();

            if (version == 200)
            {
                return DecodeV2(reader, ref list);
            }
            else if (version == 300)
            {
                return DecodeV3(reader, ref list);
            }
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.StackTrace);
            return null;
        }
    }

    private static byte[] EncodeV2(Appearance data)
    {
        using BinaryWriter writer = new(new MemoryStream());

        // write obd version
        writer.Write((ushort)200);

        // write client version
        writer.Write((ushort)1098);

        // write category
        writer.Write((byte)data.AppearanceType);

        // skipping the texture patterns position.
        int patternsPosition = (int)writer.BaseStream.Position;
        writer.Seek(4, SeekOrigin.Current);

        if (!WriteAppearance1098(writer, data)) {
            return null;
        }

        // write the texture patterns position.
        int position = (int)writer.BaseStream.Position;
        writer.Seek(patternsPosition, SeekOrigin.Begin);
        writer.Write((uint)writer.BaseStream.Position);
        writer.Seek(position, SeekOrigin.Begin);

        var spriteInfo = data.FrameGroup[0].SpriteInfo;

        writer.Write((byte)spriteInfo.PatternWidth);
        writer.Write((byte)spriteInfo.PatternHeight);

        if (spriteInfo.PatternWidth > 1 || spriteInfo.PatternHeight > 1) {
            writer.Write((byte)spriteInfo.PatternSize);
        }

        writer.Write((byte)spriteInfo.PatternLayers);
        writer.Write((byte)spriteInfo.PatternX);
        writer.Write((byte)spriteInfo.PatternY);
        writer.Write((byte)spriteInfo.PatternZ);
        writer.Write((byte)spriteInfo.PatternFrames);

        if (spriteInfo.PatternFrames > 1) {
            byte animationMode = (byte)spriteInfo.Animation.AnimationMode;
            // stored as-is (0 = infinite, -1 = ping-pong, n = counted); unrelated to the animation mode
            uint loopCount = spriteInfo.Animation.LoopCount;

            writer.Write(animationMode);
            writer.Write(loopCount);
            writer.Write((byte)spriteInfo.Animation.DefaultStartPhase);

            for (int i = 0; i < spriteInfo.PatternFrames; i++) {
                writer.Write(spriteInfo.Animation.SpritePhase[i].DurationMin);
                writer.Write(spriteInfo.Animation.SpritePhase[i].DurationMax);
            }
        }

        for (int i = 0; i < spriteInfo.SpriteId.Count; i++) {
            byte[] pixels = LoadSpritePixels(spriteInfo.SpriteId[i]);
            writer.Write(spriteInfo.SpriteId[i]);
            writer.Write(pixels);
        }

        return LZMA.Compress(((MemoryStream)writer.BaseStream).ToArray());
    }

    private static byte[] EncodeV3(Appearance data)
    {
        using BinaryWriter writer = new(new MemoryStream());

        // write obd version
        writer.Write((ushort)300);

        // write client version
        writer.Write((ushort)1098);

        // write category
        writer.Write((byte)data.AppearanceType);

        // skipping the texture patterns position.
        int patternsPosition = (int)writer.BaseStream.Position;
        writer.Seek(4, SeekOrigin.Current);

        if (!WriteAppearance1098(writer, data)) {
            return null;
        }

        // write the texture patterns position.
        int position = (int)writer.BaseStream.Position;
        writer.Seek(patternsPosition, SeekOrigin.Begin);
        writer.Write((uint)writer.BaseStream.Position);
        writer.Seek(position, SeekOrigin.Begin);

        byte FrameGroupCount = 1;
        if (data.AppearanceType == APPEARANCE_TYPE.AppearanceOutfit) {
            FrameGroupCount = (byte)data.FrameGroup.Count;
            writer.Write(FrameGroupCount);
        }
        for (int frame = 0; frame < FrameGroupCount; frame++) {

            FrameGroup group = data.FrameGroup[frame];
            var spriteInfo = group.SpriteInfo;

            writer.Write((byte)group.FixedFrameGroup);
            writer.Write((byte)spriteInfo.PatternWidth);
            writer.Write((byte)spriteInfo.PatternHeight);

            if (spriteInfo.PatternWidth > 1 || spriteInfo.PatternHeight > 1) {
                writer.Write((byte)spriteInfo.PatternSize);
            }

            writer.Write((byte)spriteInfo.PatternLayers);
            writer.Write((byte)spriteInfo.PatternX);
            writer.Write((byte)spriteInfo.PatternY);
            writer.Write((byte)spriteInfo.PatternZ);
            writer.Write((byte)spriteInfo.PatternFrames);

            if (spriteInfo.PatternFrames > 1) {
                byte animationMode = (byte)spriteInfo.Animation.AnimationMode;
                // stored as-is (0 = infinite, -1 = ping-pong, n = counted); unrelated to the animation mode
                uint loopCount = spriteInfo.Animation.LoopCount;

                writer.Write(animationMode);
                writer.Write(loopCount);
                writer.Write((byte)spriteInfo.Animation.DefaultStartPhase);

                for (int i = 0; i < spriteInfo.PatternFrames; i++) {
                    writer.Write(spriteInfo.Animation.SpritePhase[i].DurationMin);
                    writer.Write(spriteInfo.Animation.SpritePhase[i].DurationMax);
                }
            }

            for (int i = 0; i < spriteInfo.SpriteId.Count; i++) {
                byte[] pixels = LoadSpritePixels(spriteInfo.SpriteId[i]);
                writer.Write(spriteInfo.SpriteId[i]);
                writer.Write(pixels.Length);
                writer.Write(pixels);
            }
        }
        return LZMA.Compress(((MemoryStream)writer.BaseStream).ToArray());
    }
    public static async System.Threading.Tasks.Task<bool> Export(List<Appearance> appearances, Avalonia.Visual visual)
    {
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(visual);
        if (topLevel == null) return false;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Select Export Folder",
            AllowMultiple = false
        });
        if (folders != null && folders.Count > 0)
        {
            string exportFolder = folders[0].Path.LocalPath;
            foreach (var appearance in appearances)
            {
                string fileName = Path.Combine(exportFolder, appearance.Id + "_" + appearance.AppearanceType + (appearance.AppearanceType == APPEARANCE_TYPE.AppearanceOutfit ? "_v3" : "_v2") + ".obd");
                using (BinaryWriter writer = new(new FileStream(fileName, FileMode.Create)))
                {
                    if (appearance.AppearanceType == APPEARANCE_TYPE.AppearanceOutfit)
                        writer.Write(EncodeV3(appearance));
                    else
                        writer.Write(EncodeV2(appearance));
                    writer.Close();
                }
            }
            return true;
        }
        else
            return false;
    }
}
