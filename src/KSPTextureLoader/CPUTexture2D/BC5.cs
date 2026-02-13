using System;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    public readonly struct BC5 : ICPUTexture2D
    {
        const int BytesPerBlock = 16;

        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.BC5;

        readonly NativeArray<byte> data;

        public BC5(NativeArray<byte> data, int width, int height, int mipCount)
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

            // Decode red from first BC4 block (bytes 0-7)
            float red = DecodeBC4(blockOffset, localX, localY);

            // Decode green from second BC4 block (bytes 8-15)
            float green = DecodeBC4(blockOffset + 8, localX, localY);

            return new Color(red, green, 0f, 0f);
        }

        float DecodeBC4(int offset, int localX, int localY)
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
