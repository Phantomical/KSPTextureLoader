using System;
using KSPTextureLoader.Burst;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
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

        public NativeArray<Color> GetPixels(int mipLevel = 0, Allocator allocator = Allocator.Temp)
        {
            GetBlockMipProperties(
                Width,
                Height,
                mipLevel,
                out int mipWidth,
                out int mipHeight,
                out int blockOffset,
                out int blocksPerRow,
                out int blockCount
            );

            var pixels = new NativeArray<Color>(
                mipWidth * mipHeight,
                allocator,
                NativeArrayOptions.UninitializedMemory
            );

            // BC5 blocks are 16 bytes each = 2 ulongs per block
            var blocks = GetRawTextureData<ulong>().GetSubArray(blockOffset * 2, blockCount * 2);
            var job = new GetPixelsJob
            {
                blocks = blocks,
                pixels = pixels,
                blocksPerRow = blocksPerRow,
                width = mipWidth,
                height = mipHeight,
            };

            if (blockCount < 1024)
                job.RunBatch(blockCount, 256);
            else
                job.ScheduleBatch(blockCount, 256).Complete();

            return pixels;
        }

        public NativeArray<Color32> GetPixels32(
            int mipLevel = 0,
            Allocator allocator = Allocator.Temp
        )
        {
            GetBlockMipProperties(
                Width,
                Height,
                mipLevel,
                out int mipWidth,
                out int mipHeight,
                out int blockOffset,
                out int blocksPerRow,
                out int blockCount
            );

            var pixels = new NativeArray<Color32>(
                mipWidth * mipHeight,
                allocator,
                NativeArrayOptions.UninitializedMemory
            );

            var blocks = GetRawTextureData<ulong>().GetSubArray(blockOffset * 2, blockCount * 2);
            var job = new GetPixels32Job
            {
                blocks = blocks,
                pixels = pixels,
                blocksPerRow = blocksPerRow,
                width = mipWidth,
                height = mipHeight,
            };

            if (blockCount < 1024)
                job.RunBatch(blockCount, 256);
            else
                job.ScheduleBatch(blockCount, 256).Complete();

            return pixels;
        }

        [BurstCompile]
        struct GetPixelsJob : IJobParallelForBatch
        {
            [ReadOnly]
            public NativeArray<ulong> blocks;

            [WriteOnly]
            [NativeDisableParallelForRestriction]
            public NativeArray<Color> pixels;

            public int blocksPerRow;
            public int width;
            public int height;

            public unsafe void Execute(int start, int count)
            {
                float* decodedR = stackalloc float[16];
                float* decodedG = stackalloc float[16];
                int end = start + count;

                for (int blockIdx = start; blockIdx < end; blockIdx++)
                {
                    DecodeBC4Block(blocks[blockIdx * 2], decodedR);
                    DecodeBC4Block(blocks[blockIdx * 2 + 1], decodedG);

                    int blockX = blockIdx % blocksPerRow;
                    int blockY = blockIdx / blocksPerRow;
                    int baseX = blockX * 4;
                    int baseY = blockY * 4;

                    for (int row = 0; row < 4; row++)
                    {
                        int py = baseY + row;
                        if (py >= height)
                            break;

                        for (int col = 0; col < 4; col++)
                        {
                            int px = baseX + col;
                            if (px >= width)
                                break;

                            int i = row * 4 + col;
                            pixels[py * width + px] = new Color(decodedR[i], decodedG[i], 0f, 0f);
                        }
                    }
                }
            }
        }

        [BurstCompile]
        struct GetPixels32Job : IJobParallelForBatch
        {
            [ReadOnly]
            public NativeArray<ulong> blocks;

            [WriteOnly]
            [NativeDisableParallelForRestriction]
            public NativeArray<Color32> pixels;

            public int blocksPerRow;
            public int width;
            public int height;

            public unsafe void Execute(int start, int count)
            {
                float* decodedR = stackalloc float[16];
                float* decodedG = stackalloc float[16];
                int end = start + count;

                for (int blockIdx = start; blockIdx < end; blockIdx++)
                {
                    DecodeBC4Block(blocks[blockIdx * 2], decodedR);
                    DecodeBC4Block(blocks[blockIdx * 2 + 1], decodedG);

                    int blockX = blockIdx % blocksPerRow;
                    int blockY = blockIdx / blocksPerRow;
                    int baseX = blockX * 4;
                    int baseY = blockY * 4;

                    for (int row = 0; row < 4; row++)
                    {
                        int py = baseY + row;
                        if (py >= height)
                            break;

                        for (int col = 0; col < 4; col++)
                        {
                            int px = baseX + col;
                            if (px >= width)
                                break;

                            int i = row * 4 + col;
                            pixels[py * width + px] = new Color32(
                                (byte)(decodedR[i] * Float2Byte),
                                (byte)(decodedG[i] * Float2Byte),
                                0,
                                0
                            );
                        }
                    }
                }
            }
        }
    }
}
