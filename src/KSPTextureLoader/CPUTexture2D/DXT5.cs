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
