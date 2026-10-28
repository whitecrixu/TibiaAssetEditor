using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
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

                PresetSettings preset = MainWindow.GetCurrentPreset() ?? new();

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

        public static MemoryStream GetObjectImage(Appearance appearance, ConcurrentDictionary<int, MemoryStream> sprList)
        {
            int width = (int)(Sprite.DefaultSize * appearance.FrameGroup[0].SpriteInfo.PatternWidth);
            int height = (int)(Sprite.DefaultSize * appearance.FrameGroup[0].SpriteInfo.PatternHeight);

            using SKBitmap bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using SKCanvas g = new SKCanvas(bitmap);

            byte layers = (byte)(appearance.FrameGroup[0].SpriteInfo.PatternLayers);
            byte x = 0;

            if (appearance.AppearanceType == APPEARANCE_TYPE.AppearanceOutfit)
            {
                layers = 1;
                x = (byte)(2 % appearance.FrameGroup[0].SpriteInfo.PatternX);
            }

            // draw sprite
            for (byte l = 0; l < layers; l++)
            {
                for (byte w = 0; w < appearance.FrameGroup[0].SpriteInfo.PatternWidth; w++)
                {
                    for (byte h = 0; h < appearance.FrameGroup[0].SpriteInfo.PatternHeight; h++)
                    {
                        int index = GetSpriteIndex(appearance.FrameGroup[0], w, h, l, x, 0, 0, 0);
                        int spriteId = (int)appearance.FrameGroup[0].SpriteInfo.SpriteId[index];
                        int px = (int)((appearance.FrameGroup[0].SpriteInfo.PatternWidth - w - 1) * Sprite.DefaultSize);
                        int py = (int)((appearance.FrameGroup[0].SpriteInfo.PatternHeight - h - 1) * Sprite.DefaultSize);

                        sprList[spriteId].Position = 0;
                        using SKBitmap _bmp = SKBitmap.Decode(sprList[spriteId]);
                        if (_bmp != null)
                            g.DrawBitmap(_bmp, SKRect.Create(px, py, Sprite.DefaultSize, Sprite.DefaultSize));
                    }
                }
            }
            MemoryStream combinedStream = new MemoryStream();
            using (SKImage img = SKImage.FromBitmap(bitmap))
            using (SKData data = img.Encode(SKEncodedImageFormat.Png, 100))
                data.SaveTo(combinedStream);
            combinedStream.Position = 0;
            return combinedStream;
        }
        public static MemoryStream GetObjectImage(Appearance appearance, SpriteStorage spriteStorage)
        {
            int width = (int)(Sprite.DefaultSize * appearance.FrameGroup[0].SpriteInfo.PatternWidth);
            int height = (int)(Sprite.DefaultSize * appearance.FrameGroup[0].SpriteInfo.PatternHeight);

            using SKBitmap bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using SKCanvas g = new SKCanvas(bitmap);

            byte layers = (byte)(appearance.FrameGroup[0].SpriteInfo.PatternLayers);
            byte x = 0;

            if (appearance.AppearanceType == APPEARANCE_TYPE.AppearanceOutfit)
            {
                layers = 1;
                x = (byte)(2 % appearance.FrameGroup[0].SpriteInfo.PatternX);
            }

            // draw sprite
            for (byte l = 0; l < layers; l++)
            {
                for (byte w = 0; w < appearance.FrameGroup[0].SpriteInfo.PatternWidth; w++)
                {
                    for (byte h = 0; h < appearance.FrameGroup[0].SpriteInfo.PatternHeight; h++)
                    {
                        int index = GetSpriteIndex(appearance.FrameGroup[0], w, h, l, x, 0, 0, 0);
                        int spriteId = (int)appearance.FrameGroup[0].SpriteInfo.SpriteId[index];
                        int px = (int)((appearance.FrameGroup[0].SpriteInfo.PatternWidth - w - 1) * Sprite.DefaultSize);
                        int py = (int)((appearance.FrameGroup[0].SpriteInfo.PatternHeight - h - 1) * Sprite.DefaultSize);

                        using SKBitmap _bmp = SKBitmap.Decode(spriteStorage.getSpriteStream((uint)spriteId));
                        if (_bmp != null)
                            g.DrawBitmap(_bmp, SKRect.Create(px, py, Sprite.DefaultSize, Sprite.DefaultSize));
                    }
                }
            }
            MemoryStream combinedStream = new MemoryStream();
            using (SKImage img = SKImage.FromBitmap(bitmap))
            using (SKData data = img.Encode(SKEncodedImageFormat.Png, 100))
                data.SaveTo(combinedStream);
            combinedStream.Position = 0;
            return combinedStream;
        }
        public static MemoryStream GetObjectImage(Appearance appearance, SpriteStorage spriteStorage, int frame)
        {
            int width = (int)(Sprite.DefaultSize * appearance.FrameGroup[0].SpriteInfo.PatternWidth);
            int height = (int)(Sprite.DefaultSize * appearance.FrameGroup[0].SpriteInfo.PatternHeight);

            // handle bad sprite
            if (width == 0 || height == 0) {
                return new();
            }

            using SKBitmap bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using SKCanvas g = new SKCanvas(bitmap);

            byte layers = (byte)(appearance.FrameGroup[0].SpriteInfo.PatternLayers);
            byte x = 0;

            if (appearance.AppearanceType == APPEARANCE_TYPE.AppearanceOutfit)
            {
                layers = 1;
                x = (byte)(2 % appearance.FrameGroup[0].SpriteInfo.PatternX);
            }

            // draw sprite
            for (byte l = 0; l < layers; l++)
            {
                for (byte w = 0; w < appearance.FrameGroup[0].SpriteInfo.PatternWidth; w++)
                {
                    for (byte h = 0; h < appearance.FrameGroup[0].SpriteInfo.PatternHeight; h++)
                    {
                        int index = GetSpriteIndex(appearance.FrameGroup[0], w, h, l, x, 0, 0, frame);
                        int spriteId = (int)appearance.FrameGroup[0].SpriteInfo.SpriteId[index];
                        int px = (int)((appearance.FrameGroup[0].SpriteInfo.PatternWidth - w - 1) * Sprite.DefaultSize);
                        int py = (int)((appearance.FrameGroup[0].SpriteInfo.PatternHeight - h - 1) * Sprite.DefaultSize);

                        using SKBitmap _bmp = SKBitmap.Decode(spriteStorage.getSpriteStream((uint)spriteId));
                        if (_bmp != null)
                            g.DrawBitmap(_bmp, SKRect.Create(px, py, Sprite.DefaultSize, Sprite.DefaultSize));
                    }
                }
            }
            MemoryStream combinedStream = new();
            using (SKImage img = SKImage.FromBitmap(bitmap))
            using (SKData data = img.Encode(SKEncodedImageFormat.Png, 100))
                data.SaveTo(combinedStream);
            combinedStream.Position = 0;
            return combinedStream;
        }
    }
}
