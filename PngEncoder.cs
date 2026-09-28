using System;
using System.IO;
using System.IO.Compression;
using Microsoft.Xna.Framework;

namespace StardewDS
{
    /// <summary>
    /// A minimal RGBA8 PNG encoder that works on a plain <see cref="Color"/>
    /// array — no <see cref="Microsoft.Xna.Framework.Graphics.Texture2D"/>,
    /// no graphics device — so it can run on a thread-pool thread instead of
    /// the main game thread.
    ///
    /// MonoGame's <c>Texture2D.SaveAsPng</c> needs a texture (so the main
    /// thread) and was being called for every portrait re-render, which is
    /// where most of the mod's per-frame hitches came from (see
    /// <see cref="PortraitRenderer"/>). Pixels are written exactly as read
    /// back from the GPU, same as <c>SaveAsPng</c> does, so the output is
    /// pixel-identical to the old path.
    /// </summary>
    internal static class PngEncoder
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly uint[] CrcTable = BuildCrcTable();

        /// <summary>Encodes <paramref name="pixels"/> (row-major, <paramref name="width"/> x <paramref name="height"/>) as a PNG. Thread-safe; does not touch the graphics device.</summary>
        public static byte[] Encode(Color[] pixels, int width, int height)
        {
            // Raw scanlines: one filter byte (0 = none) + RGBA per pixel.
            int stride = width * 4 + 1;
            byte[] raw = new byte[stride * height];
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                int src = y * width;
                for (int x = 0; x < width; x++)
                {
                    Color c = pixels[src + x];
                    int o = row + 1 + x * 4;
                    raw[o] = c.R;
                    raw[o + 1] = c.G;
                    raw[o + 2] = c.B;
                    raw[o + 3] = c.A;
                }
            }

            byte[] compressed;
            using (MemoryStream zs = new())
            {
                using (ZLibStream z = new(zs, CompressionLevel.Fastest, leaveOpen: true))
                    z.Write(raw, 0, raw.Length);
                compressed = zs.ToArray();
            }

            using MemoryStream ms = new();
            ms.Write(Signature, 0, Signature.Length);

            byte[] ihdr = new byte[13];
            WriteBigEndian(ihdr, 0, (uint)width);
            WriteBigEndian(ihdr, 4, (uint)height);
            ihdr[8] = 8; // bit depth
            ihdr[9] = 6; // color type: RGBA
            WriteChunk(ms, "IHDR", ihdr);
            WriteChunk(ms, "IDAT", compressed);
            WriteChunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            byte[] header = new byte[8];
            WriteBigEndian(header, 0, (uint)data.Length);
            for (int i = 0; i < 4; i++)
                header[4 + i] = (byte)type[i];
            stream.Write(header, 0, 8);
            stream.Write(data, 0, data.Length);

            uint crc = 0xFFFFFFFF;
            crc = UpdateCrc(crc, header, 4, 4);
            crc = UpdateCrc(crc, data, 0, data.Length);
            byte[] crcBytes = new byte[4];
            WriteBigEndian(crcBytes, 0, crc ^ 0xFFFFFFFF);
            stream.Write(crcBytes, 0, 4);
        }

        private static uint UpdateCrc(uint crc, byte[] buffer, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++)
                crc = CrcTable[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint[] BuildCrcTable()
        {
            uint[] table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static void WriteBigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }
    }
}
