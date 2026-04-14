using System;
using System.IO;
using SevenZip;
using SevenZip.Compression.LZMA;
using SkiaSharp;

namespace Assets_Editor
{
    class LZMA
    {
        private static CoderPropID[] propIDs =
        {
            CoderPropID.DictionarySize,
            CoderPropID.PosStateBits,
            CoderPropID.LitContextBits,
            CoderPropID.LitPosBits,
            CoderPropID.Algorithm,
            CoderPropID.NumFastBytes,
            CoderPropID.MatchFinder,
            CoderPropID.EndMarker
        };

        public static SKBitmap DecompressFileLZMA(string file)
        {
            using MemoryStream spriteBuffer = new MemoryStream();
            Decoder decoder = new Decoder();
            using BinaryReader reader = new BinaryReader(File.OpenRead(file));

            while (reader.ReadByte() == 0) { }
            reader.ReadUInt32();
            while ((reader.ReadByte() & 0x80) == 0x80) { }
            decoder.SetDecoderProperties(reader.ReadBytes(5));
            reader.ReadUInt64();
            decoder.Code(reader.BaseStream, spriteBuffer, reader.BaseStream.Length - reader.BaseStream.Position, -1, null);
            spriteBuffer.Position = 0;
            return SKBitmap.Decode(spriteBuffer);
        }

        public class MyBinaryWriter : BinaryWriter
        {
            public MyBinaryWriter(Stream stream) : base(stream) { }
            public new void Write7BitEncodedInt(int i)
            {
                base.Write7BitEncodedInt(i);
            }
        }

        public static void CompressFileLZMA(MemoryStream inFile, string outFile)
        {
            int dictionary = 33554432;
            int posStateBits = 2;
            int litContextBits = 3;
            int litPosBits = 0;
            int algorithm = 2;
            int numFastBytes = 32;
            string mf = "bt4";
            bool eos = true;

            object[] properties = { dictionary, posStateBits, litContextBits, litPosBits, algorithm, numFastBytes, mf, eos };
            byte[] TibiaHeader = new byte[32];

            using FileStream outStream = new FileStream(outFile, FileMode.Create);
            outStream.Write(TibiaHeader);
            Encoder encoder = new Encoder();
            encoder.SetCoderProperties(propIDs, properties);
            encoder.WriteCoderProperties(outStream);
            long fileSize = -1;
            for (int i = 0; i < 8; i++)
                outStream.WriteByte((byte)(fileSize >> (8 * i)));

            encoder.Code(inFile, outStream, -1, -1, null);

            MemoryStream tmp = new MemoryStream();
            int size = (int)outStream.Length - 32;
            MyBinaryWriter bw = new MyBinaryWriter(tmp);
            bw.Write(new byte[] { 0x70, 0x0A, 0xFA, 0x80, 0x24 });
            bw.Write7BitEncodedInt(size);

            if (tmp.Length == 8)
                outStream.Position = 0x18;
            else
                outStream.Position = 0x19;

            outStream.Write(tmp.ToArray(), 0, (int)tmp.Length);
            outStream.Position = 0;
            outStream.Close();
        }

        public static byte[] Uncompress(byte[] bytes)
        {
            if (bytes.Length < 5)
                throw new Exception("LZMA data is too short.");

            using MemoryStream input = new MemoryStream(bytes);
            byte[] properties = new byte[5];
            if (input.Read(properties, 0, 5) != 5)
                throw new Exception("LZMA data is too short.");

            long outSize = 0;
            if (BitConverter.IsLittleEndian)
            {
                for (int i = 0; i < 8; i++)
                {
                    int v = input.ReadByte();
                    if (v < 0) throw new Exception("Can't Read 1.");
                    outSize |= ((long)v) << (8 * i);
                }
            }

            MemoryStream output = new MemoryStream();
            long compressedSize = input.Length - input.Position;
            Decoder decoder = new Decoder();
            decoder.SetDecoderProperties(properties);
            decoder.Code(input, output, compressedSize, outSize, null);
            return output.ToArray();
        }

        public static byte[] Compress(byte[] bytes)
        {
            using MemoryStream input = new MemoryStream(bytes);
            object[] properties =
            {
                (int)(1 << 21),
                (int)(2),
                (int)(3),
                (int)(0),
                (int)(2),
                (int)(128),
                "bt4",
                false
            };

            MemoryStream output = new MemoryStream();
            Encoder encoder = new Encoder();
            encoder.SetCoderProperties(propIDs, properties);
            encoder.WriteCoderProperties(output);

            if (BitConverter.IsLittleEndian)
            {
                byte[] LengthHeader = BitConverter.GetBytes(input.Length);
                output.Write(LengthHeader, 0, LengthHeader.Length);
            }

            encoder.Code(input, output, input.Length, -1, null);
            return output.ToArray();
        }

