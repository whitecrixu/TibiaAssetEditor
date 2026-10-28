using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SkiaSharp;
using Tibia.Protobuf.Appearances;

namespace Assets_Editor
{
    public class LegacyAppearance
    {
        public LegacyAppearance()
        {
            Signature = 0;
            Appearances = new Appearances();
        }

        public uint Signature;
        public ushort ObjectCount;
        public ushort OutfitCount;
        public ushort EffectCount;
        public ushort MissileCount;
        public Appearances Appearances;

        public void ReadLegacyDat(string file, int version)
        {
            ReadLegacyDat(file, version, MainWindow.GetCurrentPreset() ?? new());
        }

        // reads a dat with explicit feature flags (extended / frame durations / frame groups), e.g. a
        // second client opened in the import manager whose flags differ from the loaded preset
        public void ReadLegacyDat(string file, int version, PresetSettings preset)
        {
            var versionInfo = MainWindow.datStructure.GetVersionInfo(version);
            using var stream = File.OpenRead(file);
            using var r = new BinaryReader(stream);
            try
            {
                DatInfo info = DatStructure.ReadAppearanceInfo(r);
                Signature = info.Signature;
                ObjectCount = info.ObjectCount;
                OutfitCount = info.OutfitCount;
                EffectCount = info.EffectCount;
                MissileCount = info.MissileCount;

                // raw rd file has 2 bytes at the front of every item
                bool rdBytes = versionInfo.UseRDBytes;

                for (int i = 100; i <= ObjectCount; i++)
                {
                    if (rdBytes) {
                        r.ReadUInt16();
                    }

                    var appearance = DatStructure.ReadAppearance(r, APPEARANCE_TYPE.AppearanceObject, versionInfo, preset);
                    appearance.Id = (uint)i;
                    Appearances.Object.Add(appearance);
                }

                for (int i = 1; i <= OutfitCount; i++)
                {
                    if (rdBytes) {
                        r.ReadUInt16();
                    }

                    var appearance = DatStructure.ReadAppearance(r, APPEARANCE_TYPE.AppearanceOutfit, versionInfo, preset);
                    appearance.Id = (uint)i;
                    Appearances.Outfit.Add(appearance);
                }

                for (int i = 1; i <= EffectCount; i++)
                {
                    if (rdBytes) {
                        r.ReadUInt16();
                    }

                    var appearance = DatStructure.ReadAppearance(r, APPEARANCE_TYPE.AppearanceEffect, versionInfo, preset);
                    appearance.Id = (uint)i;
                    Appearances.Effect.Add(appearance);
                }

                for (int i = 1; i <= MissileCount; i++)
                {
                    if (rdBytes) {
                        r.ReadUInt16();
                    }

                    var appearance = DatStructure.ReadAppearance(r, APPEARANCE_TYPE.AppearanceMissile, versionInfo, preset);
                    appearance.Id = (uint)i;
                    Appearances.Missile.Add(appearance);
                }

                // a wrong structure/feature combination can parse a prefix of the file by accident;
                // only a read that ends exactly at EOF is a real match
                if (stream.Position != stream.Length)
                    throw new Exception($"{stream.Length - stream.Position} trailing bytes after the last missile");
            }
            catch (Exception e)
            {
                throw new Exception("Couldn't load dat file", e);
            }
        }
        public static bool WriteLegacyDat(string fn, uint signature, Appearances appearances, int version)
        {
            string tempFile = fn + ".tmp";
            try
            {
                // the dat stores no ids: an object's id is its position. The server (items.otb maps
                // client id -> server id 1:1 on NewEra) and the map reference these ids, so a gap
                // would silently shift every later object. Refuse to write instead.
                ValidateContiguousIds(appearances.Object, 100, "object");
                ValidateContiguousIds(appearances.Outfit, 1, "outfit");
                ValidateContiguousIds(appearances.Effect, 1, "effect");
                ValidateContiguousIds(appearances.Missile, 1, "missile");
                if (appearances.Object.Count + 99 > ushort.MaxValue)
                    throw new InvalidOperationException($"Too many objects ({appearances.Object.Count}); the dat header is limited to {ushort.MaxValue}.");

                VersionInfo versionInfo = MainWindow.datStructure.GetVersionInfo(version);
                PresetSettings preset = MainWindow.GetCurrentPreset() ?? new();

                // raw rd file has 2 bytes at the front of every item
                bool rdBytes = versionInfo.UseRDBytes;

                using (var w = new BinaryWriter(new FileStream(tempFile, FileMode.Create, FileAccess.Write)))
                {
                    w.Write(signature);
                    w.Write((ushort)(appearances.Object.Count + 99));
                    w.Write((ushort)appearances.Outfit.Count);
                    w.Write((ushort)appearances.Effect.Count);
                    w.Write((ushort)appearances.Missile.Count);

                    var groups = new (Google.Protobuf.Collections.RepeatedField<Appearance> list, APPEARANCE_TYPE type)[] {
                        (appearances.Object, APPEARANCE_TYPE.AppearanceObject),
                        (appearances.Outfit, APPEARANCE_TYPE.AppearanceOutfit),
                        (appearances.Effect, APPEARANCE_TYPE.AppearanceEffect),
                        (appearances.Missile, APPEARANCE_TYPE.AppearanceMissile),
                    };
                    foreach (var (list, type) in groups) {
                        foreach (Appearance appearance in list.OrderBy(a => a.Id)) {
                            if (rdBytes) {
                                w.Write((ushort)0);
                            }

                            // AppearanceType decides whether frame groups are written; never trust an unset field
                            appearance.AppearanceType = type;
                            DatStructure.WriteAppearance(w, appearance, versionInfo, preset);
                        }
                    }
                }

                File.Move(tempFile, fn, overwrite: true);
                return true;
            }
            catch (Exception exception)
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                ErrorManager.ShowError($"Unable to write {Path.GetFileName(fn)}:\n\n{exception.Message}");
                return false;
            }
        }

