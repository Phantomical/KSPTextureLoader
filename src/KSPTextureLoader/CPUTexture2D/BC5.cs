using System;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    public readonly struct BC5 : ICPUTexture2D
    {
        struct Block(ulong red, ulong green)
        {
            public ulong red = red;
            public ulong green = green;
        }

        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.BC5;

        readonly NativeArray<Block> data;

        public unsafe BC5(NativeArray<byte> data, int width, int height, int mipCount)
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
            float red = DecodeBC4Channel(block.red, pixelIndex);
            float green = DecodeBC4Channel(block.green, pixelIndex);
            return new Color(red, green, 0f, 0f);
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
