using System;
using SkiaSharp;

namespace Efundies
{
    public class LockBitmap
    {
        private readonly SKBitmap _source;

        public byte[] Pixels { get; set; } = Array.Empty<byte>();
        public int Depth { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Length { get; private set; }

        public LockBitmap(SKBitmap source)
        {
            _source = source;
        }

        public void LockBits()
        {
            Width = _source.Width;
            Height = _source.Height;
            Depth = _source.BytesPerPixel * 8;
            Length = Width * Height * 4;

            Pixels = new byte[Length];
            IntPtr ptr = _source.GetPixels();
            System.Runtime.InteropServices.Marshal.Copy(ptr, Pixels, 0, Length);
        }

        public void UnlockBits()
        {
            IntPtr ptr = _source.GetPixels();
            System.Runtime.InteropServices.Marshal.Copy(Pixels, 0, ptr, Length);
            _source.NotifyPixelsChanged();
        }

        public SKColor GetPixel(int x, int y)
        {
            int cCount = 4;
            int i = ((y * Width) + x) * cCount;

            if (i > Pixels.Length - cCount)
                throw new IndexOutOfRangeException();

            byte b = Pixels[i];
            byte g = Pixels[i + 1];
            byte r = Pixels[i + 2];
            byte a = Pixels[i + 3];
            return new SKColor(r, g, b, a);
        }

        public void SetPixel(int x, int y, SKColor color)
        {
            int cCount = 4;
            int i = ((y * Width) + x) * cCount;

            Pixels[i] = color.Blue;
            Pixels[i + 1] = color.Green;
            Pixels[i + 2] = color.Red;
            Pixels[i + 3] = color.Alpha;
        }

        public void CopyPixels(SKBitmap source, int rx, int ry, int rw, int rh, int px, int py)
        {
            int maxIndex = this.Length - 4;

            for (int y = 0; y < rh; y++)
            {
                int row = ((py + y) * this.Width);

                for (int x = 0; x < rw; x++)
                {
                    SKColor color = source.GetPixel(rx + x, ry + y);
                    if (color.Alpha != 0)
                    {
                        int i = (row + (px + x)) * 4;
                        if (i > maxIndex)
                            continue;

                        this.Pixels[i] = color.Blue;
                        this.Pixels[i + 1] = color.Green;
                        this.Pixels[i + 2] = color.Red;
                        this.Pixels[i + 3] = color.Alpha;
                    }
                }
            }
        }
    }
}
