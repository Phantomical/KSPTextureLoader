using System;
using KSPTextureLoader.Burst;
using KSPTextureLoader.Utils;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    public readonly struct BC4 : ICPUTexture2D, IGetPixels
    {
        struct Block(ulong bits)
        {
            public ulong bits = bits;
        }

        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.BC4;

        readonly LargeNativeArray<Block> data;

        internal unsafe BC4(LargeNativeArray<byte> data, int width, int height, int mipCount)
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

        public BC4(NativeArray<byte> data, int width, int height, int mipCount)
            : this((LargeNativeArray<byte>)data, width, height, mipCount) { }

        public Color GetPixel(int x, int y, int mipLevel = 0)
        {
            GetBlockIndex(Width, Height, x, y, mipLevel, out int blockIndex, out int pixelIndex);
            float red = DecodeBC4Channel(data[blockIndex].bits, pixelIndex);
            return new Color(red, 0f, 0f, 0f);
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
                var values = DecodeBC4Block(blocks[blockIdx].bits);

                FixedArray16<Color> colors = default;
                for (int i = 0; i < values.Length; ++i)
                    colors[i] = new(values[i], 0f, 0f, 0f);

                return colors;
            }
        }
    }
}