        private static void ValidateContiguousIds(Google.Protobuf.Collections.RepeatedField<Appearance> list, uint firstId, string what)
        {
            uint expected = firstId;
            foreach (Appearance appearance in list.OrderBy(a => a.Id)) {
                if (appearance.Id != expected)
                    throw new InvalidOperationException($"The {what} list is not contiguous: expected id {expected}, found {appearance.Id}. Ids are positional in the dat, so writing would shift every later {what}.");
                expected++;
            }
        }

        public static int GetSpriteIndex(FrameGroup frameGroup, int width, int height, int layers, int patternX, int patternY, int patternZ, int frames)
        {
            var spriteInfo = frameGroup.SpriteInfo;
            int index = (int)(frames % spriteInfo.PatternFrames);
            index = index * (int)spriteInfo.PatternZ + patternZ;
            index = index * (int)spriteInfo.PatternY + patternY;
            index = index * (int)spriteInfo.PatternX + patternX;
            index = index * (int)spriteInfo.PatternLayers + layers;
            index = index * (int)spriteInfo.PatternHeight + height;
            index = index * (int)spriteInfo.PatternWidth + width;

            return index;
        }

        // largest sprite list a single frame group may get from the texture editor
        public const long MaxSpritesPerGroup = 200_000;

        // Changes the pattern dimensions of a sprite info. Sprite ids are kept by their
        // (width, height, layer, x, y, z, frame) coordinate; new slots get sprite 0. The animation gets
        // one phase per frame (new phases 100 ms) and is dropped for a single frame, like the reader does.
        public static void ResizeSpriteInfo(SpriteInfo info, uint width, uint height, uint layers, uint patternX, uint patternY, uint patternZ, uint frames)
        {
            if (width == 0 || height == 0 || layers == 0 || patternX == 0 || patternY == 0 || patternZ == 0 || frames == 0)
                throw new ArgumentException("Pattern dimensions must be at least 1.");
            if (width > 255 || height > 255 || layers > 255 || patternX > 255 || patternY > 255 || patternZ > 255 || frames > 255)
                throw new ArgumentException("Pattern dimensions are stored as bytes (max 255).");

            long count = (long)width * height * layers * patternX * patternY * patternZ * frames;
            if (count > MaxSpritesPerGroup)
                throw new InvalidOperationException($"{count} sprites in one frame group is too many (max {MaxSpritesPerGroup}).");

            SpriteInfo old = info.Clone();
            bool sameShape = old.PatternWidth == width && old.PatternHeight == height && old.PatternLayers == layers
                             && old.PatternX == patternX && old.PatternY == patternY && old.PatternZ == patternZ
                             && old.PatternFrames == frames && old.SpriteId.Count == count;

            if (!sameShape)
            {
                var ids = new List<uint>((int)count);
                for (uint f = 0; f < frames; f++)
                for (uint z = 0; z < patternZ; z++)
                for (uint y = 0; y < patternY; y++)
                for (uint x = 0; x < patternX; x++)
                for (uint l = 0; l < layers; l++)
                for (uint h = 0; h < height; h++)
                for (uint w = 0; w < width; w++)
                {
                    uint id = 0;
                    if (f < old.PatternFrames && z < old.PatternZ && y < old.PatternY && x < old.PatternX
                        && l < old.PatternLayers && h < old.PatternHeight && w < old.PatternWidth)
                    {
                        long oldIndex = ((((((long)f * old.PatternZ + z) * old.PatternY + y) * old.PatternX + x) * old.PatternLayers + l) * old.PatternHeight + h) * old.PatternWidth + w;
                        if (oldIndex < old.SpriteId.Count)
                            id = old.SpriteId[(int)oldIndex];
                    }
                    ids.Add(id);
                }

                info.SpriteId.Clear();
                info.SpriteId.Add(ids);
                info.PatternWidth = width;
                info.PatternHeight = height;
                info.PatternLayers = layers;
                info.PatternX = patternX;
                info.PatternY = patternY;
                info.PatternZ = patternZ;
                info.PatternFrames = frames;
            }

            if ((width > 1 || height > 1) && info.PatternSize == 0)
                info.PatternSize = Sprite.DefaultSize;

            if (frames > 1)
            {
                info.Animation ??= new SpriteAnimation {
                    AnimationMode = ANIMATION_ANIMATION_MODE.AnimationAsynchronized,
                    LoopCount = 0,
                    DefaultStartPhase = 0,
                };
                var phases = info.Animation.SpritePhase;
                while (phases.Count > frames)
                    phases.RemoveAt(phases.Count - 1);
                while (phases.Count < frames)
                    phases.Add(new SpritePhase { DurationMin = 100, DurationMax = 100 });
            }
            else
            {
                info.Animation = null;
            }
        }

