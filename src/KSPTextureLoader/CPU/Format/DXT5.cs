using System;
using KSPTextureLoader.Burst;
using KSPTextureLoader.Utils;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    [BurstCompile(FloatMode = FloatMode.Fast)]
    public readonly struct DXT5 : ICPUTexture2D, IGetPixels
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

        readonly LargeNativeArray<Block> data;

        internal unsafe DXT5(LargeNativeArray<byte> data, int width, int height, int mipCount)
        {
            this.data = data.Reinterpret<Block>();
            this.Width = width;
            this.Height = height;
            this.MipCount = mipCount;

            int expected = GetTotalBlockCount(width, height, mipCount) * sizeof(Block);
            if (expected != data.Length)
                throw new Exception(
                    $"data size did not match expected texture size (expected {expected}, but got {data.Length} instead)"
                );
        }

        public DXT5(NativeArray<byte> data, int width, int height, int mipCount)
            : this((LargeNativeArray<byte>)data, width, height, mipCount) { }

        public Color GetPixel(int x, int y, int mipLevel = 0)
        {
            if (CPU.BurstForward.ShouldForward)
            {
                CPU.BurstForward.Dxt5GetPixel(in this, x, y, mipLevel, out Color result);
                return result;
            }
            return GetPixelCore(x, y, mipLevel);
        }

        internal Color GetPixelCore(int x, int y, int mipLevel)
        {
            GetBlockIndex(Width, Height, x, y, mipLevel, out int blockIndex, out int pixelIndex);
            Block block = data[blockIndex];
            float alpha = CPU.Block.BC4.DecodePixel(block.alpha, pixelIndex);
            Color rgb = CPU.Block.DXT1.DecodePixel(block.color, pixelIndex);
            return new Color(rgb.r, rgb.g, rgb.b, alpha);
        }

        public Color32 GetPixel32(int x, int y, int mipLevel = 0) => GetPixel(x, y, mipLevel);

        public Color GetPixelBilinear(float u, float v, int mipLevel = 0) =>
            CPUTexture2D.GetPixelBilinear(in this, u, v, mipLevel);

        public NativeArray<T> GetRawTextureData<T>()
            where T : unmanaged
        {
            return data.Reinterpret<T>().AsNativeArray();
        }

        public NativeArray<Color> GetPixels(int mipLevel = 0, Allocator allocator = Allocator.Temp)
        {
            return GetBlockPixels(
                in this,
                mipLevel,
                allocator,
                (NativeArray<Block> data) => new GetPixelsJob { blocks = data }
            );
        }

        public NativeArray<Color32> GetPixels32(
            int mipLevel = 0,
            Allocator allocator = Allocator.Temp
        )
        {
            return GetBlockPixels32(
                in this,
                mipLevel,
                allocator,
                (NativeArray<Block> data) => new GetPixelsJob { blocks = data }
            );
        }

        [BurstCompile(FloatMode = FloatMode.Fast)]
        struct GetPixelsJob : IGetPixelsBlockJob
        {
            [ReadOnly]
            public NativeArray<Block> blocks;

            public FixedArray16<Color> DecodeBlock(int blockIdx)
            {
                var block = blocks[blockIdx];
                var colors = CPU.Block.DXT1.DecodeBlock(block.color);
                var alphas = CPU.Block.BC4.DecodeBlock(block.alpha);

                for (int i = 0; i < 16; ++i)
                    colors[i].a = alphas[i];

                return colors;
            }
        }
    }
}
