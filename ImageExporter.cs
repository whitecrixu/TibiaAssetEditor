using System.Collections.Generic;
using System.IO;
using Tibia.Protobuf.Appearances;
using System;
using ImageMagick;
using SkiaSharp;

namespace Assets_Editor;

/// <summary>
/// Exports individual assets as GIFs or PNGs
/// </summary>
public static class ImageExporter
{
    private static uint SafeGetFrameDuration(FrameGroup frameGroup, int index)
    {
        var animation = frameGroup.SpriteInfo.Animation;
        if (animation == null || animation.SpritePhase.Count == 0) return 30;
        if (index >= animation.SpritePhase.Count)
            index = animation.SpritePhase.Count - 1;
        return Math.Max(30, frameGroup.SpriteInfo.Animation.SpritePhase[index].DurationMin);
    }

    private static uint SafeGetAnimationFrameCount(FrameGroup frameGroup)
    {
        var animation = frameGroup.SpriteInfo.Animation;
        if (animation == null) return 0;
        return (uint)animation.SpritePhase.Count;
    }

    private static SKBitmap StreamToTransparentBitmap(MemoryStream stream)
    {
        using var original = SKBitmap.Decode(stream.ToArray());
        var result = new SKBitmap(original.Width, original.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(original, 0, 0);
        return result;
    }

    private static void SaveItemAsGIFBasic(string exportPath, Appearance appearance)
    {
        foreach (var frameGroup in appearance.FrameGroup)
        {
            var spriteInfo = frameGroup.SpriteInfo;
            for (int width = 0; width < spriteInfo.PatternWidth; ++width)
            {
                for (int height = 0; height < spriteInfo.PatternHeight; ++height)
                {
                    for (int depth = 0; depth < spriteInfo.PatternDepth; ++depth)
                    {
                        List<(SKBitmap, uint)> frames = [];
                        for (int frame = 0; frame <= SafeGetAnimationFrameCount(frameGroup); ++frame)
                        {
                            for (int layers = 0; layers < spriteInfo.Layers; ++layers)
                            {
                                try
                                {
                                    int spriteIndex = DatEditor.GetSpriteIndex(frameGroup, layers, width, height, depth, frame);
                                    var stream = MainWindow.getSpriteStream((int)spriteInfo.SpriteId[spriteIndex]);
                                    var bitmap = StreamToTransparentBitmap(stream);
                                    frames.Add((bitmap, 100));
                                }
                                catch (Exception ex)
                                {
                                    MainWindow.Log($"Failed to save image for object {appearance.Id}: {ex.Message}");
                                }
                            }
                        }

                        int subTypeIndex = height * (int)spriteInfo.PatternWidth + width;
                        string fileName = $"{subTypeIndex}{(depth > 0 ? ("_" + depth) : "")}.gif";
                        SaveFramesAsGIF(frames, Path.Combine(exportPath, fileName));
                    }
                }
            }
        }
    }

    public static void SaveItemAsGIF(string exportPath, Appearance appearance)
    {
        string itemPath = Path.Combine(exportPath, Convert.ToString(appearance.Id));
        Directory.CreateDirectory(itemPath);
        SaveItemAsGIFBasic(itemPath, appearance);
    }

    public static void SaveOutfitAsImages(string exportPath, Appearance appearance)
    {
        string outfitPath = Path.Combine(exportPath, Convert.ToString(appearance.Id));
        Directory.CreateDirectory(outfitPath);

        foreach (var frameGroup in appearance.FrameGroup)
        {
            var spriteInfo = frameGroup.SpriteInfo;
            for (int height = 0; height < spriteInfo.PatternHeight; ++height)
            {
                for (int depth = 0; depth < spriteInfo.PatternDepth; ++depth)
                {
                    for (int width = 0; width < spriteInfo.PatternWidth; ++width)
                    {
                        for (int frames = 0; frames <= SafeGetAnimationFrameCount(frameGroup); ++frames)
                        {
                            for (int layers = 0; layers < spriteInfo.Layers; ++layers)
                            {
                                try
                                {
                                    int index = DatEditor.GetSpriteIndex(frameGroup, layers, width, height, depth, frames);
                                    string fileName = $"{frameGroup.Id}_{height + 1}_{depth + 1}_{width + 1}_{frames + 1}{(layers == 1 ? "_template" : "")}.png";

                                    var stream = MainWindow.getSpriteStream((int)spriteInfo.SpriteId[index]);
                                    using var bitmap = StreamToTransparentBitmap(stream);
                                    using var image = SKImage.FromBitmap(bitmap);
                                    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                                    using var fs = File.OpenWrite(Path.Combine(outfitPath, fileName));
                                    data.SaveTo(fs);
                                }
                                catch (Exception ex)
                                {
                                    MainWindow.Log($"Failed to save image for outfit {appearance.Id}: {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    public static void SaveEffectAsGIF(string exportPath, Appearance appearance)
    {
        List<(SKBitmap, uint)> frames = [];
        var frameGroup = appearance.FrameGroup[0];
        int layer = 0, depth = 0, height = 0, width = 0;

        try
        {
            for (int animationFrame = 0; animationFrame < frameGroup.SpriteInfo.SpriteId.Count; animationFrame++)
            {
                int index = DatEditor.GetSpriteIndex(frameGroup, layer, (int)Math.Min(width, frameGroup.SpriteInfo.PatternWidth - 1), height, depth, animationFrame);
                var stream = MainWindow.getSpriteStream((int)frameGroup.SpriteInfo.SpriteId[index]);
                uint frameDuration = SafeGetFrameDuration(frameGroup, index);
                var bitmap = StreamToTransparentBitmap(stream);
                frames.Add((bitmap, frameDuration));
            }
            SaveFramesAsGIF(frames, Path.Combine(exportPath, "" + appearance.Id + ".gif"));
        }
        catch { MainWindow.Log("Invalid animation for sprite " + appearance.Id + "."); }
        finally { foreach (var (frame, _) in frames) frame.Dispose(); }
    }

    public static void SaveMissileAsGIF(string exportPath, Appearance appearance)
    {
        int[] frameOrder = [1, 2, 5, 8, 7, 6, 3, 0];
        List<(SKBitmap, uint)> frames = [];

        try
        {
            var spriteInfo = appearance.FrameGroup[0].SpriteInfo;
            foreach (int frameIndex in frameOrder)
            {
                if (frameIndex < spriteInfo.SpriteId.Count)
                {
                    var stream = MainWindow.getSpriteStream((int)spriteInfo.SpriteId[frameIndex]);
                    var bitmap = StreamToTransparentBitmap(stream);
                    frames.Add((bitmap, 100));
                }
            }
            SaveFramesAsGIF(frames, Path.Combine(exportPath, "" + appearance.Id + ".gif"));
        }
        catch { MainWindow.Log("Invalid animation for sprite " + appearance.Id + "."); }
        finally { foreach (var (frame, _) in frames) frame.Dispose(); }
    }

    public static void SaveFramesAsGIF(List<(SKBitmap, uint)> frames, string fullFilePath)
    {
        if (frames.Count == 0) return;

        var collection = new MagickImageCollection();
        foreach (var (bitmap, duration) in frames)
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new MemoryStream(data.ToArray());
            ms.Position = 0;

            MagickImage magickImage = new(ms);
            magickImage.AnimationDelay = duration / 10;
            magickImage.GifDisposeMethod = GifDisposeMethod.Background;
            collection.Add(magickImage);
        }

        collection[0].AnimationIterations = 0;
        collection.Optimize();
        collection.Write(fullFilePath);
    }
}
