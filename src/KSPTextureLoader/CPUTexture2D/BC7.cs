using System;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    public readonly struct BC7 : ICPUTexture2D
    {
        struct Block(ulong lo, ulong hi)
        {
            public ulong lo = lo;
            public ulong hi = hi;
        }

        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.BC7;

        readonly NativeArray<Block> data;

        public unsafe BC7(NativeArray<byte> data, int width, int height, int mipCount)
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

        public Color GetPixel(int x, int y, int mipLevel = 0) => GetPixel32(x, y, mipLevel);

        public Color32 GetPixel32(int x, int y, int mipLevel = 0)
        {
            int mipWidth = Width;
            int mipHeight = Height;
            int blockOffset = 0;

            for (int m = 0; m < mipLevel; m++)
            {
                int bw = (mipWidth + 3) / 4;
                int bh = (mipHeight + 3) / 4;
                blockOffset += bw * bh;
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

            int blockIndex = blockOffset + blockY * blocksPerRow + blockX;

            DecodeBC7Pixel(
                data[blockIndex],
                localX,
                localY,
                out byte r,
                out byte g,
                out byte b,
                out byte a
            );
            return new Color32(r, g, b, a);
        }

        public Color GetPixelBilinear(float u, float v, int mipLevel = 0) =>
            CPUTexture2D.GetPixelBilinear(in this, u, v, mipLevel);

        public unsafe NativeArray<T> GetRawTextureData<T>()
            where T : unmanaged
        {
            return GetNonOwningNativeArray(data).Reinterpret<T>(sizeof(Block));
        }

        static int GetTotalBlockCount(int width, int height, int mipCount)
        {
            int count = 0;
            for (int m = 0; m < mipCount; m++)
            {
                int bw = (width + 3) / 4;
                int bh = (height + 3) / 4;
                count += bw * bh;
                width = Math.Max(width >> 1, 1);
                height = Math.Max(height >> 1, 1);
            }
            return count;
        }

        // ================================================================
        // BC7 Decoder
        // ================================================================

        struct BitReader
        {
            Block block;
            int bitPos;

            public BitReader(Block block)
            {
                this.block = block;
                bitPos = 0;
            }

            public int ReadBits(int count)
            {
                int result;
                int bitIdx = bitPos & 63;
                ulong mask = (1ul << count) - 1;

                if (bitPos < 64)
                {
                    result = (int)((block.lo >> bitIdx) & mask);
                    // If the read spans the lo/hi boundary, grab remaining bits from hi
                    if (bitIdx + count > 64)
                    {
                        int loBits = 64 - bitIdx;
                        result |= (int)(block.hi & ((1ul << (count - loBits)) - 1)) << loBits;
                    }
                }
                else
                {
                    result = (int)((block.hi >> bitIdx) & mask);
                }

                bitPos += count;
                return result;
            }
        }

        // csharpier-ignore-start

        // 2-subset partition table: 64 partitions x 16 pixels
        static readonly byte[] PartitionTable2 =
        [
            0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,
            0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,
            0,1,1,1,0,1,1,1,0,1,1,1,0,1,1,1,
            0,0,0,1,0,0,1,1,0,0,1,1,0,1,1,1,
            0,0,0,0,0,0,0,1,0,0,0,1,0,0,1,1,
            0,0,1,1,0,1,1,1,0,1,1,1,1,1,1,1,
            0,0,0,1,0,0,1,1,0,1,1,1,1,1,1,1,
            0,0,0,0,0,0,0,1,0,0,1,1,0,1,1,1,
            0,0,0,0,0,0,0,0,0,0,0,1,0,0,1,1,
            0,0,1,1,0,1,1,1,1,1,1,1,1,1,1,1,
            0,0,0,0,0,0,0,1,0,1,1,1,1,1,1,1,
            0,0,0,0,0,0,0,0,0,0,0,1,0,1,1,1,
            0,0,0,1,0,1,1,1,1,1,1,1,1,1,1,1,
            0,0,0,0,0,0,0,0,1,1,1,1,1,1,1,1,
            0,0,0,0,1,1,1,1,1,1,1,1,1,1,1,1,
            0,0,0,0,0,0,0,0,0,0,0,0,1,1,1,1,
            0,0,0,0,1,0,0,0,1,1,1,0,1,1,1,1,
            0,1,1,1,0,0,0,1,0,0,0,0,0,0,0,0,
            0,0,0,0,0,0,0,0,1,0,0,0,1,1,1,0,
            0,1,1,1,0,0,1,1,0,0,0,1,0,0,0,0,
            0,0,1,1,0,0,0,1,0,0,0,0,0,0,0,0,
            0,0,0,0,1,0,0,0,1,1,0,0,1,1,1,0,
            0,0,0,0,0,0,0,0,1,0,0,0,1,1,0,0,
            0,1,1,1,0,0,1,1,0,0,1,1,0,0,0,1,
            0,0,1,1,0,0,0,1,0,0,0,1,0,0,0,0,
            0,0,0,0,1,0,0,0,1,0,0,0,1,1,0,0,
            0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,0,
            0,0,1,1,0,1,1,0,0,1,1,0,1,1,0,0,
            0,0,0,1,0,1,1,1,1,1,1,0,1,0,0,0,
            0,0,0,0,1,1,1,1,1,1,1,1,0,0,0,0,
            0,1,1,1,0,0,0,1,1,0,0,0,1,1,1,0,
            0,0,1,1,1,0,0,1,1,0,0,1,1,1,0,0,
            0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,
            0,0,0,0,1,1,1,1,0,0,0,0,1,1,1,1,
            0,1,0,1,1,0,1,0,0,1,0,1,1,0,1,0,
            0,0,1,1,0,0,1,1,1,1,0,0,1,1,0,0,
            0,0,1,1,1,1,0,0,0,0,1,1,1,1,0,0,
            0,1,0,1,0,1,0,1,1,0,1,0,1,0,1,0,
            0,1,1,0,1,0,0,1,0,1,1,0,1,0,0,1,
            0,1,0,1,1,0,1,0,1,0,1,0,0,1,0,1,
            0,1,1,1,0,0,1,1,1,1,0,0,1,1,1,0,
            0,0,0,1,0,0,1,1,1,1,0,0,1,0,0,0,
            0,0,1,1,0,0,1,0,0,1,0,0,1,1,0,0,
            0,0,1,1,1,0,1,1,1,1,0,1,1,1,0,0,
            0,1,1,0,1,0,0,1,1,0,0,1,0,1,1,0,
            0,0,1,1,1,1,0,0,1,1,0,0,0,0,1,1,
            0,1,1,0,0,1,1,0,1,0,0,1,1,0,0,1,
            0,0,0,0,0,1,1,0,0,1,1,0,0,0,0,0,
            0,1,0,0,1,1,1,0,0,1,0,0,0,0,0,0,
            0,0,1,0,0,1,1,1,0,0,1,0,0,0,0,0,
            0,0,0,0,0,0,1,0,0,1,1,1,0,0,1,0,
            0,0,0,0,0,1,0,0,1,1,1,0,0,1,0,0,
            0,1,1,0,1,1,0,0,1,0,0,1,0,0,1,1,
            0,0,1,1,0,1,1,0,1,1,0,0,1,0,0,1,
            0,1,1,0,0,0,1,1,1,0,0,1,1,1,0,0,
            0,0,1,1,1,0,0,1,1,1,0,0,0,1,1,0,
            0,1,1,0,1,1,0,0,1,1,0,0,1,0,0,1,
            0,1,1,0,0,0,1,1,0,0,1,1,1,0,0,1,
            0,1,1,1,1,1,1,0,1,0,0,0,0,0,0,1,
            0,0,0,1,1,0,0,0,1,1,1,0,0,1,1,1,
            0,0,0,0,1,1,1,1,0,0,1,1,0,0,1,1,
            0,0,1,1,0,0,1,1,1,1,1,1,0,0,0,0,
            0,0,1,0,0,0,1,0,1,1,1,0,1,1,1,0,
            0,1,0,0,0,1,0,0,0,1,1,1,0,1,1,1,
        ];

        // 3-subset partition table: 64 partitions x 16 pixels
        static readonly byte[] PartitionTable3 =
        [
            0,0,1,1,0,0,1,1,0,2,2,1,2,2,2,2,
            0,0,0,1,0,0,1,1,2,2,1,1,2,2,2,1,
            0,0,0,0,2,0,0,1,2,2,1,1,2,2,1,1,
            0,2,2,2,0,0,2,2,0,0,1,1,0,1,1,1,
            0,0,0,0,0,0,0,0,1,1,2,2,1,1,2,2,
            0,0,1,1,0,0,1,1,0,0,2,2,0,0,2,2,
            0,0,2,2,0,0,2,2,1,1,1,1,1,1,1,1,
            0,0,1,1,0,0,1,1,2,2,1,1,2,2,1,1,
            0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,
            0,0,0,0,1,1,1,1,1,1,1,1,2,2,2,2,
            0,0,0,0,1,1,1,1,2,2,2,2,2,2,2,2,
            0,0,1,2,0,0,1,2,0,0,1,2,0,0,1,2,
            0,1,1,2,0,1,1,2,0,1,1,2,0,1,1,2,
            0,1,2,2,0,1,2,2,0,1,2,2,0,1,2,2,
            0,0,1,1,0,1,1,2,1,1,2,2,1,2,2,2,
            0,0,1,1,2,0,0,1,2,2,0,0,2,2,2,0,
            0,0,0,1,0,0,1,1,0,1,1,2,1,1,2,2,
            0,1,1,1,0,0,1,1,2,0,0,1,2,2,0,0,
            0,0,0,0,1,1,2,2,1,1,2,2,1,1,2,2,
            0,0,2,2,0,0,2,2,0,0,2,2,1,1,1,1,
            0,1,1,1,0,1,1,1,0,2,2,2,0,2,2,2,
            0,0,0,1,0,0,0,1,2,2,2,1,2,2,2,1,
            0,0,0,0,0,0,1,1,0,1,2,2,0,1,2,2,
            0,0,0,0,1,1,0,0,2,2,1,0,2,2,1,0,
            0,1,2,2,0,1,2,2,0,0,1,1,0,0,0,0,
            0,0,1,2,0,0,1,2,1,1,2,2,2,2,2,2,
            0,1,1,0,1,2,2,1,1,2,2,1,0,1,1,0,
            0,0,0,0,0,1,1,0,1,2,2,1,1,2,2,1,
            0,0,2,2,1,1,0,2,1,1,0,2,0,0,2,2,
            0,1,1,0,0,1,1,0,2,0,0,2,2,2,2,2,
            0,0,1,1,0,1,2,2,0,1,2,2,0,0,1,1,
            0,0,0,0,2,0,0,0,2,2,1,1,2,2,2,1,
            0,0,0,0,0,0,0,2,1,1,2,2,1,2,2,2,
            0,2,2,2,0,0,2,2,0,0,1,2,0,0,1,1,
            0,0,1,1,0,0,1,2,0,0,2,2,0,2,2,2,
            0,1,2,0,0,1,2,0,0,1,2,0,0,1,2,0,
            0,0,0,0,1,1,1,1,2,2,2,2,0,0,0,0,
            0,1,2,0,1,2,0,1,2,0,1,2,0,1,2,0,
            0,1,2,0,2,0,1,2,1,2,0,1,0,1,2,0,
            0,0,1,1,2,2,0,0,1,1,2,2,0,0,1,1,
            0,0,1,1,1,1,2,2,2,2,0,0,0,0,1,1,
            0,1,0,1,0,1,0,1,2,2,2,2,2,2,2,2,
            0,0,0,0,0,0,0,0,2,1,2,1,2,1,2,1,
            0,0,2,2,1,1,2,2,0,0,2,2,1,1,2,2,
            0,0,2,2,0,0,1,1,0,0,2,2,0,0,1,1,
            0,2,2,0,1,2,2,1,0,2,2,0,1,2,2,1,
            0,1,0,1,2,2,2,2,2,2,2,2,0,1,0,1,
            0,0,0,0,2,1,2,1,2,1,2,1,2,1,2,1,
            0,1,0,1,0,1,0,1,0,1,0,1,2,2,2,2,
            0,2,2,2,0,1,1,1,0,2,2,2,0,1,1,1,
            0,0,0,2,1,1,1,2,0,0,0,2,1,1,1,2,
            0,0,0,0,2,1,1,2,2,1,1,2,2,1,1,2,
            0,2,2,2,0,1,1,1,0,1,1,1,0,2,2,2,
            0,0,0,2,1,1,1,2,1,1,1,2,0,0,0,2,
            0,1,1,0,0,1,1,0,0,1,1,0,2,2,2,2,
            0,0,0,0,0,0,0,0,2,1,1,2,2,1,1,2,
            0,1,1,0,0,1,1,0,2,2,2,2,2,2,2,2,
            0,0,2,2,0,0,1,1,0,0,1,1,0,0,2,2,
            0,0,2,2,1,1,2,2,1,1,2,2,0,0,2,2,
            0,0,0,0,0,0,0,0,0,0,0,0,2,1,1,2,
            0,0,0,2,0,0,0,1,0,0,0,2,0,0,0,1,
            0,2,2,2,1,2,2,2,0,2,2,2,1,2,2,2,
            0,1,0,1,2,2,2,2,2,2,2,2,2,2,2,2,
            0,1,1,1,2,0,1,1,2,2,0,1,2,2,2,0,
        ];

        // Anchor indices for 2-subset partitions (second subset anchor)
        static readonly byte[] AnchorIndex2_1 =
        [
            15,15,15,15,15,15,15,15,
            15,15,15,15,15,15,15,15,
            15, 2, 8, 2, 2, 8, 8,15,
             2, 8, 2, 2, 8, 8, 2, 2,
            15,15, 6, 8, 2, 8,15,15,
             2, 8, 2, 2, 2,15,15, 6,
             6, 2, 6, 8,15,15, 2, 2,
            15,15,15,15,15, 2, 2,15,
        ];

        // Anchor indices for 3-subset partitions (second subset)
        static readonly byte[] AnchorIndex3_1 =
        [
             3, 3,15,15, 8, 3,15,15,
             8, 8, 6, 6, 6, 5, 3, 3,
             3, 3, 8,15, 3, 3, 6,10,
             5, 8, 8, 6, 8, 5,15,15,
             8,15, 3, 5, 6,10, 8,15,
            15, 3,15, 5,15,15,15,15,
             3,15, 5, 5, 5, 8, 5,10,
             5,10, 8,13,15,12, 3, 3,
        ];

        // Anchor indices for 3-subset partitions (third subset)
        static readonly byte[] AnchorIndex3_2 =
        [
            15, 8, 8, 3,15,15, 3, 8,
            15,15,15,15,15,15,15, 8,
            15, 8,15, 3,15, 8,15, 8,
             3,15, 6,10,15,15,10, 8,
            15, 3,15,10,10, 8, 9,10,
             6,15, 8,15, 3, 6, 6, 8,
            15, 3,15,15,15,15,15,15,
            15,15,15,15, 3,15,15, 8,
        ];

        static readonly byte[] Weights2 = [0, 21, 43, 64];
        static readonly byte[] Weights3 = [0, 9, 18, 27, 37, 46, 55, 64];
        static readonly byte[] Weights4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];
        // csharpier-ignore-end

        static int BC7Interpolate(int e0, int e1, int weight)
        {
            return (e0 * (64 - weight) + e1 * weight + 32) >> 6;
        }

        static int BC7Unquantize(int val, int bits)
        {
            if (bits >= 8)
                return val;
            val <<= 8 - bits;
            return val | (val >> bits);
        }

        static void ApplyRotation(int rotation, ref int r, ref int g, ref int b, ref int a)
        {
            switch (rotation)
            {
                case 1:
                    (a, r) = (r, a);
                    break;
                case 2:
                    (a, g) = (g, a);
                    break;
                case 3:
                    (a, b) = (b, a);
                    break;
            }
        }

        static void DecodeBC7Pixel(
            Block block,
            int localX,
            int localY,
            out byte r,
            out byte g,
            out byte b,
            out byte a
        )
        {
            var reader = new BitReader(block);

            // Determine mode (0-7) from leading bits
            int mode = 0;
            while (mode < 8 && reader.ReadBits(1) == 0)
                mode++;

            if (mode >= 8)
            {
                r = g = b = a = 0;
                return;
            }

            int pixelIndex = localY * 4 + localX;

            switch (mode)
            {
                case 0:
                    DecodeMode0(ref reader, pixelIndex, out r, out g, out b, out a);
                    break;
                case 1:
                    DecodeMode1(ref reader, pixelIndex, out r, out g, out b, out a);
                    break;
                case 2:
                    DecodeMode2(ref reader, pixelIndex, out r, out g, out b, out a);
                    break;
                case 3:
                    DecodeMode3(ref reader, pixelIndex, out r, out g, out b, out a);
                    break;
                case 4:
                    DecodeMode4(ref reader, pixelIndex, out r, out g, out b, out a);
                    break;
                case 5:
                    DecodeMode5(ref reader, pixelIndex, out r, out g, out b, out a);
                    break;
                case 6:
                    DecodeMode6(ref reader, pixelIndex, out r, out g, out b, out a);
                    break;
                default:
                    DecodeMode7(ref reader, pixelIndex, out r, out g, out b, out a);
                    break;
            }
        }

        // Mode 0: 3 subsets, 4-bit endpoints (RGB), 1 unique pbit per endpoint, 3-bit indices
        static void DecodeMode0(
            ref BitReader reader,
            int pixelIndex,
            out byte r,
            out byte g,
            out byte b,
            out byte a
        )
        {
            int partition = reader.ReadBits(4);

            // 3 subsets x 2 endpoints x 4 bits = 24 bits per channel
            // R endpoints: s0e0, s0e1, s1e0, s1e1, s2e0, s2e1
            int rS0E0 = reader.ReadBits(4),
                rS0E1 = reader.ReadBits(4);
            int rS1E0 = reader.ReadBits(4),
                rS1E1 = reader.ReadBits(4);
            int rS2E0 = reader.ReadBits(4),
                rS2E1 = reader.ReadBits(4);
            // G endpoints
            int gS0E0 = reader.ReadBits(4),
                gS0E1 = reader.ReadBits(4);
            int gS1E0 = reader.ReadBits(4),
                gS1E1 = reader.ReadBits(4);
            int gS2E0 = reader.ReadBits(4),
                gS2E1 = reader.ReadBits(4);
            // B endpoints
            int bS0E0 = reader.ReadBits(4),
                bS0E1 = reader.ReadBits(4);
            int bS1E0 = reader.ReadBits(4),
                bS1E1 = reader.ReadBits(4);
            int bS2E0 = reader.ReadBits(4),
                bS2E1 = reader.ReadBits(4);

            // 6 unique p-bits (one per endpoint)
            int pb0 = reader.ReadBits(1),
                pb1 = reader.ReadBits(1);
            int pb2 = reader.ReadBits(1),
                pb3 = reader.ReadBits(1);
            int pb4 = reader.ReadBits(1),
                pb5 = reader.ReadBits(1);

            // Apply p-bits to all channels
            rS0E0 = (rS0E0 << 1) | pb0;
            rS0E1 = (rS0E1 << 1) | pb1;
            rS1E0 = (rS1E0 << 1) | pb2;
            rS1E1 = (rS1E1 << 1) | pb3;
            rS2E0 = (rS2E0 << 1) | pb4;
            rS2E1 = (rS2E1 << 1) | pb5;
            gS0E0 = (gS0E0 << 1) | pb0;
            gS0E1 = (gS0E1 << 1) | pb1;
            gS1E0 = (gS1E0 << 1) | pb2;
            gS1E1 = (gS1E1 << 1) | pb3;
            gS2E0 = (gS2E0 << 1) | pb4;
            gS2E1 = (gS2E1 << 1) | pb5;
            bS0E0 = (bS0E0 << 1) | pb0;
            bS0E1 = (bS0E1 << 1) | pb1;
            bS1E0 = (bS1E0 << 1) | pb2;
            bS1E1 = (bS1E1 << 1) | pb3;
            bS2E0 = (bS2E0 << 1) | pb4;
            bS2E1 = (bS2E1 << 1) | pb5;

            // Read 3-bit indices (anchors lose 1 bit)
            int subset = PartitionTable3[partition * 16 + pixelIndex];
            int anchor0 = 0;
            int anchor1 = AnchorIndex3_1[partition];
            int anchor2 = AnchorIndex3_2[partition];

            int idx = 0;
            for (int i = 0; i < 16; i++)
            {
                int bits = (i == anchor0 || i == anchor1 || i == anchor2) ? 2 : 3;
                int val = reader.ReadBits(bits);
                if (i == pixelIndex)
                    idx = val;
            }

            // Select endpoints by subset
            int er0,
                er1,
                eg0,
                eg1,
                eb0,
                eb1;
            switch (subset)
            {
                case 0:
                    er0 = rS0E0;
                    er1 = rS0E1;
                    eg0 = gS0E0;
                    eg1 = gS0E1;
                    eb0 = bS0E0;
                    eb1 = bS0E1;
                    break;
                case 1:
                    er0 = rS1E0;
                    er1 = rS1E1;
                    eg0 = gS1E0;
                    eg1 = gS1E1;
                    eb0 = bS1E0;
                    eb1 = bS1E1;
                    break;
                default:
                    er0 = rS2E0;
                    er1 = rS2E1;
                    eg0 = gS2E0;
                    eg1 = gS2E1;
                    eb0 = bS2E0;
                    eb1 = bS2E1;
                    break;
            }

            int w = Weights3[idx];
            r = (byte)BC7Interpolate(BC7Unquantize(er0, 5), BC7Unquantize(er1, 5), w);
            g = (byte)BC7Interpolate(BC7Unquantize(eg0, 5), BC7Unquantize(eg1, 5), w);
            b = (byte)BC7Interpolate(BC7Unquantize(eb0, 5), BC7Unquantize(eb1, 5), w);
            a = 255;
        }

        // Mode 1: 2 subsets, 6-bit endpoints (RGB), 1 shared pbit per subset, 3-bit indices
        static void DecodeMode1(
            ref BitReader reader,
            int pixelIndex,
            out byte r,
            out byte g,
            out byte b,
            out byte a
        )
        {
            int partition = reader.ReadBits(6);

            // R endpoints: s0e0, s0e1, s1e0, s1e1
            int rS0E0 = reader.ReadBits(6),
                rS0E1 = reader.ReadBits(6);
            int rS1E0 = reader.ReadBits(6),
                rS1E1 = reader.ReadBits(6);
            int gS0E0 = reader.ReadBits(6),
                gS0E1 = reader.ReadBits(6);
            int gS1E0 = reader.ReadBits(6),
                gS1E1 = reader.ReadBits(6);
            int bS0E0 = reader.ReadBits(6),
                bS0E1 = reader.ReadBits(6);
            int bS1E0 = reader.ReadBits(6),
                bS1E1 = reader.ReadBits(6);

            // Shared p-bits per subset
            int pb0 = reader.ReadBits(1); // shared for both endpoints of subset 0
            int pb1 = reader.ReadBits(1); // shared for both endpoints of subset 1

            rS0E0 = (rS0E0 << 1) | pb0;
            rS0E1 = (rS0E1 << 1) | pb0;
            rS1E0 = (rS1E0 << 1) | pb1;
            rS1E1 = (rS1E1 << 1) | pb1;
            gS0E0 = (gS0E0 << 1) | pb0;
            gS0E1 = (gS0E1 << 1) | pb0;
            gS1E0 = (gS1E0 << 1) | pb1;
            gS1E1 = (gS1E1 << 1) | pb1;
            bS0E0 = (bS0E0 << 1) | pb0;
            bS0E1 = (bS0E1 << 1) | pb0;
            bS1E0 = (bS1E0 << 1) | pb1;
            bS1E1 = (bS1E1 << 1) | pb1;

            int subset = PartitionTable2[partition * 16 + pixelIndex];
            int anchor1 = AnchorIndex2_1[partition];

            int idx = 0;
            for (int i = 0; i < 16; i++)
            {
                int bits = (i == 0 || i == anchor1) ? 2 : 3;
                int val = reader.ReadBits(bits);
                if (i == pixelIndex)
                    idx = val;
            }

            int er0,
                er1,
                eg0,
                eg1,
                eb0,
                eb1;
            if (subset == 0)
            {
                er0 = rS0E0;
                er1 = rS0E1;
                eg0 = gS0E0;
                eg1 = gS0E1;
                eb0 = bS0E0;
                eb1 = bS0E1;
            }
            else
            {
                er0 = rS1E0;
                er1 = rS1E1;
                eg0 = gS1E0;
                eg1 = gS1E1;
                eb0 = bS1E0;
                eb1 = bS1E1;
            }

            int w = Weights3[idx];
            r = (byte)BC7Interpolate(BC7Unquantize(er0, 7), BC7Unquantize(er1, 7), w);
            g = (byte)BC7Interpolate(BC7Unquantize(eg0, 7), BC7Unquantize(eg1, 7), w);
            b = (byte)BC7Interpolate(BC7Unquantize(eb0, 7), BC7Unquantize(eb1, 7), w);
            a = 255;
        }

        // Mode 2: 3 subsets, 5-bit endpoints (RGB), no pbit, 2-bit indices
        static void DecodeMode2(
            ref BitReader reader,
            int pixelIndex,
            out byte r,
            out byte g,
            out byte b,
            out byte a
        )
        {
            int partition = reader.ReadBits(6);

            int rS0E0 = reader.ReadBits(5),
                rS0E1 = reader.ReadBits(5);
            int rS1E0 = reader.ReadBits(5),
                rS1E1 = reader.ReadBits(5);
            int rS2E0 = reader.ReadBits(5),
                rS2E1 = reader.ReadBits(5);
            int gS0E0 = reader.ReadBits(5),
                gS0E1 = reader.ReadBits(5);
            int gS1E0 = reader.ReadBits(5),
                gS1E1 = reader.ReadBits(5);
            int gS2E0 = reader.ReadBits(5),
                gS2E1 = reader.ReadBits(5);
            int bS0E0 = reader.ReadBits(5),
                bS0E1 = reader.ReadBits(5);
            int bS1E0 = reader.ReadBits(5),
                bS1E1 = reader.ReadBits(5);
            int bS2E0 = reader.ReadBits(5),
                bS2E1 = reader.ReadBits(5);

            int subset = PartitionTable3[partition * 16 + pixelIndex];
            int anchor0 = 0;
            int anchor1 = AnchorIndex3_1[partition];
            int anchor2 = AnchorIndex3_2[partition];

            int idx = 0;
            for (int i = 0; i < 16; i++)
            {
                int bits = (i == anchor0 || i == anchor1 || i == anchor2) ? 1 : 2;
                int val = reader.ReadBits(bits);
                if (i == pixelIndex)
                    idx = val;
            }

            int er0,
                er1,
                eg0,
                eg1,
                eb0,
                eb1;
            switch (subset)
            {
                case 0:
                    er0 = rS0E0;
                    er1 = rS0E1;
                    eg0 = gS0E0;
                    eg1 = gS0E1;
                    eb0 = bS0E0;
                    eb1 = bS0E1;
                    break;
                case 1:
                    er0 = rS1E0;
                    er1 = rS1E1;
                    eg0 = gS1E0;
                    eg1 = gS1E1;
                    eb0 = bS1E0;
                    eb1 = bS1E1;
                    break;
                default:
                    er0 = rS2E0;
                    er1 = rS2E1;
                    eg0 = gS2E0;
                    eg1 = gS2E1;
                    eb0 = bS2E0;
                    eb1 = bS2E1;
                    break;
            }

            int w = Weights2[idx];
            r = (byte)BC7Interpolate(BC7Unquantize(er0, 5), BC7Unquantize(er1, 5), w);
            g = (byte)BC7Interpolate(BC7Unquantize(eg0, 5), BC7Unquantize(eg1, 5), w);
            b = (byte)BC7Interpolate(BC7Unquantize(eb0, 5), BC7Unquantize(eb1, 5), w);
            a = 255;
        }

        // Mode 3: 2 subsets, 7-bit endpoints (RGB), 1 unique pbit per endpoint, 2-bit indices
        static void DecodeMode3(
            ref BitReader reader,
            int pixelIndex,
            out byte r,
            out byte g,
            out byte b,
            out byte a
        )
        {
            int partition = reader.ReadBits(6);

            int rS0E0 = reader.ReadBits(7),
                rS0E1 = reader.ReadBits(7);
            int rS1E0 = reader.ReadBits(7),
                rS1E1 = reader.ReadBits(7);
            int gS0E0 = reader.ReadBits(7),
                gS0E1 = reader.ReadBits(7);
            int gS1E0 = reader.ReadBits(7),
                gS1E1 = reader.ReadBits(7);
            int bS0E0 = reader.ReadBits(7),
                bS0E1 = reader.ReadBits(7);
            int bS1E0 = reader.ReadBits(7),
                bS1E1 = reader.ReadBits(7);

            // 4 unique p-bits
            int pb0 = reader.ReadBits(1),
                pb1 = reader.ReadBits(1);
            int pb2 = reader.ReadBits(1),
                pb3 = reader.ReadBits(1);

            rS0E0 = (rS0E0 << 1) | pb0;
            rS0E1 = (rS0E1 << 1) | pb1;
            rS1E0 = (rS1E0 << 1) | pb2;
            rS1E1 = (rS1E1 << 1) | pb3;
            gS0E0 = (gS0E0 << 1) | pb0;
            gS0E1 = (gS0E1 << 1) | pb1;
            gS1E0 = (gS1E0 << 1) | pb2;
            gS1E1 = (gS1E1 << 1) | pb3;
            bS0E0 = (bS0E0 << 1) | pb0;
            bS0E1 = (bS0E1 << 1) | pb1;
            bS1E0 = (bS1E0 << 1) | pb2;
            bS1E1 = (bS1E1 << 1) | pb3;

            int subset = PartitionTable2[partition * 16 + pixelIndex];
            int anchor1 = AnchorIndex2_1[partition];

            int idx = 0;
            for (int i = 0; i < 16; i++)
            {
                int bits = (i == 0 || i == anchor1) ? 1 : 2;
                int val = reader.ReadBits(bits);
                if (i == pixelIndex)
                    idx = val;
            }

            int er0,
                er1,
                eg0,
                eg1,
                eb0,
                eb1;
            if (subset == 0)
            {
                er0 = rS0E0;
                er1 = rS0E1;
                eg0 = gS0E0;
                eg1 = gS0E1;
                eb0 = bS0E0;
                eb1 = bS0E1;
            }
            else
            {
                er0 = rS1E0;
                er1 = rS1E1;
                eg0 = gS1E0;
                eg1 = gS1E1;
                eb0 = bS1E0;
                eb1 = bS1E1;
            }

            int w = Weights2[idx];
            r = (byte)BC7Interpolate(BC7Unquantize(er0, 8), BC7Unquantize(er1, 8), w);
            g = (byte)BC7Interpolate(BC7Unquantize(eg0, 8), BC7Unquantize(eg1, 8), w);
            b = (byte)BC7Interpolate(BC7Unquantize(eb0, 8), BC7Unquantize(eb1, 8), w);
            a = 255;
        }

        // Mode 4: 1 subset, 5-bit RGB + 6-bit A, rotation, 2+3 bit indices (or swapped by idxMode)
        static void DecodeMode4(
            ref BitReader reader,
            int pixelIndex,
            out byte r,
            out byte g,
            out byte b,
            out byte a
        )
        {
            int rotation = reader.ReadBits(2);
            int idxMode = reader.ReadBits(1);

            int r0 = reader.ReadBits(5),
                r1 = reader.ReadBits(5);
            int g0 = reader.ReadBits(5),
                g1 = reader.ReadBits(5);
            int b0 = reader.ReadBits(5),
                b1 = reader.ReadBits(5);
            int a0 = reader.ReadBits(6),
                a1 = reader.ReadBits(6);

            // Read 2-bit index set (31 bits: anchor pixel has 1 bit)
            int idx2 = 0;
            for (int i = 0; i < 16; i++)
            {
                int bits = (i == 0) ? 1 : 2;
                int val = reader.ReadBits(bits);
                if (i == pixelIndex)
                    idx2 = val;
            }

            // Read 3-bit index set (47 bits: anchor pixel has 2 bits)
            int idx3 = 0;
            for (int i = 0; i < 16; i++)
            {
                int bits = (i == 0) ? 2 : 3;
                int val = reader.ReadBits(bits);
                if (i == pixelIndex)
                    idx3 = val;
            }

            int colorIdx,
                alphaIdx;
            if (idxMode == 0)
            {
                colorIdx = idx2;
                alphaIdx = idx3;
            }
            else
            {
                colorIdx = idx3;
                alphaIdx = idx2;
            }

            int colorWeight = idxMode == 0 ? Weights2[colorIdx] : Weights3[colorIdx];
            int alphaWeight = idxMode == 0 ? Weights3[alphaIdx] : Weights2[alphaIdx];

            int ri = BC7Interpolate(BC7Unquantize(r0, 5), BC7Unquantize(r1, 5), colorWeight);
            int gi = BC7Interpolate(BC7Unquantize(g0, 5), BC7Unquantize(g1, 5), colorWeight);
            int bi = BC7Interpolate(BC7Unquantize(b0, 5), BC7Unquantize(b1, 5), colorWeight);
            int ai = BC7Interpolate(BC7Unquantize(a0, 6), BC7Unquantize(a1, 6), alphaWeight);

            ApplyRotation(rotation, ref ri, ref gi, ref bi, ref ai);
            r = (byte)ri;
            g = (byte)gi;
            b = (byte)bi;
            a = (byte)ai;
        }

        // Mode 5: 1 subset, 7-bit RGB + 8-bit A, rotation, separate 2-bit color and alpha indices
        static void DecodeMode5(
            ref BitReader reader,
            int pixelIndex,
            out byte r,
            out byte g,
            out byte b,
            out byte a
        )
        {
            int rotation = reader.ReadBits(2);

            int r0 = reader.ReadBits(7),
                r1 = reader.ReadBits(7);
            int g0 = reader.ReadBits(7),
                g1 = reader.ReadBits(7);
            int b0 = reader.ReadBits(7),
                b1 = reader.ReadBits(7);
            int a0 = reader.ReadBits(8),
                a1 = reader.ReadBits(8);

            int colorIdx = 0;
            for (int i = 0; i < 16; i++)
            {
                int bits = (i == 0) ? 1 : 2;
                int val = reader.ReadBits(bits);
                if (i == pixelIndex)
                    colorIdx = val;
            }

            int alphaIdx = 0;
            for (int i = 0; i < 16; i++)
            {
                int bits = (i == 0) ? 1 : 2;
                int val = reader.ReadBits(bits);
                if (i == pixelIndex)
                    alphaIdx = val;
            }

            int ri = BC7Interpolate(BC7Unquantize(r0, 7), BC7Unquantize(r1, 7), Weights2[colorIdx]);
            int gi = BC7Interpolate(BC7Unquantize(g0, 7), BC7Unquantize(g1, 7), Weights2[colorIdx]);
            int bi = BC7Interpolate(BC7Unquantize(b0, 7), BC7Unquantize(b1, 7), Weights2[colorIdx]);
            int ai = BC7Interpolate(BC7Unquantize(a0, 8), BC7Unquantize(a1, 8), Weights2[alphaIdx]);

            ApplyRotation(rotation, ref ri, ref gi, ref bi, ref ai);
            r = (byte)ri;
            g = (byte)gi;
            b = (byte)bi;
            a = (byte)ai;
        }

        // Mode 6: 1 subset, 7-bit RGBA + 1 unique pbit per endpoint, 4-bit indices
        static void DecodeMode6(
            ref BitReader reader,
            int pixelIndex,
            out byte r,
            out byte g,
            out byte b,
            out byte a
        )
        {
            int r0 = reader.ReadBits(7),
                r1 = reader.ReadBits(7);
            int g0 = reader.ReadBits(7),
                g1 = reader.ReadBits(7);
            int b0 = reader.ReadBits(7),
                b1 = reader.ReadBits(7);
            int a0 = reader.ReadBits(7),
                a1 = reader.ReadBits(7);

            int pb0 = reader.ReadBits(1);
            int pb1 = reader.ReadBits(1);

            r0 = (r0 << 1) | pb0;
            r1 = (r1 << 1) | pb1;
            g0 = (g0 << 1) | pb0;
            g1 = (g1 << 1) | pb1;
            b0 = (b0 << 1) | pb0;
            b1 = (b1 << 1) | pb1;
            a0 = (a0 << 1) | pb0;
            a1 = (a1 << 1) | pb1;

            int idx = 0;
            for (int i = 0; i < 16; i++)
            {
                int bits = (i == 0) ? 3 : 4;
                int val = reader.ReadBits(bits);
                if (i == pixelIndex)
                    idx = val;
            }

            int w = Weights4[idx];
            r = (byte)BC7Interpolate(BC7Unquantize(r0, 8), BC7Unquantize(r1, 8), w);
            g = (byte)BC7Interpolate(BC7Unquantize(g0, 8), BC7Unquantize(g1, 8), w);
            b = (byte)BC7Interpolate(BC7Unquantize(b0, 8), BC7Unquantize(b1, 8), w);
            a = (byte)BC7Interpolate(BC7Unquantize(a0, 8), BC7Unquantize(a1, 8), w);
        }

        // Mode 7: 2 subsets, 5-bit RGBA + 1 unique pbit per endpoint, 2-bit indices
        static void DecodeMode7(
            ref BitReader reader,
            int pixelIndex,
            out byte r,
            out byte g,
            out byte b,
            out byte a
        )
        {
            int partition = reader.ReadBits(6);

            int rS0E0 = reader.ReadBits(5),
                rS0E1 = reader.ReadBits(5);
            int rS1E0 = reader.ReadBits(5),
                rS1E1 = reader.ReadBits(5);
            int gS0E0 = reader.ReadBits(5),
                gS0E1 = reader.ReadBits(5);
            int gS1E0 = reader.ReadBits(5),
                gS1E1 = reader.ReadBits(5);
            int bS0E0 = reader.ReadBits(5),
                bS0E1 = reader.ReadBits(5);
            int bS1E0 = reader.ReadBits(5),
                bS1E1 = reader.ReadBits(5);
            int aS0E0 = reader.ReadBits(5),
                aS0E1 = reader.ReadBits(5);
            int aS1E0 = reader.ReadBits(5),
                aS1E1 = reader.ReadBits(5);

            int pb0 = reader.ReadBits(1),
                pb1 = reader.ReadBits(1);
            int pb2 = reader.ReadBits(1),
                pb3 = reader.ReadBits(1);

            rS0E0 = (rS0E0 << 1) | pb0;
            rS0E1 = (rS0E1 << 1) | pb1;
            rS1E0 = (rS1E0 << 1) | pb2;
            rS1E1 = (rS1E1 << 1) | pb3;
            gS0E0 = (gS0E0 << 1) | pb0;
            gS0E1 = (gS0E1 << 1) | pb1;
            gS1E0 = (gS1E0 << 1) | pb2;
            gS1E1 = (gS1E1 << 1) | pb3;
            bS0E0 = (bS0E0 << 1) | pb0;
            bS0E1 = (bS0E1 << 1) | pb1;
            bS1E0 = (bS1E0 << 1) | pb2;
            bS1E1 = (bS1E1 << 1) | pb3;
            aS0E0 = (aS0E0 << 1) | pb0;
            aS0E1 = (aS0E1 << 1) | pb1;
            aS1E0 = (aS1E0 << 1) | pb2;
            aS1E1 = (aS1E1 << 1) | pb3;

            int subset = PartitionTable2[partition * 16 + pixelIndex];
            int anchor1 = AnchorIndex2_1[partition];

            int idx = 0;
            for (int i = 0; i < 16; i++)
            {
                int bits = (i == 0 || i == anchor1) ? 1 : 2;
                int val = reader.ReadBits(bits);
                if (i == pixelIndex)
                    idx = val;
            }

            int er0,
                er1,
                eg0,
                eg1,
                eb0,
                eb1,
                ea0,
                ea1;
            if (subset == 0)
            {
                er0 = rS0E0;
                er1 = rS0E1;
                eg0 = gS0E0;
                eg1 = gS0E1;
                eb0 = bS0E0;
                eb1 = bS0E1;
                ea0 = aS0E0;
                ea1 = aS0E1;
            }
            else
            {
                er0 = rS1E0;
                er1 = rS1E1;
                eg0 = gS1E0;
                eg1 = gS1E1;
                eb0 = bS1E0;
                eb1 = bS1E1;
                ea0 = aS1E0;
                ea1 = aS1E1;
            }

            int w = Weights2[idx];
            r = (byte)BC7Interpolate(BC7Unquantize(er0, 6), BC7Unquantize(er1, 6), w);
            g = (byte)BC7Interpolate(BC7Unquantize(eg0, 6), BC7Unquantize(eg1, 6), w);
            b = (byte)BC7Interpolate(BC7Unquantize(eb0, 6), BC7Unquantize(eb1, 6), w);
            a = (byte)BC7Interpolate(BC7Unquantize(ea0, 6), BC7Unquantize(ea1, 6), w);
        }
    }
}
