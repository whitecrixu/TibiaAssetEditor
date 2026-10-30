using Avalonia.Media.Imaging;
using SkiaSharp;
using Tibia.Protobuf.Appearances;

namespace Assets_Editor
{
    /// <summary>List and header pictures of creatures (monsters, NPCs) from the client loaded in the main window.</summary>
    public static class OutfitPreview
    {
        /// <summary>
        /// The whole outfit facing south in the given colours; on legacy clients its colour mask (layer 1) is painted,
        /// elsewhere the first sprite is used.
        /// </summary>
        public static Bitmap? Outfit(Appearance outfit, int head, int body, int legs, int feet) =>
            LegacyOutfit(outfit, head, body, legs, feet) ?? FirstSprite(outfit);

        private static Bitmap? LegacyOutfit(Appearance outfit, int head, int body, int legs, int feet)
        {
            if (!MainWindow.LegacyClient || MainWindow.MainSprStorage == null || outfit.FrameGroup.Count == 0)
                return null;
            try
            {
                var info = outfit.FrameGroup[0].SpriteInfo;
                // legacy sprite info: PatternX = directions, PatternLayers = base + colour mask
                int south = info.PatternX > 0 ? (int)(2 % info.PatternX) : 0;
                using var figure = SKBitmap.Decode(LegacyAppearance.GetObjectImage(outfit, MainWindow.MainSprStorage, 0, south, 0, 0, 0, 0).ToArray());
                if (figure == null)
                    return null;
                if (info.PatternLayers > 1)
                {
                    using var mask = SKBitmap.Decode(LegacyAppearance.GetObjectImage(outfit, MainWindow.MainSprStorage, 0, south, 0, 0, 0, 1).ToArray());
                    if (mask != null)
                        Utils.ColorizeOutfit(mask, figure, Utils.GetOutfitColor(head), Utils.GetOutfitColor(body),
                            Utils.GetOutfitColor(legs), Utils.GetOutfitColor(feet));
                }
                using var image = SKImage.FromBitmap(figure);
                using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                return new Bitmap(png.AsStream());
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The whole object on legacy clients (items used as a creature's look), else its first sprite.</summary>
        public static Bitmap? Item(Appearance item)
        {
            if (MainWindow.LegacyClient && MainWindow.MainSprStorage != null && item.FrameGroup.Count > 0)
            {
                try
                {
                    var png = LegacyAppearance.GetObjectImage(item, MainWindow.MainSprStorage, 0, 0, 0, 0, 0, -1);
                    if (png.Length > 0)
                        return new Bitmap(png);
                }
                catch
                {
                    // fall back to the first sprite
                }
            }
            return FirstSprite(item);
        }

        public static Bitmap? FirstSprite(Appearance? a)
        {
            try
            {
                if (a == null || a.FrameGroup.Count == 0 || a.FrameGroup[0].SpriteInfo.SpriteId.Count == 0) return null;
                return Utils.MemoryStreamToBitmap(MainWindow.getSpriteStream((int)a.FrameGroup[0].SpriteInfo.SpriteId[0]));
            }
            catch
            {
                return null;
            }
        }
    }
}
