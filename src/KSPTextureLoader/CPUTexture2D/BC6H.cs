using System;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    public readonly struct BC6H : ICPUTexture2D
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

        readonly NativeArray<Block> data;
        readonly bool signed;

        public unsafe BC6H(
            NativeArray<byte> data,
            int width,
            int height,
            int mipCount,
            bool signed = false
        )
        {
            this.data = data.Reinterpret<Block>(sizeof(byte));
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

        public Color GetPixel(int x, int y, int mipLevel = 0)
        {
            GetBlockIndex(Width, Height, x, y, mipLevel, out int blockIndex, out int pixelIndex);
            DecodeBC6HPixel(
                data[blockIndex],
                pixelIndex,
                signed,
                out float r,
                out float g,
                out float b
            );
            return new Color(r, g, b, 1f);
        }

        public Color32 GetPixel32(int x, int y, int mipLevel = 0) => GetPixel(x, y, mipLevel);

        public Color GetPixelBilinear(float u, float v, int mipLevel = 0) =>
            CPUTexture2D.GetPixelBilinear(in this, u, v, mipLevel);

        public unsafe NativeArray<T> GetRawTextureData<T>()
            where T : unmanaged
        {
            return GetNonOwningNativeArray(data).Reinterpret<T>(sizeof(Block));
        }

        // ================================================================
        // BC6H Decoder
        // ================================================================

        struct BitReader(Block block)
        {
            Block block = block;
            int bitPos = 0;

            public int ReadBits(int count)
            {
                int result;
                int bitIdx = bitPos & 63;
                ulong mask = (1ul << count) - 1;

                if (bitPos < 64)
                {
                    result = (int)((block.lo >> bitIdx) & mask);
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

            public void SkipBits(int count) => bitPos += count;
        }

        // ---- Mode descriptor ----

        struct ModeInfo
        {
            public int numSubsets;
            public int endpointBits;
            public int deltaBitsR,
                deltaBitsG,
                deltaBitsB;
            public bool transformed;
            public int indexBits;
        }

        // csharpier-ignore-start
        static readonly ModeInfo[] Modes =
        [
            new() { numSubsets = 2, endpointBits = 10, deltaBitsR = 5,  deltaBitsG = 5,  deltaBitsB = 5,  transformed = true,  indexBits = 3 }, // 0
            new() { numSubsets = 2, endpointBits = 7,  deltaBitsR = 6,  deltaBitsG = 6,  deltaBitsB = 6,  transformed = true,  indexBits = 3 }, // 1
            new() { numSubsets = 2, endpointBits = 11, deltaBitsR = 5,  deltaBitsG = 4,  deltaBitsB = 4,  transformed = true,  indexBits = 3 }, // 2
            new() { numSubsets = 2, endpointBits = 11, deltaBitsR = 4,  deltaBitsG = 5,  deltaBitsB = 4,  transformed = true,  indexBits = 3 }, // 3
            new() { numSubsets = 2, endpointBits = 11, deltaBitsR = 4,  deltaBitsG = 4,  deltaBitsB = 5,  transformed = true,  indexBits = 3 }, // 4
            new() { numSubsets = 2, endpointBits = 9,  deltaBitsR = 5,  deltaBitsG = 5,  deltaBitsB = 5,  transformed = true,  indexBits = 3 }, // 5
            new() { numSubsets = 2, endpointBits = 8,  deltaBitsR = 6,  deltaBitsG = 5,  deltaBitsB = 5,  transformed = true,  indexBits = 3 }, // 6
            new() { numSubsets = 2, endpointBits = 8,  deltaBitsR = 5,  deltaBitsG = 6,  deltaBitsB = 5,  transformed = true,  indexBits = 3 }, // 7
            new() { numSubsets = 2, endpointBits = 8,  deltaBitsR = 5,  deltaBitsG = 5,  deltaBitsB = 6,  transformed = true,  indexBits = 3 }, // 8
            new() { numSubsets = 2, endpointBits = 6,  deltaBitsR = 6,  deltaBitsG = 6,  deltaBitsB = 6,  transformed = false, indexBits = 3 }, // 9
            new() { numSubsets = 1, endpointBits = 10, deltaBitsR = 10, deltaBitsG = 10, deltaBitsB = 10, transformed = false, indexBits = 4 }, // 10
            new() { numSubsets = 1, endpointBits = 11, deltaBitsR = 9,  deltaBitsG = 9,  deltaBitsB = 9,  transformed = true,  indexBits = 4 }, // 11
            new() { numSubsets = 1, endpointBits = 12, deltaBitsR = 8,  deltaBitsG = 8,  deltaBitsB = 8,  transformed = true,  indexBits = 4 }, // 12
            new() { numSubsets = 1, endpointBits = 16, deltaBitsR = 4,  deltaBitsG = 4,  deltaBitsB = 4,  transformed = true,  indexBits = 4 }, // 13
        ];

        // 2-subset partition table (32 partitions x 16 pixels), shared with BC7
        static readonly byte[] PartitionTable =
        [
            0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1, // 0
            0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1, // 1
            0,1,1,1,0,1,1,1,0,1,1,1,0,1,1,1, // 2
            0,0,0,1,0,0,1,1,0,0,1,1,0,1,1,1, // 3
            0,0,0,0,0,0,0,1,0,0,0,1,0,0,1,1, // 4
            0,0,1,1,0,1,1,1,0,1,1,1,1,1,1,1, // 5
            0,0,0,1,0,0,1,1,0,1,1,1,1,1,1,1, // 6
            0,0,0,0,0,0,0,1,0,0,1,1,0,1,1,1, // 7
            0,0,0,0,0,0,0,0,0,0,0,1,0,0,1,1, // 8
            0,0,1,1,0,1,1,1,1,1,1,1,1,1,1,1, // 9
            0,0,0,0,0,0,0,1,0,1,1,1,1,1,1,1, // 10
            0,0,0,0,0,0,0,0,0,0,0,1,0,1,1,1, // 11
            0,0,0,1,0,1,1,1,1,1,1,1,1,1,1,1, // 12
            0,0,0,0,0,0,0,0,1,1,1,1,1,1,1,1, // 13
            0,0,0,0,1,1,1,1,1,1,1,1,1,1,1,1, // 14
            0,0,0,0,0,0,0,0,0,0,0,0,1,1,1,1, // 15
            0,0,0,0,1,0,0,0,1,1,1,0,1,1,1,1, // 16
            0,1,1,1,0,0,0,1,0,0,0,0,0,0,0,0, // 17
            0,0,0,0,0,0,0,0,1,0,0,0,1,1,1,0, // 18
            0,1,1,1,0,0,1,1,0,0,0,1,0,0,0,0, // 19
            0,0,1,1,0,0,0,1,0,0,0,0,0,0,0,0, // 20
            0,0,0,0,1,0,0,0,1,1,0,0,1,1,1,0, // 21
            0,0,0,0,0,0,0,0,1,0,0,0,1,1,0,0, // 22
            0,1,1,1,0,0,1,1,0,0,1,1,0,0,0,1, // 23
            0,0,1,1,0,0,0,1,0,0,0,1,0,0,0,0, // 24
            0,0,0,0,1,0,0,0,1,0,0,0,1,1,0,0, // 25
            0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,0, // 26
            0,0,1,1,0,1,1,0,0,1,1,0,1,1,0,0, // 27
            0,0,0,1,0,1,1,1,1,1,1,0,1,0,0,0, // 28
            0,0,0,0,1,1,1,1,1,1,1,1,0,0,0,0, // 29
            0,1,1,1,0,0,0,1,1,0,0,0,1,1,1,0, // 30
            0,0,1,1,1,0,0,1,1,0,0,1,1,1,0,0, // 31
        ];

        // Anchor index for second subset in 2-subset partitions
        static readonly byte[] AnchorIndex =
        [
            15,15,15,15,15,15,15,15,
            15,15,15,15,15,15,15,15,
            15, 2, 8, 2, 2, 8, 8,15,
             2, 8, 2, 2, 8, 8, 2, 2,
        ];

        static readonly byte[] Weights3 = [0, 9, 18, 27, 37, 46, 55, 64];
        static readonly byte[] Weights4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];
        // csharpier-ignore-end

        // ---- Utility functions ----

        static int SignExtend(int val, int bits)
        {
            int shift = 32 - bits;
            return (val << shift) >> shift;
        }

        static int ReverseBits(int val, int numBits)
        {
            int result = 0;
            for (int i = 0; i < numBits; i++)
            {
                result = (result << 1) | (val & 1);
                val >>= 1;
            }
            return result;
        }

        static float HalfToFloat(int h)
        {
            return (float)new Utils.Half((ushort)h);
        }

        static int Unquantize(int val, int bits, bool signed)
        {
            if (signed)
            {
                if (bits >= 16)
                    return val;
                bool s = false;
                if (val < 0)
                {
                    s = true;
                    val = -val;
                }
                int unq;
                if (val == 0)
                    unq = 0;
                else if (val >= ((1 << (bits - 1)) - 1))
                    unq = 0x7FFF;
                else
                    unq = ((val << 15) + 0x4000) >> (bits - 1);
                return s ? -unq : unq;
            }
            else
            {
                if (bits >= 15)
                    return val;
                if (val == 0)
                    return 0;
                if (val == ((1 << bits) - 1))
                    return 0xFFFF;
                return ((val << 15) + 0x4000) >> (bits - 1);
            }
        }

        static float FinishUnquantize(int val, bool signed)
        {
            if (signed)
            {
                int s = 0;
                if (val < 0)
                {
                    s = 0x8000;
                    val = -val;
                }
                return HalfToFloat(s | ((val * 31) >> 5));
            }
            else
            {
                return HalfToFloat((val * 31) >> 6);
            }
        }

        // ---- Mode detection ----

        static int GetMode(Block block)
        {
            int low5 = (int)(block.lo & 0x1F);
            if ((low5 & 3) == 0)
                return 0;
            if ((low5 & 3) == 1)
                return 1;
            return low5 switch
            {
                0x02 => 2,
                0x06 => 3,
                0x0A => 4,
                0x0E => 5,
                0x12 => 6,
                0x16 => 7,
                0x1A => 8,
                0x1E => 9,
                0x03 => 10,
                0x07 => 11,
                0x0B => 12,
                0x0F => 13,
                _ => -1,
            };
        }

        // ---- Endpoint storage ----

        struct Endpoints
        {
            public int e0r,
                e0g,
                e0b;
            public int e1r,
                e1g,
                e1b;
            public int e2r,
                e2g,
                e2b;
            public int e3r,
                e3g,
                e3b;
        }

        // ---- Endpoint extraction for all 14 modes ----
        // Bit layouts from the bcdec reference decoder.
        // e0 = base endpoint (rw,gw,bw), e1 = (rx,gx,bx)
        // e2 = (ry,gy,by), e3 = (rz,gz,bz)

        static void DecodeEndpoints(ref BitReader r, int mode, out Endpoints ep, out int partition)
        {
            ep = default;
            partition = 0;

            switch (mode)
            {
                case 0: // 10-bit base, 5/5/5 delta
                {
                    ep.e2g |= r.ReadBits(1) << 4;
                    ep.e2b |= r.ReadBits(1) << 4;
                    ep.e3b |= r.ReadBits(1) << 4;
                    ep.e0r |= r.ReadBits(10);
                    ep.e0g |= r.ReadBits(10);
                    ep.e0b |= r.ReadBits(10);
                    ep.e1r |= r.ReadBits(5);
                    ep.e3g |= r.ReadBits(1) << 4;
                    ep.e2g |= r.ReadBits(4);
                    ep.e1g |= r.ReadBits(5);
                    ep.e3b |= r.ReadBits(1);
                    ep.e3g |= r.ReadBits(4);
                    ep.e1b |= r.ReadBits(5);
                    ep.e3b |= r.ReadBits(1) << 1;
                    ep.e2b |= r.ReadBits(4);
                    ep.e2r |= r.ReadBits(5);
                    ep.e3b |= r.ReadBits(1) << 2;
                    ep.e3r |= r.ReadBits(5);
                    ep.e3b |= r.ReadBits(1) << 3;
                    partition = r.ReadBits(5);
                    break;
                }

                case 1: // 7-bit base, 6/6/6 delta
                {
                    ep.e2g |= r.ReadBits(1) << 5;
                    ep.e3g |= r.ReadBits(1) << 4;
                    ep.e3g |= r.ReadBits(1) << 5;
                    ep.e0r |= r.ReadBits(7);
                    ep.e3b |= r.ReadBits(1);
                    ep.e3b |= r.ReadBits(1) << 1;
                    ep.e2b |= r.ReadBits(1) << 4;
                    ep.e0g |= r.ReadBits(7);
                    ep.e2b |= r.ReadBits(1) << 5;
                    ep.e3b |= r.ReadBits(1) << 2;
                    ep.e2g |= r.ReadBits(1) << 4;
                    ep.e0b |= r.ReadBits(7);
                    ep.e3b |= r.ReadBits(1) << 3;
                    ep.e3b |= r.ReadBits(1) << 5;
                    ep.e3b |= r.ReadBits(1) << 4;
                    ep.e1r |= r.ReadBits(6);
                    ep.e2g |= r.ReadBits(4);
                    ep.e1g |= r.ReadBits(6);
                    ep.e3g |= r.ReadBits(4);
                    ep.e1b |= r.ReadBits(6);
                    ep.e2b |= r.ReadBits(4);
                    ep.e2r |= r.ReadBits(6);
                    ep.e3r |= r.ReadBits(6);
                    partition = r.ReadBits(5);
                    break;
                }

                case 2: // 11-bit base (10+1), 5/4/4 delta
                {
                    ep.e0r |= r.ReadBits(10);
                    ep.e0g |= r.ReadBits(10);
                    ep.e0b |= r.ReadBits(10);
                    ep.e1r |= r.ReadBits(5);
                    ep.e0r |= r.ReadBits(1) << 10;
                    ep.e2g |= r.ReadBits(4);
                    ep.e1g |= r.ReadBits(4);
                    ep.e0g |= r.ReadBits(1) << 10;
                    ep.e3b |= r.ReadBits(1);
                    ep.e3g |= r.ReadBits(4);
                    ep.e1b |= r.ReadBits(4);
                    ep.e0b |= r.ReadBits(1) << 10;
                    ep.e3b |= r.ReadBits(1) << 1;
                    ep.e2b |= r.ReadBits(4);
                    ep.e2r |= r.ReadBits(5);
                    ep.e3b |= r.ReadBits(1) << 2;
                    ep.e3r |= r.ReadBits(5);
                    ep.e3b |= r.ReadBits(1) << 3;
                    partition = r.ReadBits(5);
                    break;
                }

                case 3: // 11-bit base, 4/5/4 delta
                {
                    ep.e0r |= r.ReadBits(10);
                    ep.e0g |= r.ReadBits(10);
                    ep.e0b |= r.ReadBits(10);
                    ep.e1r |= r.ReadBits(4);
                    ep.e0r |= r.ReadBits(1) << 10;
                    ep.e3g |= r.ReadBits(1) << 4;
                    ep.e2g |= r.ReadBits(4);
                    ep.e1g |= r.ReadBits(5);
                    ep.e0g |= r.ReadBits(1) << 10;
                    ep.e3g |= r.ReadBits(4);
                    ep.e1b |= r.ReadBits(4);
                    ep.e0b |= r.ReadBits(1) << 10;
                    ep.e3b |= r.ReadBits(1) << 1;
                    ep.e2b |= r.ReadBits(4);
                    ep.e2r |= r.ReadBits(4);
                    ep.e3b |= r.ReadBits(1);
                    ep.e3b |= r.ReadBits(1) << 2;
                    ep.e3r |= r.ReadBits(4);
                    ep.e2g |= r.ReadBits(1) << 4;
                    ep.e3b |= r.ReadBits(1) << 3;
                    partition = r.ReadBits(5);
                    break;
                }

                case 4: // 11-bit base, 4/4/5 delta
                {
                    ep.e0r |= r.ReadBits(10);
                    ep.e0g |= r.ReadBits(10);
                    ep.e0b |= r.ReadBits(10);
                    ep.e1r |= r.ReadBits(4);
                    ep.e0r |= r.ReadBits(1) << 10;
                    ep.e2b |= r.ReadBits(1) << 4;
                    ep.e2g |= r.ReadBits(4);
                    ep.e1g |= r.ReadBits(4);
                    ep.e0g |= r.ReadBits(1) << 10;
                    ep.e3b |= r.ReadBits(1);
                    ep.e3g |= r.ReadBits(4);
                    ep.e1b |= r.ReadBits(5);
                    ep.e0b |= r.ReadBits(1) << 10;
                    ep.e2b |= r.ReadBits(4);
                    ep.e2r |= r.ReadBits(4);
                    ep.e3b |= r.ReadBits(1) << 1;
                    ep.e3b |= r.ReadBits(1) << 2;
                    ep.e3r |= r.ReadBits(4);
                    ep.e3b |= r.ReadBits(1) << 4;
                    ep.e3b |= r.ReadBits(1) << 3;
                    partition = r.ReadBits(5);
                    break;
                }

                case 5: // 9-bit base, 5/5/5 delta
                {
                    ep.e0r |= r.ReadBits(9);
                    ep.e2b |= r.ReadBits(1) << 4;
                    ep.e0g |= r.ReadBits(9);
                    ep.e2g |= r.ReadBits(1) << 4;
                    ep.e0b |= r.ReadBits(9);
                    ep.e3b |= r.ReadBits(1) << 4;
                    ep.e1r |= r.ReadBits(5);
                    ep.e3g |= r.ReadBits(1) << 4;
                    ep.e2g |= r.ReadBits(4);
                    ep.e1g |= r.ReadBits(5);
                    ep.e3b |= r.ReadBits(1);
                    ep.e3g |= r.ReadBits(4);
                    ep.e1b |= r.ReadBits(5);
                    ep.e3b |= r.ReadBits(1) << 1;
                    ep.e2b |= r.ReadBits(4);
                    ep.e2r |= r.ReadBits(5);
                    ep.e3b |= r.ReadBits(1) << 2;
                    ep.e3r |= r.ReadBits(5);
                    ep.e3b |= r.ReadBits(1) << 3;
                    partition = r.ReadBits(5);
                    break;
                }

                case 6: // 8-bit base, 6/5/5 delta
                {
                    ep.e0r |= r.ReadBits(8);
                    ep.e3g |= r.ReadBits(1) << 4;
                    ep.e2b |= r.ReadBits(1) << 4;
                    ep.e0g |= r.ReadBits(8);
                    ep.e3b |= r.ReadBits(1) << 2;
                    ep.e2g |= r.ReadBits(1) << 4;
                    ep.e0b |= r.ReadBits(8);
                    ep.e3b |= r.ReadBits(1) << 3;
                    ep.e3b |= r.ReadBits(1) << 4;
                    ep.e1r |= r.ReadBits(6);
                    ep.e2g |= r.ReadBits(4);
                    ep.e1g |= r.ReadBits(5);
                    ep.e3b |= r.ReadBits(1);
                    ep.e3g |= r.ReadBits(4);
                    ep.e1b |= r.ReadBits(5);
                    ep.e3b |= r.ReadBits(1) << 1;
                    ep.e2b |= r.ReadBits(4);
                    ep.e2r |= r.ReadBits(6);
                    ep.e3r |= r.ReadBits(6);
                    partition = r.ReadBits(5);
                    break;
                }

                case 7: // 8-bit base, 5/6/5 delta
                {
                    ep.e0r |= r.ReadBits(8);
                    ep.e3b |= r.ReadBits(1);
                    ep.e2b |= r.ReadBits(1) << 4;
                    ep.e0g |= r.ReadBits(8);
                    ep.e2g |= r.ReadBits(1) << 5;
                    ep.e2g |= r.ReadBits(1) << 4;
                    ep.e0b |= r.ReadBits(8);
                    ep.e3g |= r.ReadBits(1) << 5;
                    ep.e3b |= r.ReadBits(1) << 4;
                    ep.e1r |= r.ReadBits(5);
                    ep.e3g |= r.ReadBits(1) << 4;
                    ep.e2g |= r.ReadBits(4);
                    ep.e1g |= r.ReadBits(6);
                    ep.e3g |= r.ReadBits(4);
                    ep.e1b |= r.ReadBits(5);
                    ep.e3b |= r.ReadBits(1) << 1;
                    ep.e2b |= r.ReadBits(4);
                    ep.e2r |= r.ReadBits(5);
                    ep.e3b |= r.ReadBits(1) << 2;
                    ep.e3r |= r.ReadBits(5);
                    ep.e3b |= r.ReadBits(1) << 3;
                    partition = r.ReadBits(5);
                    break;
                }

                case 8: // 8-bit base, 5/5/6 delta
                {
                    ep.e0r |= r.ReadBits(8);
                    ep.e3b |= r.ReadBits(1) << 1;
                    ep.e2b |= r.ReadBits(1) << 4;
                    ep.e0g |= r.ReadBits(8);
                    ep.e2b |= r.ReadBits(1) << 5;
                    ep.e2g |= r.ReadBits(1) << 4;
                    ep.e0b |= r.ReadBits(8);
                    ep.e3b |= r.ReadBits(1) << 5;
                    ep.e3b |= r.ReadBits(1) << 4;
                    ep.e1r |= r.ReadBits(5);
                    ep.e3g |= r.ReadBits(1) << 4;
                    ep.e2g |= r.ReadBits(4);
                    ep.e1g |= r.ReadBits(5);
                    ep.e3b |= r.ReadBits(1);
                    ep.e3g |= r.ReadBits(4);
                    ep.e1b |= r.ReadBits(6);
                    ep.e2b |= r.ReadBits(4);
                    ep.e2r |= r.ReadBits(5);
                    ep.e3b |= r.ReadBits(1) << 2;
                    ep.e3r |= r.ReadBits(5);
                    ep.e3b |= r.ReadBits(1) << 3;
                    partition = r.ReadBits(5);
                    break;
                }

                case 9: // 6-bit base, 6/6/6 (no transform)
                {
                    ep.e0r |= r.ReadBits(6);
                    ep.e3g |= r.ReadBits(1) << 4;
                    ep.e3b |= r.ReadBits(1);
                    ep.e3b |= r.ReadBits(1) << 1;
                    ep.e2b |= r.ReadBits(1) << 4;
                    ep.e0g |= r.ReadBits(6);
                    ep.e2g |= r.ReadBits(1) << 5;
                    ep.e2b |= r.ReadBits(1) << 5;
                    ep.e3b |= r.ReadBits(1) << 2;
                    ep.e2g |= r.ReadBits(1) << 4;
                    ep.e0b |= r.ReadBits(6);
                    ep.e3g |= r.ReadBits(1) << 5;
                    ep.e3b |= r.ReadBits(1) << 3;
                    ep.e3b |= r.ReadBits(1) << 5;
                    ep.e3b |= r.ReadBits(1) << 4;
                    ep.e1r |= r.ReadBits(6);
                    ep.e2g |= r.ReadBits(4);
                    ep.e1g |= r.ReadBits(6);
                    ep.e3g |= r.ReadBits(4);
                    ep.e1b |= r.ReadBits(6);
                    ep.e2b |= r.ReadBits(4);
                    ep.e2r |= r.ReadBits(6);
                    ep.e3r |= r.ReadBits(6);
                    partition = r.ReadBits(5);
                    break;
                }

                case 10: // 10-bit direct, no delta, no transform
                {
                    ep.e0r |= r.ReadBits(10);
                    ep.e0g |= r.ReadBits(10);
                    ep.e0b |= r.ReadBits(10);
                    ep.e1r |= r.ReadBits(10);
                    ep.e1g |= r.ReadBits(10);
                    ep.e1b |= r.ReadBits(10);
                    break;
                }

                case 11: // 11-bit base (10+1), 9/9/9 delta
                {
                    ep.e0r |= r.ReadBits(10);
                    ep.e0g |= r.ReadBits(10);
                    ep.e0b |= r.ReadBits(10);
                    ep.e1r |= r.ReadBits(9);
                    ep.e0r |= r.ReadBits(1) << 10;
                    ep.e1g |= r.ReadBits(9);
                    ep.e0g |= r.ReadBits(1) << 10;
                    ep.e1b |= r.ReadBits(9);
                    ep.e0b |= r.ReadBits(1) << 10;
                    break;
                }

                case 12: // 12-bit base (10+2 reversed), 8/8/8 delta
                {
                    ep.e0r |= r.ReadBits(10);
                    ep.e0g |= r.ReadBits(10);
                    ep.e0b |= r.ReadBits(10);
                    ep.e1r |= r.ReadBits(8);
                    ep.e0r |= ReverseBits(r.ReadBits(2), 2) << 10;
                    ep.e1g |= r.ReadBits(8);
                    ep.e0g |= ReverseBits(r.ReadBits(2), 2) << 10;
                    ep.e1b |= r.ReadBits(8);
                    ep.e0b |= ReverseBits(r.ReadBits(2), 2) << 10;
                    break;
                }

                case 13: // 16-bit base (10+6 reversed), 4/4/4 delta
                {
                    ep.e0r |= r.ReadBits(10);
                    ep.e0g |= r.ReadBits(10);
                    ep.e0b |= r.ReadBits(10);
                    ep.e1r |= r.ReadBits(4);
                    ep.e0r |= ReverseBits(r.ReadBits(6), 6) << 10;
                    ep.e1g |= r.ReadBits(4);
                    ep.e0g |= ReverseBits(r.ReadBits(6), 6) << 10;
                    ep.e1b |= r.ReadBits(4);
                    ep.e0b |= ReverseBits(r.ReadBits(6), 6) << 10;
                    break;
                }
            }
        }

        // ---- Main pixel decoder ----

        static void DecodeBC6HPixel(
            Block block,
            int pixelIndex,
            bool signed,
            out float r,
            out float g,
            out float b
        )
        {
            int mode = GetMode(block);
            if (mode < 0)
            {
                r = g = b = 0f;
                return;
            }

            var info = Modes[mode];

            var reader = new BitReader(block);
            reader.SkipBits(mode <= 1 ? 2 : 5);

            DecodeEndpoints(ref reader, mode, out var ep, out int partition);

            // Apply transforms (delta decoding)
            if (info.transformed)
            {
                ep.e1r = SignExtend(ep.e1r, info.deltaBitsR) + ep.e0r;
                ep.e1g = SignExtend(ep.e1g, info.deltaBitsG) + ep.e0g;
                ep.e1b = SignExtend(ep.e1b, info.deltaBitsB) + ep.e0b;

                if (info.numSubsets == 2)
                {
                    ep.e2r = SignExtend(ep.e2r, info.deltaBitsR) + ep.e0r;
                    ep.e2g = SignExtend(ep.e2g, info.deltaBitsG) + ep.e0g;
                    ep.e2b = SignExtend(ep.e2b, info.deltaBitsB) + ep.e0b;
                    ep.e3r = SignExtend(ep.e3r, info.deltaBitsR) + ep.e0r;
                    ep.e3g = SignExtend(ep.e3g, info.deltaBitsG) + ep.e0g;
                    ep.e3b = SignExtend(ep.e3b, info.deltaBitsB) + ep.e0b;
                }

                // Mask to endpoint precision
                int mask = (1 << info.endpointBits) - 1;
                if (signed)
                {
                    ep.e0r = SignExtend(ep.e0r & mask, info.endpointBits);
                    ep.e0g = SignExtend(ep.e0g & mask, info.endpointBits);
                    ep.e0b = SignExtend(ep.e0b & mask, info.endpointBits);
                    ep.e1r = SignExtend(ep.e1r & mask, info.endpointBits);
                    ep.e1g = SignExtend(ep.e1g & mask, info.endpointBits);
                    ep.e1b = SignExtend(ep.e1b & mask, info.endpointBits);
                    ep.e2r = SignExtend(ep.e2r & mask, info.endpointBits);
                    ep.e2g = SignExtend(ep.e2g & mask, info.endpointBits);
                    ep.e2b = SignExtend(ep.e2b & mask, info.endpointBits);
                    ep.e3r = SignExtend(ep.e3r & mask, info.endpointBits);
                    ep.e3g = SignExtend(ep.e3g & mask, info.endpointBits);
                    ep.e3b = SignExtend(ep.e3b & mask, info.endpointBits);
                }
                else
                {
                    ep.e0r &= mask;
                    ep.e0g &= mask;
                    ep.e0b &= mask;
                    ep.e1r &= mask;
                    ep.e1g &= mask;
                    ep.e1b &= mask;
                    ep.e2r &= mask;
                    ep.e2g &= mask;
                    ep.e2b &= mask;
                    ep.e3r &= mask;
                    ep.e3g &= mask;
                    ep.e3b &= mask;
                }
            }

            // Unquantize all endpoints
            ep.e0r = Unquantize(ep.e0r, info.endpointBits, signed);
            ep.e0g = Unquantize(ep.e0g, info.endpointBits, signed);
            ep.e0b = Unquantize(ep.e0b, info.endpointBits, signed);
            ep.e1r = Unquantize(ep.e1r, info.endpointBits, signed);
            ep.e1g = Unquantize(ep.e1g, info.endpointBits, signed);
            ep.e1b = Unquantize(ep.e1b, info.endpointBits, signed);
            ep.e2r = Unquantize(ep.e2r, info.endpointBits, signed);
            ep.e2g = Unquantize(ep.e2g, info.endpointBits, signed);
            ep.e2b = Unquantize(ep.e2b, info.endpointBits, signed);
            ep.e3r = Unquantize(ep.e3r, info.endpointBits, signed);
            ep.e3g = Unquantize(ep.e3g, info.endpointBits, signed);
            ep.e3b = Unquantize(ep.e3b, info.endpointBits, signed);

            // Determine subset
            int subset = info.numSubsets == 2 ? PartitionTable[partition * 16 + pixelIndex] : 0;

            // Read index for this pixel
            int anchor0 = 0;
            int anchor1 = info.numSubsets == 2 ? AnchorIndex[partition] : -1;

            int indexStart = 128 - (info.numSubsets == 2 ? 46 : 63);
            var idxReader = new BitReader(block);
            idxReader.SkipBits(indexStart);

            int idx = 0;
            for (int i = 0; i < 16; i++)
            {
                int bits = info.indexBits;
                if (i == anchor0 || i == anchor1)
                    bits--;
                int val = idxReader.ReadBits(bits);
                if (i == pixelIndex)
                    idx = val;
            }

            // Select endpoints by subset
            int lor,
                log,
                lob,
                hir,
                hig,
                hib;
            if (subset == 0)
            {
                lor = ep.e0r;
                log = ep.e0g;
                lob = ep.e0b;
                hir = ep.e1r;
                hig = ep.e1g;
                hib = ep.e1b;
            }
            else
            {
                lor = ep.e2r;
                log = ep.e2g;
                lob = ep.e2b;
                hir = ep.e3r;
                hig = ep.e3g;
                hib = ep.e3b;
            }

            // Interpolate
            int w = info.indexBits == 3 ? Weights3[idx] : Weights4[idx];
            int finalR = ((64 - w) * lor + w * hir + 32) >> 6;
            int finalG = ((64 - w) * log + w * hig + 32) >> 6;
            int finalB = ((64 - w) * lob + w * hib + 32) >> 6;

            // Finish unquantize to float
            r = FinishUnquantize(finalR, signed);
            g = FinishUnquantize(finalG, signed);
            b = FinishUnquantize(finalB, signed);
        }
    }
}
