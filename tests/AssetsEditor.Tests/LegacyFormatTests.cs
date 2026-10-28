using System;
using System.IO;
using System.Linq;
using SkiaSharp;
using Tibia.Protobuf.Appearances;
using Xunit;

namespace Assets_Editor.Tests;

// Round trips of the legacy 8.60 formats (structure 5, extended, frame durations, frame groups),
// the layout of the NewEra / WolfOTS client.
public class LegacyFormatTests : IDisposable
{
    private const int Structure860 = 5;
    private readonly string _dir = Directory.CreateTempSubdirectory("tae-tests-").FullName;

    public LegacyFormatTests()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        MainWindow.Preset = new PresetSettings { Version = Structure860, Extended = true, FrameDurations = true, FrameGroups = true };
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private static Appearance Thing(uint id, APPEARANCE_TYPE type, uint frames = 1, uint groups = 1)
    {
        var a = new Appearance { Id = id, AppearanceType = type, Flags = new AppearanceFlags() };
        for (uint g = 0; g < groups; g++)
        {
            var info = new SpriteInfo { PatternWidth = 1, PatternHeight = 1, PatternSize = 32, PatternLayers = 1, PatternX = 1, PatternY = 1, PatternZ = 1, PatternFrames = frames };
            if (frames > 1)
            {
                info.Animation = new SpriteAnimation { AnimationMode = ANIMATION_ANIMATION_MODE.AnimationAsynchronized, LoopCount = uint.MaxValue, DefaultStartPhase = 0 };
                for (int f = 0; f < frames; f++)
                    info.Animation.SpritePhase.Add(new SpritePhase { DurationMin = 100, DurationMax = 200 });
            }
            for (uint s = 0; s < frames; s++)
                info.SpriteId.Add(id * 10 + s);
            a.FrameGroup.Add(new FrameGroup { FixedFrameGroup = (FIXED_FRAME_GROUP)g, SpriteInfo = info });
        }
        return a;
    }

    private static Appearances Sample()
    {
        var all = new Appearances();
        for (uint id = 100; id < 110; id++)
            all.Object.Add(Thing(id, APPEARANCE_TYPE.AppearanceObject));

        var f = all.Object[0].Flags;
        f.Bank = new AppearanceFlagBank { Waypoints = 150 };
        f.Clip = true;
        f.Lenshelp = new AppearanceFlagLenshelp { Id = 1104 };
        f.Fullbank = true;
        f.Light = new AppearanceFlagLight { Brightness = 50, Color = 215 };
        f.Automap = new AppearanceFlagAutomap { Color = 186 };

        all.Object[1].Flags.Market = new AppearanceFlagMarket { Category = ITEM_CATEGORY.Others, TradeAsObjectId = 101, ShowAsObjectId = 101, MinimumLevel = 8 };
        all.Object[1].Name = "café";
        all.Object[1].Flags.Write = new AppearanceFlagWrite { MaxTextLength = 3998 };
        all.Object[2].Flags.Shift = new AppearanceFlagShift { X = 8, Y = 8 };
        all.Object[2].Flags.Clothes = new AppearanceFlagClothes { Slot = 12 };
        all.Object[3] = Thing(103, APPEARANCE_TYPE.AppearanceObject, frames: 3);

        all.Outfit.Add(Thing(1, APPEARANCE_TYPE.AppearanceOutfit, frames: 2, groups: 2));
        all.Effect.Add(Thing(1, APPEARANCE_TYPE.AppearanceEffect, frames: 4));
        all.Missile.Add(Thing(1, APPEARANCE_TYPE.AppearanceMissile));
        return all;
    }

