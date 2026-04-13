using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SkiaSharp;
using Tibia.Protobuf.Appearances;
using Appearance = Tibia.Protobuf.Appearances.Appearance;

namespace Assets_Editor;

public static class Utils
{
    public static Avalonia.Media.Color Get8Bit(int color)
    {
        return new Avalonia.Media.Color(
            255,
            (byte)(color / 36 % 6 * 51),
            (byte)(color / 6 % 6 * 51),
            (byte)(color % 6 * 51)
        );
    }

    public static SKColor GetOutfitColor(int color)
    {
        const int HSI_SI_VALUES = 7;
        const int HSI_H_STEPS = 19;

        if (color >= HSI_H_STEPS * HSI_SI_VALUES)
            color = 0;

        float loc1 = 0, loc2 = 0, loc3 = 0;
        if (color % HSI_H_STEPS != 0)
        {
            loc1 = color % HSI_H_STEPS * 1.0f / 18.0f;
            loc2 = 1;
            loc3 = 1;

            switch (color / HSI_H_STEPS)
            {
                case 0: loc2 = 0.25f; loc3 = 1.00f; break;
                case 1: loc2 = 0.25f; loc3 = 0.75f; break;
                case 2: loc2 = 0.50f; loc3 = 0.75f; break;
                case 3: loc2 = 0.667f; loc3 = 0.75f; break;
                case 4: loc2 = 1.00f; loc3 = 1.00f; break;
                case 5: loc2 = 1.00f; loc3 = 0.75f; break;
                case 6: loc2 = 1.00f; loc3 = 0.50f; break;
            }
        }
        else
        {
            loc1 = 0;
            loc2 = 0;
            loc3 = 1 - (float)color / HSI_H_STEPS / HSI_SI_VALUES;
        }

        if (loc3 == 0)
            return new SKColor(0, 0, 0);

        if (loc2 == 0)
        {
            int loc7 = (int)(loc3 * 255);
            return new SKColor((byte)loc7, (byte)loc7, (byte)loc7);
        }

        float red = 0, green = 0, blue = 0;

        if (loc1 < 1.0 / 6.0)
        {
            red = loc3;
            blue = loc3 * (1 - loc2);
            green = blue + (loc3 - blue) * 6 * loc1;
        }
        else if (loc1 < 2.0 / 6.0)
        {
            green = loc3;
            blue = loc3 * (1 - loc2);
            red = green - (loc3 - blue) * (6 * loc1 - 1);
        }
        else if (loc1 < 3.0 / 6.0)
        {
            green = loc3;
            red = loc3 * (1 - loc2);
            blue = red + (loc3 - red) * (6 * loc1 - 2);
        }
        else if (loc1 < 4.0 / 6.0)
        {
            blue = loc3;
            red = loc3 * (1 - loc2);
            green = blue - (loc3 - red) * (6 * loc1 - 3);
        }
        else if (loc1 < 5.0 / 6.0)
        {
            blue = loc3;
            green = loc3 * (1 - loc2);
            red = green + (loc3 - green) * (6 * loc1 - 4);
        }
        else
        {
            red = loc3;
            green = loc3 * (1 - loc2);
            blue = red - (loc3 - green) * (6 * loc1 - 5);
        }

        return new SKColor((byte)(red * 255), (byte)(green * 255), (byte)(blue * 255));
    }

    public static Bitmap? MemoryStreamToBitmap(MemoryStream? stream)
    {
        if (stream == null || stream.Length == 0)
            return null;

        stream.Seek(0, SeekOrigin.Begin);
        return new Bitmap(stream);
    }

