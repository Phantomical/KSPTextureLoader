using System;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    public readonly struct DXT5 : ICPUTexture2D
    {
        const int BytesPerBlock = 16;

        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.DXT5;

        readonly NativeArray<byte> data;

        public DXT5(NativeArray<byte> data, int width, int height, int mipCount)
        {
            this.data = data;
            this.Width = width;
            this.Height = height;
            this.MipCount = mipCount;

            int expected = GetTotalByteSize(width, height, mipCount);
            if (expected != data.Length)
                throw new Exception(
                    $"data size did not match expected texture size (expected {expected}, but got {data.Length} instead)"
                );
        }

        public Color GetPixel(int x, int y, int mipLevel = 0)
        {
            int mipWidth = Width;
            int mipHeight = Height;
            int byteOffset = 0;

            for (int m = 0; m < mipLevel; m++)
            {
                int bw = (mipWidth + 3) / 4;
                int bh = (mipHeight + 3) / 4;
                byteOffset += bw * bh * BytesPerBlock;
                mipWidth = Math.Max(mipWidth >> 1, 1);
                mipHeight = Math.Max(mipHeight >> 1, 1);
            }

            x = Mathf.Clamp(x, 0, mipWidth - 1);
            y = Mathf.Clamp(y, 0, mipHeight - 1);

            int blocksPerRow = (mipWidth + 3) / 4;
            int blockX = x / 4;
            int blockY = y / 4;
            int localX = x % 4;
            int localY = y % 4;

            int blockOffset = byteOffset + (blockY * blocksPerRow + blockX) * BytesPerBlock;

            // Decode alpha from BC4 block (first 8 bytes)
            float alpha = DecodeBC4Alpha(blockOffset, localX, localY);

            // Decode RGB from DXT1 block (next 8 bytes)
            int colorOffset = blockOffset + 8;
            Color rgb = DecodeDXT1Color(colorOffset, localX, localY);

            return new Color(rgb.r, rgb.g, rgb.b, alpha);
        }

        float DecodeBC4Alpha(int offset, int localX, int localY)
        {
            byte a0 = data[offset];
            byte a1 = data[offset + 1];

            // 48-bit index data in bytes [offset+2..offset+7]
            // Each pixel has a 3-bit index
            int pixelIndex = localY * 4 + localX;
            int bitOffset = pixelIndex * 3;
            int byteIndex = offset + 2 + bitOffset / 8;
            int bitShift = bitOffset % 8;

            // Read 2 bytes to extract the 3-bit index (may span a byte boundary)
            int raw = data[byteIndex] | (data[byteIndex + 1] << 8);
            int code = (raw >> bitShift) & 0x7;

            float fa0 = a0 * (1f / 255f);
            float fa1 = a1 * (1f / 255f);

            if (a0 > a1)
            {
                return code switch
                {
                    0 => fa0,
                    1 => fa1,
                    2 => (6f * fa0 + 1f * fa1) * (1f / 7f),
                    3 => (5f * fa0 + 2f * fa1) * (1f / 7f),
                    4 => (4f * fa0 + 3f * fa1) * (1f / 7f),
                    5 => (3f * fa0 + 4f * fa1) * (1f / 7f),
                    6 => (2f * fa0 + 5f * fa1) * (1f / 7f),
                    _ => (1f * fa0 + 6f * fa1) * (1f / 7f),
                };
            }
            else
            {
                return code switch
                {
                    0 => fa0,
                    1 => fa1,
                    2 => (4f * fa0 + 1f * fa1) * (1f / 5f),
                    3 => (3f * fa0 + 2f * fa1) * (1f / 5f),
                    4 => (2f * fa0 + 3f * fa1) * (1f / 5f),
                    5 => (1f * fa0 + 4f * fa1) * (1f / 5f),
                    6 => 0f,
                    _ => 1f,
                };
            }
        }

        Color DecodeDXT1Color(int offset, int localX, int localY)
        {
            ushort c0Raw = (ushort)(data[offset] | (data[offset + 1] << 8));
            ushort c1Raw = (ushort)(data[offset + 2] | (data[offset + 3] << 8));

            float r0 = ((c0Raw >> 11) & 0x1F) * (1f / 31f);
            float g0 = ((c0Raw >> 5) & 0x3F) * (1f / 63f);
            float b0 = (c0Raw & 0x1F) * (1f / 31f);

            float r1 = ((c1Raw >> 11) & 0x1F) * (1f / 31f);
            float g1 = ((c1Raw >> 5) & 0x3F) * (1f / 63f);
            float b1 = (c1Raw & 0x1F) * (1f / 31f);

            byte indexByte = data[offset + 4 + localY];
            int index = (indexByte >> (localX * 2)) & 0x3;

            if (c0Raw > c1Raw)
            {
                // 4-color mode
                return index switch
                {
                    0 => new Color(r0, g0, b0),
                    1 => new Color(r1, g1, b1),
                    2 => new Color(
                        (2f * r0 + r1) * (1f / 3f),
                        (2f * g0 + g1) * (1f / 3f),
                        (2f * b0 + b1) * (1f / 3f)
                    ),
                    _ => new Color(
                        (r0 + 2f * r1) * (1f / 3f),
                        (g0 + 2f * g1) * (1f / 3f),
                        (b0 + 2f * b1) * (1f / 3f)
                    ),
                };
            }
            else
            {
                // 3-color + transparent-black mode
                return index switch
                {
                    0 => new Color(r0, g0, b0),
                    1 => new Color(r1, g1, b1),
                    2 => new Color((r0 + r1) * 0.5f, (g0 + g1) * 0.5f, (b0 + b1) * 0.5f),
                    _ => new Color(0f, 0f, 0f),
                };
            }
        }

        public Color32 GetPixel32(int x, int y, int mipLevel = 0) => GetPixel(x, y, mipLevel);

        public Color GetPixelBilinear(float u, float v, int mipLevel = 0) =>
            CPUTexture2D.GetPixelBilinear(in this, u, v, mipLevel);

        public NativeArray<T> GetRawTextureData<T>()
            where T : unmanaged
        {
            return GetNonOwningNativeArray(data).Reinterpret<T>(sizeof(byte));
        }

        static int GetTotalByteSize(int width, int height, int mipCount)
        {
            int size = 0;
            for (int m = 0; m < mipCount; m++)
            {
                int bw = (width + 3) / 4;
                int bh = (height + 3) / 4;
                size += bw * bh * BytesPerBlock;
                width = Math.Max(width >> 1, 1);
                height = Math.Max(height >> 1, 1);
            }
            return size;
        }
    }
}