    [Fact]
    public void Dat_write_read_write_is_byte_identical()
    {
        string first = Path.Combine(_dir, "a.dat"), second = Path.Combine(_dir, "b.dat");
        Assert.True(LegacyAppearance.WriteLegacyDat(first, 0x4C2C7993, Sample(), Structure860));

        var read = new LegacyAppearance();
        read.ReadLegacyDat(first, Structure860);
        Assert.True(LegacyAppearance.WriteLegacyDat(second, read.Signature, read.Appearances, Structure860));

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public void Dat_keeps_values_the_ui_used_to_lose()
    {
        string path = Path.Combine(_dir, "a.dat");
        LegacyAppearance.WriteLegacyDat(path, 0x4C2C7993, Sample(), Structure860);
        var read = new LegacyAppearance();
        read.ReadLegacyDat(path, Structure860);
        var objects = read.Appearances.Object;

        Assert.Equal(1104u, objects[0].Flags.Lenshelp.Id);
        Assert.Equal(50u, objects[0].Flags.Light.Brightness);
        Assert.Equal("café", objects[1].Name);
        Assert.Equal(101u, objects[1].Flags.Market.TradeAsObjectId);
        Assert.Equal(8u, objects[1].Flags.Market.MinimumLevel);
        Assert.Equal(3998u, objects[1].Flags.Write.MaxTextLength);
        Assert.Equal(12u, objects[2].Flags.Clothes.Slot);
        Assert.Equal(uint.MaxValue, objects[3].FrameGroup[0].SpriteInfo.Animation.LoopCount); // ping-pong
        Assert.Equal(2, read.Appearances.Outfit[0].FrameGroup.Count);
    }

    [Fact]
    public void Dat_with_an_id_gap_is_refused()
    {
        var all = Sample();
        all.Object.RemoveAt(4);
        string path = Path.Combine(_dir, "gap.dat");
        Assert.False(LegacyAppearance.WriteLegacyDat(path, 0x4C2C7993, all, Structure860));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void Dat_read_with_the_wrong_structure_fails(int structure)
    {
        string path = Path.Combine(_dir, "a.dat");
        LegacyAppearance.WriteLegacyDat(path, 0x4C2C7993, Sample(), Structure860);
        Assert.ThrowsAny<Exception>(() => new LegacyAppearance().ReadLegacyDat(path, structure));
    }

    // a tiny spr: sprite 1 red, sprite 2 empty (address 0), sprite 3 green
    private string WriteSampleSpr()
    {
        string path = Path.Combine(_dir, "Tibia.spr");
        byte[] Pixels(SKColor c)
        {
            using var bmp = new SKBitmap(32, 32, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            bmp.Erase(c);
            return Sprite.CompressBitmap(bmp, false);
        }
        var sprites = new[] { Pixels(SKColors.Red), null, Pixels(SKColors.Lime) };
        using var w = new BinaryWriter(File.Create(path));
        w.Write(0x4C220594u);
        w.Write((uint)sprites.Length);
        long table = w.BaseStream.Position;
        w.Write(new byte[sprites.Length * 4]);
        for (int i = 0; i < sprites.Length; i++)
        {
            if (sprites[i] == null) continue;
            uint address = (uint)w.BaseStream.Position;
            w.Write((byte)0xFF); w.Write((byte)0x00); w.Write((byte)0xFF);
            w.Write((ushort)sprites[i]!.Length);
            w.Write(sprites[i]!);
            long end = w.BaseStream.Position;
            w.Seek((int)(table + i * 4), SeekOrigin.Begin);
            w.Write(address);
            w.Seek((int)end, SeekOrigin.Begin);
        }
        return path;
    }

    private static SKColor PixelOf(SpriteStorage storage, uint id)
    {
        using var stream = storage.getSpriteStream(id);
        using var bmp = SKBitmap.Decode(stream.ToArray());
        return bmp.GetPixel(10, 10);
    }

    [Fact]
    public void Spr_unchanged_compile_is_byte_identical_and_empty_sprites_read_blank()
    {
        string source = WriteSampleSpr();
        var storage = new SpriteStorage(source, false);
        storage.LoadSprites();

        Assert.Equal(SKColors.Red, PixelOf(storage, 1));
        Assert.Equal(0, PixelOf(storage, 2).Alpha);
        Assert.Equal(SKColors.Lime, PixelOf(storage, 3));

        string output = Path.Combine(_dir, "out.spr");
        storage.Compile(output, storage.Signature, false, true, null);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(output));
    }

    [Fact]
    public void Spr_compile_rewrites_only_the_modified_sprite()
    {
        var storage = new SpriteStorage(WriteSampleSpr(), false);
        storage.LoadSprites();

        using (var blue = new SKBitmap(32, 32, SKColorType.Bgra8888, SKAlphaType.Unpremul))
        {
            blue.Erase(SKColors.Blue);
            byte[] compressed = Sprite.CompressBitmap(blue, false);
            storage.SetSprite(3, compressed);
        }

        string output = Path.Combine(_dir, "out.spr");
        storage.Compile(output, storage.Signature, false, true, null);

        var reread = new SpriteStorage(output, false);
        reread.LoadSprites();
        Assert.Equal(SKColors.Red, PixelOf(reread, 1));
        Assert.Equal(0, PixelOf(reread, 2).Alpha);
        Assert.Equal(SKColors.Blue, PixelOf(reread, 3));
    }

    [Fact]
    public void Spr_added_sprite_is_appended()
    {
        var storage = new SpriteStorage(WriteSampleSpr(), false);
        storage.LoadSprites();
        using var yellow = new SKBitmap(32, 32, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        yellow.Erase(SKColors.Yellow);
        uint id = storage.AddSprite(Sprite.CompressBitmap(yellow, false));
        Assert.Equal(4u, id);

        string output = Path.Combine(_dir, "out.spr");
        storage.Compile(output, storage.Signature, false, true, null);
        var reread = new SpriteStorage(output, false);
        reread.LoadSprites();
        Assert.Equal(4u, reread.SpriteCount);
        Assert.Equal(SKColors.Yellow, PixelOf(reread, 4));
        Assert.Equal(SKColors.Red, PixelOf(reread, 1));
    }
}
