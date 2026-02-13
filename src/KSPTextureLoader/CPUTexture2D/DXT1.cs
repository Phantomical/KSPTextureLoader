using System;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    public readonly struct DXT1 : ICPUTexture2D
    {
        const int BytesPerBlock = 8;

        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.DXT1;

        readonly NativeArray<byte> data;

        public DXT1(NativeArray<byte> data, int width, int height, int mipCount)
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

            ushort c0Raw = (ushort)(data[blockOffset] | (data[blockOffset + 1] << 8));
            ushort c1Raw = (ushort)(data[blockOffset + 2] | (data[blockOffset + 3] << 8));

            float r0 = ((c0Raw >> 11) & 0x1F) * (1f / 31f);
            float g0 = ((c0Raw >> 5) & 0x3F) * (1f / 63f);
            float b0 = (c0Raw & 0x1F) * (1f / 31f);

            float r1 = ((c1Raw >> 11) & 0x1F) * (1f / 31f);
            float g1 = ((c1Raw >> 5) & 0x3F) * (1f / 63f);
            float b1 = (c1Raw & 0x1F) * (1f / 31f);

            byte indexByte = data[blockOffset + 4 + localY];
            int index = (indexByte >> (localX * 2)) & 0x3;

            if (c0Raw > c1Raw)
            {
                return index switch
                {
                    0 => new Color(r0, g0, b0, 1f),
                    1 => new Color(r1, g1, b1, 1f),
                    2 => new Color(
                        (2f * r0 + r1) * (1f / 3f),
                        (2f * g0 + g1) * (1f / 3f),
                        (2f * b0 + b1) * (1f / 3f),
                        1f
                    ),
                    _ => new Color(
                        (r0 + 2f * r1) * (1f / 3f),
                        (g0 + 2f * g1) * (1f / 3f),
                        (b0 + 2f * b1) * (1f / 3f),
                        1f
                    ),
                };
            }
            else
            {
                return index switch
                {
                    0 => new Color(r0, g0, b0, 1f),
                    1 => new Color(r1, g1, b1, 1f),
                    2 => new Color((r0 + r1) * 0.5f, (g0 + g1) * 0.5f, (b0 + b1) * 0.5f, 1f),
                    _ => new Color(0f, 0f, 0f, 0f),
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
