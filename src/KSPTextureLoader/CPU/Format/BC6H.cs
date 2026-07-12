using System;
using KSPTextureLoader.Burst;
using KSPTextureLoader.Utils;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    public readonly struct BC6H : ICPUTexture2D, IGetPixels
    {
        struct Block(ulong lo, ulong hi)
        {
            public ulong lo = lo;
            public ulong hi = hi;
        }

        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.BC6H;

        readonly LargeNativeArray<Block> data;
        readonly bool signed;

        internal unsafe BC6H(
            LargeNativeArray<byte> data,
            int width,
            int height,
            int mipCount,
            bool signed = false
        )
        {
            this.data = data.Reinterpret<Block>();
            this.Width = width;
            this.Height = height;
            this.MipCount = mipCount;
            this.signed = signed;

            int expected = GetTotalBlockCount(width, height, mipCount) * sizeof(Block);
            if (expected != data.Length)
                throw new Exception(
                    $"data size did not match expected texture size (expected {expected}, but got {data.Length} instead)"
                );
        }

        public BC6H(
            NativeArray<byte> data,
            int width,
            int height,
            int mipCount,
            bool signed = false
        )
            : this((LargeNativeArray<byte>)data, width, height, mipCount, signed) { }

        public Color GetPixel(int x, int y, int mipLevel = 0)
        {
            if (CPU.BurstForward.ShouldForward)
            {
                CPU.BurstForward.Bc6hGetPixel(in this, x, y, mipLevel, out Color result);
                return result;
            }
            return GetPixelCore(x, y, mipLevel);
        }

        internal Color GetPixelCore(int x, int y, int mipLevel)
        {
            GetBlockIndex(Width, Height, x, y, mipLevel, out int blockIndex, out int pixelIndex);
            Block block = data[blockIndex];
            return CPU.Block.BC6H.DecodePixel(block.lo, block.hi, pixelIndex, signed);
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
            bool s = signed;
            return GetBlockPixels(
                in this,
                mipLevel,
                allocator,
                (NativeArray<Block> data) => new GetPixelsJob { blocks = data, signed = s }
            );
        }

        public NativeArray<Color32> GetPixels32(
            int mipLevel = 0,
            Allocator allocator = Allocator.Temp
        )
        {
            bool s = signed;
            return GetBlockPixels32(
                in this,
                mipLevel,
                allocator,
                (NativeArray<Block> data) => new GetPixelsJob { blocks = data, signed = s }
            );
        }

        [BurstCompile(FloatMode = FloatMode.Fast)]
        struct GetPixelsJob : IGetPixelsBlockJob
        {
            [ReadOnly]
            public NativeArray<Block> blocks;

            public bool signed;

            public FixedArray16<Color> DecodeBlock(int blockIdx)
            {
                var block = blocks[blockIdx];
                return CPU.Block.BC6H.DecodeBlock(block.lo, block.hi, signed);
            }
        }
    }
}
