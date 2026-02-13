using System;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    public readonly struct BC4 : ICPUTexture2D
    {
        struct Block(ulong bits)
        {
            public ulong bits = bits;
        }

        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.BC4;

        readonly NativeArray<Block> data;

        public unsafe BC4(NativeArray<byte> data, int width, int height, int mipCount)
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
            int mipWidth = Width;
            int mipHeight = Height;
            int blockOffset = 0;

            for (int m = 0; m < mipLevel; m++)
            {
                int bw = (mipWidth + 3) / 4;
                int bh = (mipHeight + 3) / 4;
                blockOffset += bw * bh;
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

            int blockIndex = blockOffset + blockY * blocksPerRow + blockX;
            int pixelIndex = localY * 4 + localX;

            float red = DecodeBC4Channel(data[blockIndex].bits, pixelIndex);
            return new Color(red, 0f, 0f, 0f);
        }

        static float DecodeBC4Channel(ulong bits, int pixelIndex)
        {
            byte r0 = (byte)(bits & 0xFF);
            byte r1 = (byte)((bits >> 8) & 0xFF);

            // 48-bit index data starts at bit 16; each pixel has a 3-bit index
            int code = (int)((bits >> (16 + pixelIndex * 3)) & 0x7);

            float fr0 = r0 * (1f / 255f);
            float fr1 = r1 * (1f / 255f);

            if (r0 > r1)
            {
                return code switch
                {
                    0 => fr0,
                    1 => fr1,
                    2 => (6f * fr0 + 1f * fr1) * (1f / 7f),
                    3 => (5f * fr0 + 2f * fr1) * (1f / 7f),
                    4 => (4f * fr0 + 3f * fr1) * (1f / 7f),
                    5 => (3f * fr0 + 4f * fr1) * (1f / 7f),
                    6 => (2f * fr0 + 5f * fr1) * (1f / 7f),
                    _ => (1f * fr0 + 6f * fr1) * (1f / 7f),
                };
            }
            else
            {
                return code switch
                {
                    0 => fr0,
                    1 => fr1,
                    2 => (4f * fr0 + 1f * fr1) * (1f / 5f),
                    3 => (3f * fr0 + 2f * fr1) * (1f / 5f),
                    4 => (2f * fr0 + 3f * fr1) * (1f / 5f),
                    5 => (1f * fr0 + 4f * fr1) * (1f / 5f),
                    6 => 0f,
                    _ => 1f,
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

        static int GetTotalBlockCount(int width, int height, int mipCount)
        {
            int count = 0;
            for (int m = 0; m < mipCount; m++)
            {
                int bw = (width + 3) / 4;
                int bh = (height + 3) / 4;
                count += bw * bh;
                width = Math.Max(width >> 1, 1);
                height = Math.Max(height >> 1, 1);
            }
            return count;
        }
    }
}