    public static Bitmap? SKBitmapToAvaloniaBitmap(SKBitmap skBitmap)
    {
        if (skBitmap == null) return null;

        using var image = SKImage.FromBitmap(skBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream(data.ToArray());
        return new Bitmap(ms);
    }

    public static SKBitmap? MemoryStreamToSKBitmap(MemoryStream? stream)
    {
        if (stream == null || stream.Length == 0)
            return null;
        return SKBitmap.Decode(stream.ToArray());
    }

    public static bool ByteArrayCompare(byte[] a1, byte[] a2)
    {
        if (a1.Length != a2.Length)
            return false;

        for (int i = 0; i < a1.Length; i++)
        {
            if (a1[i] != a2[i])
                return false;
        }

        return true;
    }

    public static SKBitmap ConvertBackgroundToMagenta(SKBitmap original, bool opaque)
    {
        var magenta = opaque
            ? new SKColor(255, 0, 255, 255)
            : new SKColor(255, 0, 255, 0);

        var result = new SKBitmap(original.Width, original.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);
        canvas.Clear(magenta);
        canvas.DrawBitmap(original, 0, 0);
        return result;
    }

    public static ushort GetLastIdOrZero<T>(Google.Protobuf.Collections.RepeatedField<T> collection, Func<T, uint> idSelector)
    {
        if (collection.Count == 0) return 0;
        uint maxId = 0;
        foreach (var item in collection)
        {
            var id = idSelector(item);
            if (id > maxId) maxId = id;
        }
        return (ushort)maxId;
    }

    public static ushort GetLastIdOrZero<T>(IList<T> list, Func<T, uint> idSelector)
    {
        return (ushort)(list.Count > 0 ? idSelector(list[^1]) : 0);
    }

    /// <summary>
    /// Colorize an outfit bitmap using a template bitmap. Template pixel channels determine which
    /// body part each pixel belongs to: yellow=head, red=body, green=legs, blue=feet.
    /// </summary>
    public static void ColorizeOutfit(SKBitmap imageTemplate, SKBitmap imageOutfit, SKColor head, SKColor body, SKColor legs, SKColor feet)
    {
        int w = Math.Min(imageTemplate.Width, imageOutfit.Width);
        int h = Math.Min(imageTemplate.Height, imageOutfit.Height);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var tp = imageTemplate.GetPixel(x, y);
                var op = imageOutfit.GetPixel(x, y);

                if (tp == op) continue;

                int ro = op.Red, go = op.Green, bo = op.Blue;

                if (tp.Red > 0 && tp.Green > 0 && tp.Blue == 0) // yellow == head
                    ColorizePixel(ref ro, ref go, ref bo, head);
                else if (tp.Red > 0 && tp.Green == 0 && tp.Blue == 0) // red == body
                    ColorizePixel(ref ro, ref go, ref bo, body);
                else if (tp.Red == 0 && tp.Green > 0 && tp.Blue == 0) // green == legs
                    ColorizePixel(ref ro, ref go, ref bo, legs);
                else if (tp.Red == 0 && tp.Green == 0 && tp.Blue > 0) // blue == feet
                    ColorizePixel(ref ro, ref go, ref bo, feet);
                else
                    continue;

                imageOutfit.SetPixel(x, y, new SKColor((byte)ro, (byte)go, (byte)bo, op.Alpha));
            }
        }
    }

    private static void ColorizePixel(ref int r, ref int g, ref int b, SKColor colorPart)
    {
        r = r * colorPart.Red / 255;
        g = g * colorPart.Green / 255;
        b = b * colorPart.Blue / 255;
    }

    /// <summary>
    /// Updates inner values when cloning an appearance (market and cyclopedia flags).
    /// </summary>
    public static void OnAppearanceCloned(Appearance origItem, ref Appearance clonedItem)
    {
        if (clonedItem.Flags?.Market != null)
        {
            if (clonedItem.Flags.Market.HasTradeAsObjectId && clonedItem.Flags.Market.TradeAsObjectId == origItem.Id)
                clonedItem.Flags.Market.TradeAsObjectId = clonedItem.Id;
            if (clonedItem.Flags.Market.HasShowAsObjectId && clonedItem.Flags.Market.ShowAsObjectId == origItem.Id)
                clonedItem.Flags.Market.ShowAsObjectId = clonedItem.Id;
        }
        if (clonedItem.Flags?.Cyclopediaitem != null)
        {
            if (clonedItem.Flags.Cyclopediaitem.HasCyclopediaType && clonedItem.Flags.Cyclopediaitem.CyclopediaType == origItem.Id)
                clonedItem.Flags.Cyclopediaitem.CyclopediaType = clonedItem.Id;
        }
    }

    public class Outfit
    {
        public Outfit(ushort type, byte head, byte body, byte legs, byte feet, byte addons)
        {
            Type = type; Head = head; Body = body; Legs = legs; Feet = feet; Addons = addons;
        }
        public Outfit() : this(0, 0, 0, 0, 0, 0) { }
        public ushort Type { get; set; }
        public byte Head { get; set; }
        public byte Body { get; set; }
        public byte Legs { get; set; }
        public byte Feet { get; set; }
        public byte Addons { get; set; }
    }
}
