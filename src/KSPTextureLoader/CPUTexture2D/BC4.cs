using System;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    public readonly struct BC4 : ICPUTexture2D
    {
        const int BytesPerBlock = 8;

        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.BC4;

        readonly NativeArray<byte> data;

        public BC4(NativeArray<byte> data, int width, int height, int mipCount)
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

            byte r0 = data[blockOffset];
            byte r1 = data[blockOffset + 1];

            // 48-bit index data in bytes [blockOffset+2..blockOffset+7]
            // Each pixel has a 3-bit index
            int pixelIndex = localY * 4 + localX;
            int bitOffset = pixelIndex * 3;
            int byteIndex = blockOffset + 2 + bitOffset / 8;
            int bitShift = bitOffset % 8;

            // Read 2 bytes to extract the 3-bit index (may span a byte boundary)
            int raw = data[byteIndex] | (data[byteIndex + 1] << 8);
            int code = (raw >> bitShift) & 0x7;

            float fr0 = r0 * (1f / 255f);
            float fr1 = r1 * (1f / 255f);

            float red;
            if (r0 > r1)
            {
                red = code switch
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
                red = code switch
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

            return new Color(red, 0f, 0f, 0f);
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
