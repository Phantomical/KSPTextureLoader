using System;
using KSPTextureLoader.Burst;
using KSPTextureLoader.Utils;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    public readonly struct DXT1 : ICPUTexture2D, IGetPixels
    {
        struct Block(ulong bits)
        {
            public ulong bits = bits;
        }

        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.DXT1;

        readonly LargeNativeArray<Block> data;

        internal unsafe DXT1(LargeNativeArray<byte> data, int width, int height, int mipCount)
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

        public DXT1(NativeArray<byte> data, int width, int height, int mipCount)
            : this((LargeNativeArray<byte>)data, width, height, mipCount) { }

        public Color GetPixel(int x, int y, int mipLevel = 0)
        {
            if (CPU.BurstForward.ShouldForward)
            {
                CPU.BurstForward.Dxt1GetPixel(in this, x, y, mipLevel, out Color result);
                return result;
            }
            return GetPixelCore(x, y, mipLevel);
        }

        internal Color GetPixelCore(int x, int y, int mipLevel)
        {
            GetBlockIndex(Width, Height, x, y, mipLevel, out int blockIndex, out int pixelIndex);
            return CPU.Block.DXT1.DecodePixel(data[blockIndex].bits, pixelIndex);
        }

        public Color32 GetPixel32(int x, int y, int mipLevel = 0)
        {
            if (CPU.BurstForward.ShouldForward)
            {
                CPU.BurstForward.Dxt1GetPixel32(in this, x, y, mipLevel, out Color32 result);
                return result;
            }
            return GetPixel32Core(x, y, mipLevel);
        }

        internal Color32 GetPixel32Core(int x, int y, int mipLevel)
        {
            GetBlockIndex(Width, Height, x, y, mipLevel, out int blockIndex, out int pixelIndex);
            return CPU.Block.DXT1.DecodePixel32(data[blockIndex].bits, pixelIndex);
        }

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
                return CPU.Block.DXT1.DecodeBlock(blocks[blockIdx].bits);
            }
        }
    }
}
