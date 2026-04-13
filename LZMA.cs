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
            // BMPv5 is built by hand below; we don't need SKImage.Encode(Bmp) at all (Linux
            // SkiaSharp doesn't even ship a BMP encoder, so that path used to NRE).
            MemoryStream outFile = new MemoryStream();

            // BMP header
            outFile.WriteByte(0x42);
            outFile.WriteByte(0x4D);
            outFile.Write(BitConverter.GetBytes(0));
            outFile.Write(BitConverter.GetBytes((ushort)0));
            outFile.Write(BitConverter.GetBytes((ushort)0));
            outFile.Write(BitConverter.GetBytes(122));
            outFile.Write(BitConverter.GetBytes(108));
            outFile.Write(BitConverter.GetBytes(targetImg.Width));
            outFile.Write(BitConverter.GetBytes(-targetImg.Height));
            outFile.Write(BitConverter.GetBytes((ushort)1));
            outFile.Write(BitConverter.GetBytes((ushort)32));
            outFile.Write(BitConverter.GetBytes(3));

            int pixelDataSize = targetImg.Width * targetImg.Height * 4;
            outFile.Write(BitConverter.GetBytes(pixelDataSize));
            outFile.Write(BitConverter.GetBytes(2835));
            outFile.Write(BitConverter.GetBytes(2835));
            outFile.Write(BitConverter.GetBytes(0));
            outFile.Write(BitConverter.GetBytes(0));
            outFile.Write(BitConverter.GetBytes(0x00FF0000));
            outFile.Write(BitConverter.GetBytes(0x0000FF00));
            outFile.Write(BitConverter.GetBytes(0x000000FF));
            outFile.Write(BitConverter.GetBytes(unchecked((int)0xFF000000)));
            outFile.Write(BitConverter.GetBytes(0x206E6957));
            outFile.Write(new byte[36]);
            outFile.Write(BitConverter.GetBytes(0));
            outFile.Write(BitConverter.GetBytes(0));
            outFile.Write(BitConverter.GetBytes(0));

            // pixel data
            IntPtr pixelsPtr = targetImg.GetPixels();
            byte[] pixelBytes = new byte[pixelDataSize];
            System.Runtime.InteropServices.Marshal.Copy(pixelsPtr, pixelBytes, 0, pixelDataSize);

            for (int i = 0; i < pixelDataSize; i += 4)
            {
                byte b = pixelBytes[i];
                byte g = pixelBytes[i + 1];
                byte r = pixelBytes[i + 2];
                byte a = pixelBytes[i + 3];
                outFile.WriteByte(b);
                outFile.WriteByte(g);
                outFile.WriteByte(r);
                outFile.WriteByte(a);
            }

            int totalSize = (int)outFile.Length;
            outFile.Seek(2, SeekOrigin.Begin);
            outFile.Write(BitConverter.GetBytes(totalSize));

            outFile.Position = 0;
            CompressFileLZMA(outFile, filename);
        }
    }
}
