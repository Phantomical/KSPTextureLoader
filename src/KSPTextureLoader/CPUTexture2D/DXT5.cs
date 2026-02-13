using System;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    public readonly struct DXT5 : ICPUTexture2D
    {
        struct Block(ulong alpha, ulong color)
        {
            public ulong alpha = alpha;
            public ulong color = color;
        }

        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.DXT5;

        readonly NativeArray<Block> data;

        public unsafe DXT5(NativeArray<byte> data, int width, int height, int mipCount)
        {
            this.data = data.Reinterpret<Block>(sizeof(byte));
            this.Width = width;
            this.Height = height;
            this.MipCount = mipCount;

            int expected = GetTotalBlockCount(width, height, mipCount) * sizeof(Block);
            if (expected != data.Length)
                throw new Exception(
                    $"data size did not match expected texture size (expected {expected}, but got {data.Length} instead)"
                );
        }

        public Color GetPixel(int x, int y, int mipLevel = 0)
        {
            GetBlockIndex(Width, Height, x, y, mipLevel, out int blockIndex, out int pixelIndex);
            Block block = data[blockIndex];
            float alpha = DecodeBC4Channel(block.alpha, pixelIndex);
            Color rgb = DecodeDXT1Color(block.color, pixelIndex);
            return new Color(rgb.r, rgb.g, rgb.b, alpha);
        }

        static Color DecodeDXT1Color(ulong bits, int pixelIndex)
        {
            ushort c0Raw = (ushort)(bits & 0xFFFF);
            ushort c1Raw = (ushort)((bits >> 16) & 0xFFFF);

            float r0 = ((c0Raw >> 11) & 0x1F) * (1f / 31f);
            float g0 = ((c0Raw >> 5) & 0x3F) * (1f / 63f);
            float b0 = (c0Raw & 0x1F) * (1f / 31f);

            float r1 = ((c1Raw >> 11) & 0x1F) * (1f / 31f);
            float g1 = ((c1Raw >> 5) & 0x3F) * (1f / 63f);
            float b1 = (c1Raw & 0x1F) * (1f / 31f);

            int localY = pixelIndex / 4;
            int localX = pixelIndex % 4;
            byte indexByte = (byte)((bits >> (32 + localY * 8)) & 0xFF);
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

        public unsafe NativeArray<T> GetRawTextureData<T>()
            where T : unmanaged
        {
            return GetNonOwningNativeArray(data).Reinterpret<T>(sizeof(Block));
        }
    }
}