        // Splits an image whose sides are multiples of 32 into Tibia tiles. The result is indexed
        // [h * tilesWide + w] in sprite coordinates: w = 0 / h = 0 is the bottom-right tile, as the
        // client draws tile (w, h) at ((width - 1 - w) * 32, (height - 1 - h) * 32). Fully transparent
        // tiles are null (sprite 0); the rest are compressed in the given spr layout.
        public static byte[]?[] SplitIntoTiles(SKBitmap image, bool transparent, out int tilesWide, out int tilesHigh)
        {
            if (image.Width % Sprite.DefaultSize != 0 || image.Height % Sprite.DefaultSize != 0)
                throw new ArgumentException($"Image size {image.Width}x{image.Height} is not a multiple of {Sprite.DefaultSize}.");

            tilesWide = image.Width / Sprite.DefaultSize;
            tilesHigh = image.Height / Sprite.DefaultSize;
            var tiles = new byte[]?[tilesWide * tilesHigh];

            using SKPixmap pixmap = image.PeekPixels();
            var tileInfo = new SKImageInfo(Sprite.DefaultSize, Sprite.DefaultSize, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var tile = new SKBitmap(tileInfo);
            byte[] pixels = new byte[Sprite.ARGBPixelsDataSize];

            for (int h = 0; h < tilesHigh; h++)
            {
                for (int w = 0; w < tilesWide; w++)
                {
                    int srcX = (tilesWide - 1 - w) * Sprite.DefaultSize;
                    int srcY = (tilesHigh - 1 - h) * Sprite.DefaultSize;
                    if (!pixmap.ReadPixels(tileInfo, tile.GetPixels(), tile.RowBytes, srcX, srcY))
                        throw new InvalidOperationException("Unable to read image pixels.");
                    Marshal.Copy(tile.GetPixels(), pixels, 0, pixels.Length);

                    byte[] compressed = Sprite.CompressPixelsBGRA(pixels, transparent);
                    tiles[h * tilesWide + w] = compressed.Length > 0 ? compressed : null;
                }
            }
            return tiles;
        }

        // Renders one view of an object: frame group, pattern x/y/z and animation frame. layer -1 draws
        // every layer, otherwise only that one (outfits: layer 0 is the body, layer 1 the colour mask).
        // Tiles are stored bottom-right first, so tile (w, h) lands at ((width - 1 - w), (height - 1 - h)).
        public static SKBitmap? RenderObject(Appearance appearance, Func<uint, SKBitmap?> getSprite, int groupIndex, int patternX, int patternY, int patternZ, int frame, int layer)
        {
            if (appearance.FrameGroup.Count == 0)
                return null;

            FrameGroup frameGroup = appearance.FrameGroup[Math.Clamp(groupIndex, 0, appearance.FrameGroup.Count - 1)];
            SpriteInfo info = frameGroup.SpriteInfo;
            if (info == null)
                return null;

            int width = (int)info.PatternWidth;
            int height = (int)info.PatternHeight;
            int layers = (int)info.PatternLayers;
            if (width == 0 || height == 0 || layers == 0 || info.PatternX == 0 || info.PatternY == 0 || info.PatternZ == 0 || info.PatternFrames == 0)
                return null;

            patternX = Math.Clamp(patternX, 0, (int)info.PatternX - 1);
            patternY = Math.Clamp(patternY, 0, (int)info.PatternY - 1);
            patternZ = Math.Clamp(patternZ, 0, (int)info.PatternZ - 1);
            frame = Math.Clamp(frame, 0, (int)info.PatternFrames - 1);
            int firstLayer = layer < 0 ? 0 : Math.Min(layer, layers - 1);
            int lastLayer = layer < 0 ? layers - 1 : firstLayer;

            var bitmap = new SKBitmap(width * Sprite.DefaultSize, height * Sprite.DefaultSize, SKColorType.Bgra8888, SKAlphaType.Premul);
            using SKCanvas canvas = new(bitmap);
            canvas.Clear(SKColors.Transparent);

            for (int l = firstLayer; l <= lastLayer; l++)
            {
                for (int w = 0; w < width; w++)
                {
                    for (int h = 0; h < height; h++)
                    {
                        int index = GetSpriteIndex(frameGroup, w, h, l, patternX, patternY, patternZ, frame);
                        if (index < 0 || index >= info.SpriteId.Count)
                            continue;
                        uint spriteId = info.SpriteId[index];
                        if (spriteId == 0)
                            continue;

                        using SKBitmap? tile = getSprite(spriteId);
                        if (tile != null)
                            canvas.DrawBitmap(tile, SKRect.Create((width - w - 1) * Sprite.DefaultSize, (height - h - 1) * Sprite.DefaultSize, Sprite.DefaultSize, Sprite.DefaultSize));
                    }
                }
            }
            return bitmap;
        }

        public static SKBitmap? DecodeSprite(SpriteStorage spriteStorage, uint spriteId)
        {
            // decode from bytes: SKBitmap.Decode(Stream) disposes the stream
            return SKBitmap.Decode(spriteStorage.getSpriteStream(spriteId).ToArray());
        }

        public static MemoryStream EncodePng(SKBitmap? bitmap)
        {
            MemoryStream stream = new();
            if (bitmap == null)
                return stream;
            using (SKImage img = SKImage.FromBitmap(bitmap))
            using (SKData data = img.Encode(SKEncodedImageFormat.Png, 100))
                data.SaveTo(stream);
            stream.Position = 0;
            return stream;
        }

        public static MemoryStream GetObjectImage(Appearance appearance, SpriteStorage spriteStorage, int groupIndex, int patternX, int patternY, int patternZ, int frame, int layer)
        {
            using SKBitmap? bitmap = RenderObject(appearance, id => DecodeSprite(spriteStorage, id), groupIndex, patternX, patternY, patternZ, frame, layer);
            return EncodePng(bitmap);
        }

        // list thumbnail: first frame group, outfits facing south (pattern x 2) without the colour mask
        public static MemoryStream GetObjectImage(Appearance appearance, SpriteStorage spriteStorage)
        {
            return GetObjectImage(appearance, spriteStorage, 0);
        }

        public static MemoryStream GetObjectImage(Appearance appearance, SpriteStorage spriteStorage, int frame)
        {
            if (appearance.FrameGroup.Count == 0)
                return new();

            bool outfit = appearance.AppearanceType == APPEARANCE_TYPE.AppearanceOutfit;
            uint patternX = appearance.FrameGroup[0].SpriteInfo?.PatternX ?? 1;
            int x = outfit && patternX > 0 ? (int)(2 % patternX) : 0;
            return GetObjectImage(appearance, spriteStorage, 0, x, 0, 0, frame, outfit ? 0 : -1);
        }
    }
}