        public static void ExportLzmaFile(SKBitmap targetImg, ref string filename)
        {
            // BMPv4 built by hand to match the Windows writer byte-for-byte:
            //   • positive height → bottom-up row order (Tibia's BMP loader assumes this)
            //   • straight-alpha BGRA (unpremultiplied) — SKCanvas draws into Premul
            //     targets, so we must unpremultiply before writing the pixels out.
            // Any deviation from these two expectations (top-down rows or premultiplied
            // pixels) produces sheets that load in this editor but crash Tibia at runtime.
            int width = targetImg.Width;
            int height = targetImg.Height;
            int pixelDataSize = width * height * 4;

            IntPtr pixelsPtr = targetImg.GetPixels();
            byte[] srcPixels = new byte[pixelDataSize];
            System.Runtime.InteropServices.Marshal.Copy(pixelsPtr, srcPixels, 0, pixelDataSize);

            if (targetImg.AlphaType == SKAlphaType.Premul)
            {
                for (int i = 0; i < pixelDataSize; i += 4)
                {
                    byte a = srcPixels[i + 3];
                    if (a != 0 && a != 255)
                    {
                        srcPixels[i]     = (byte)Math.Min(255, (srcPixels[i]     * 255 + a / 2) / a);
                        srcPixels[i + 1] = (byte)Math.Min(255, (srcPixels[i + 1] * 255 + a / 2) / a);
                        srcPixels[i + 2] = (byte)Math.Min(255, (srcPixels[i + 2] * 255 + a / 2) / a);
                    }
                    else if (a == 0)
                    {
                        srcPixels[i] = 0; srcPixels[i + 1] = 0; srcPixels[i + 2] = 0;
                    }
                }
            }

            MemoryStream outFile = new MemoryStream();

            // BITMAPV4HEADER — matches Windows Assets Editor/LZMA.cs byte-for-byte.
            outFile.WriteByte(0x42);                                    // 'B'
            outFile.WriteByte(0x4D);                                    // 'M'
            outFile.Write(BitConverter.GetBytes(0));                    // file size (patched below)
            outFile.Write(BitConverter.GetBytes((ushort)0));            // reserved
            outFile.Write(BitConverter.GetBytes((ushort)0));            // reserved
            outFile.Write(BitConverter.GetBytes(122));                  // pixel array offset
            outFile.Write(BitConverter.GetBytes(108));                  // bV4Size
            outFile.Write(BitConverter.GetBytes(width));                // bV4Width
            outFile.Write(BitConverter.GetBytes(height));               // bV4Height (positive → bottom-up)
            outFile.Write(BitConverter.GetBytes((ushort)1));            // bV4Planes
            outFile.Write(BitConverter.GetBytes((ushort)32));           // bV4BitCount
            outFile.Write(BitConverter.GetBytes(3));                    // bV4Compression = BI_BITFIELDS
            outFile.Write(BitConverter.GetBytes(0));                    // bV4SizeImage
            outFile.Write(BitConverter.GetBytes(0));                    // bV4XPelsPerMeter
            outFile.Write(BitConverter.GetBytes(0));                    // bV4YPelsPerMeter
            outFile.Write(BitConverter.GetBytes(0));                    // bV4ClrUsed
            outFile.Write(BitConverter.GetBytes(0));                    // bV4ClrImportant
            outFile.Write(new byte[] { 0x0, 0x0, 0xFF, 0x0 }, 0, 4);    // bV4RedMask
            outFile.Write(new byte[] { 0x0, 0xFF, 0x0, 0x0 }, 0, 4);    // bV4GreenMask
            outFile.Write(new byte[] { 0xFF, 0x0, 0x0, 0x0 }, 0, 4);    // bV4BlueMask
            outFile.Write(new byte[] { 0x0, 0x0, 0x0, 0xFF }, 0, 4);    // bV4AlphaMask
            outFile.Write(new byte[] { 0x57, 0x69, 0x6E, 0x20 }, 0, 4); // bV4CSType = "Win "
            outFile.Write(new byte[36]);                                // bV4Endpoints (9 ints)
            outFile.Write(BitConverter.GetBytes(0));                    // bV4GammaRed
            outFile.Write(BitConverter.GetBytes(0));                    // bV4GammaGreen
            outFile.Write(BitConverter.GetBytes(0));                    // bV4GammaBlue

            // Pixel data — write rows bottom-up. SKBitmap is top-down in memory, so
            // row (height-1) goes first.
            int stride = width * 4;
            for (int r = height - 1; r >= 0; r--)
            {
                outFile.Write(srcPixels, r * stride, stride);
            }

            int totalSize = (int)outFile.Length;
            outFile.Seek(2, SeekOrigin.Begin);
            outFile.Write(BitConverter.GetBytes(totalSize));

            outFile.Position = 0;
            CompressFileLZMA(outFile, filename);
        }
    }
}
