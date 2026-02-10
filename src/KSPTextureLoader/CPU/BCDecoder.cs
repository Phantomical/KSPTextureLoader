using System;
using System.Runtime.CompilerServices;
using Unity.Collections;

namespace KSPTextureLoader.CPU;

internal static class BCDecoder
{
    private const float Byte2Float = 1f / 255f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void BlockCoords(
        int x,
        int y,
        int width,
        out int blockIndex,
        out int localX,
        out int localY,
        int blockSize
    )
    {
        int blockX = x >> 2;
        int blockY = y >> 2;
        localX = x & 3;
        localY = y & 3;
        int blocksPerRow = (width + 3) >> 2;
        blockIndex = (blockY * blocksPerRow + blockX) * blockSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void UnpackRGB565(ushort c, out float r, out float g, out float b)
    {
        r = ((c >> 11) & 0x1F) * (1f / 31f);
        g = ((c >> 5) & 0x3F) * (1f / 63f);
        b = (c & 0x1F) * (1f / 31f);
    }

    internal static void DecodeDXT1Pixel(
        NativeArray<byte> data,
        int blockOffset,
        int localX,
        int localY,
        out float r,
        out float g,
        out float b,
        out float a
    )
    {
        ushort c0 = (ushort)(data[blockOffset] | (data[blockOffset + 1] << 8));
        ushort c1 = (ushort)(data[blockOffset + 2] | (data[blockOffset + 3] << 8));

        UnpackRGB565(c0, out float r0, out float g0, out float b0);
        UnpackRGB565(c1, out float r1, out float g1, out float b1);

        int indexByte = data[blockOffset + 4 + localY];
        int code = (indexByte >> (localX * 2)) & 3;

        if (c0 > c1)
        {
            switch (code)
            {
                case 0:
                    r = r0;
                    g = g0;
                    b = b0;
                    a = 1f;
                    return;
                case 1:
                    r = r1;
                    g = g1;
                    b = b1;
                    a = 1f;
                    return;
                case 2:
                    r = (2f * r0 + r1) * (1f / 3f);
                    g = (2f * g0 + g1) * (1f / 3f);
                    b = (2f * b0 + b1) * (1f / 3f);
                    a = 1f;
                    return;
                default:
                    r = (r0 + 2f * r1) * (1f / 3f);
                    g = (g0 + 2f * g1) * (1f / 3f);
                    b = (b0 + 2f * b1) * (1f / 3f);
                    a = 1f;
                    return;
            }
        }
        else
        {
            switch (code)
            {
                case 0:
                    r = r0;
                    g = g0;
                    b = b0;
                    a = 1f;
                    return;
                case 1:
                    r = r1;
                    g = g1;
                    b = b1;
                    a = 1f;
                    return;
                case 2:
                    r = (r0 + r1) * 0.5f;
                    g = (g0 + g1) * 0.5f;
                    b = (b0 + b1) * 0.5f;
                    a = 1f;
                    return;
                default:
                    r = 0f;
                    g = 0f;
                    b = 0f;
                    a = 0f;
                    return;
            }
        }
    }

    internal static float DecodeBC4Block(
        NativeArray<byte> data,
        int blockOffset,
        int localX,
        int localY
    )
    {
        float a0 = data[blockOffset] * Byte2Float;
        float a1 = data[blockOffset + 1] * Byte2Float;

        int bitOffset = (localY * 4 + localX) * 3;
        int byteIndex = blockOffset + 2 + bitOffset / 8;
        int bitShift = bitOffset % 8;

        int code;
        if (bitShift <= 5)
        {
            code = (data[byteIndex] >> bitShift) & 7;
        }
        else
        {
            code = ((data[byteIndex] >> bitShift) | (data[byteIndex + 1] << (8 - bitShift))) & 7;
        }

        if (data[blockOffset] > data[blockOffset + 1])
        {
            return code switch
            {
                0 => a0,
                1 => a1,
                2 => (6f * a0 + 1f * a1) * (1f / 7f),
                3 => (5f * a0 + 2f * a1) * (1f / 7f),
                4 => (4f * a0 + 3f * a1) * (1f / 7f),
                5 => (3f * a0 + 4f * a1) * (1f / 7f),
                6 => (2f * a0 + 5f * a1) * (1f / 7f),
                _ => (1f * a0 + 6f * a1) * (1f / 7f),
            };
        }
        else
        {
            return code switch
            {
                0 => a0,
                1 => a1,
                2 => (4f * a0 + 1f * a1) * (1f / 5f),
                3 => (3f * a0 + 2f * a1) * (1f / 5f),
                4 => (2f * a0 + 3f * a1) * (1f / 5f),
                5 => (1f * a0 + 4f * a1) * (1f / 5f),
                6 => 0f,
                _ => 1f,
            };
        }
    }

    // ---- BC7 ----

    private struct BitReader
    {
        private NativeArray<byte> data;
        private int offset;
        private int bitPos;

        public BitReader(NativeArray<byte> data, int offset)
        {
            this.data = data;
            this.offset = offset;
            bitPos = 0;
        }

        public int ReadBits(int count)
        {
            int result = 0;
            for (int i = 0; i < count; i++)
            {
                int byteIdx = offset + (bitPos >> 3);
                int bitIdx = bitPos & 7;
                result |= ((data[byteIdx] >> bitIdx) & 1) << i;
                bitPos++;
            }
            return result;
        }

        public void SkipBits(int count)
        {
            bitPos += count;
        }
    }

    // csharpier-ignore-start
    private static readonly byte[] BC7PartitionTable2 =
    {
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
    };

    private static readonly byte[] BC7PartitionTable3 =
    {
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
    };

    private static readonly byte[] BC7AnchorIndex2_1 =
    {
        15,15,15,15,15,15,15,15,
        15,15,15,15,15,15,15,15,
        15, 2, 8, 2, 2, 8, 8,15,
         2, 8, 2, 2, 8, 8, 2, 2,
        15,15, 6, 8, 2, 8,15,15,
         2, 8, 2, 2, 2,15,15, 6,
         6, 2, 6, 8,15,15, 2, 2,
        15,15,15,15,15, 2, 2,15,
    };

    private static readonly byte[] BC7AnchorIndex3_1 =
    {
         3, 3,15,15, 8, 3,15,15,
         8, 8, 6, 6, 6, 5, 3, 3,
         3, 3, 8,15, 3, 3, 6,10,
         5, 8, 8, 6, 8, 5,15,15,
         8,15, 3, 5, 6,10, 8,15,
        15, 3,15, 5,15,15,15,15,
         3,15, 5, 5, 5, 8, 5,10,
         5,10, 8,13,15,12, 3, 3,
    };

    private static readonly byte[] BC7AnchorIndex3_2 =
    {
        15, 8, 8, 3,15,15, 3, 8,
        15,15,15,15,15,15,15, 8,
        15, 8,15, 3,15, 8,15, 8,
         3,15, 6,10,15,15,10, 8,
        15, 3,15,10,10, 8, 9,10,
         6,15, 8,15, 3, 6, 6, 8,
        15, 3,15,15,15,15,15,15,
        15,15,15,15, 3,15,15, 8,
    };

    private static readonly byte[] BC7Weights2 = { 0, 21, 43, 64 };
    private static readonly byte[] BC7Weights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
    private static readonly byte[] BC7Weights4 =
    {
        0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64,
    };
    // csharpier-ignore-end

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BC7Interpolate(int e0, int e1, int weight)
    {
        return (e0 * (64 - weight) + e1 * weight + 32) >> 6;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BC7Unquantize(int val, int bits)
    {
        if (bits >= 8)
            return val;
        val = val << (8 - bits);
        return val | (val >> bits);
    }

    internal static void DecodeBC7Pixel(
        NativeArray<byte> data,
        int blockOffset,
        int localX,
        int localY,
        out float r,
        out float g,
        out float b,
        out float a
    )
    {
        var reader = new BitReader(data, blockOffset);

        int mode = 0;
        while (mode < 8 && reader.ReadBits(1) == 0)
            mode++;

        if (mode >= 8)
        {
            r = g = b = a = 0f;
            return;
        }

        int pixelIndex = localY * 4 + localX;

        switch (mode)
        {
            case 0:
                DecodeBC7Mode0(ref reader, pixelIndex, out r, out g, out b, out a);
                break;
            case 1:
                DecodeBC7Mode1(ref reader, pixelIndex, out r, out g, out b, out a);
                break;
            case 2:
                DecodeBC7Mode2(ref reader, pixelIndex, out r, out g, out b, out a);
                break;
            case 3:
                DecodeBC7Mode3(ref reader, pixelIndex, out r, out g, out b, out a);
                break;
            case 4:
                DecodeBC7Mode4(ref reader, pixelIndex, out r, out g, out b, out a);
                break;
            case 5:
                DecodeBC7Mode5(ref reader, pixelIndex, out r, out g, out b, out a);
                break;
            case 6:
                DecodeBC7Mode6(ref reader, pixelIndex, out r, out g, out b, out a);
                break;
            default:
                DecodeBC7Mode7(ref reader, pixelIndex, out r, out g, out b, out a);
                break;
        }
    }

    private static void DecodeBC7Mode0(
        ref BitReader reader,
        int pixelIndex,
        out float r,
        out float g,
        out float b,
        out float a
    )
    {
        int partition = reader.ReadBits(4);

        int r0_0 = reader.ReadBits(4),
            r0_1 = reader.ReadBits(4),
            r0_2 = reader.ReadBits(4);
        int r1_0 = reader.ReadBits(4),
            r1_1 = reader.ReadBits(4),
            r1_2 = reader.ReadBits(4);
        int g0_0 = reader.ReadBits(4),
            g0_1 = reader.ReadBits(4),
            g0_2 = reader.ReadBits(4);
        int g1_0 = reader.ReadBits(4),
            g1_1 = reader.ReadBits(4),
            g1_2 = reader.ReadBits(4);
        int b0_0 = reader.ReadBits(4),
            b0_1 = reader.ReadBits(4),
            b0_2 = reader.ReadBits(4);
        int b1_0 = reader.ReadBits(4),
            b1_1 = reader.ReadBits(4),
            b1_2 = reader.ReadBits(4);

        int p0 = reader.ReadBits(1),
            p1 = reader.ReadBits(1),
            p2 = reader.ReadBits(1);
        int p3 = reader.ReadBits(1),
            p4 = reader.ReadBits(1),
            p5 = reader.ReadBits(1);

        r0_0 = (r0_0 << 1) | p0;
        r1_0 = (r1_0 << 1) | p1;
        r0_1 = (r0_1 << 1) | p2;
        r1_1 = (r1_1 << 1) | p3;
        r0_2 = (r0_2 << 1) | p4;
        r1_2 = (r1_2 << 1) | p5;
        g0_0 = (g0_0 << 1) | p0;
        g1_0 = (g1_0 << 1) | p1;
        g0_1 = (g0_1 << 1) | p2;
        g1_1 = (g1_1 << 1) | p3;
        g0_2 = (g0_2 << 1) | p4;
        g1_2 = (g1_2 << 1) | p5;
        b0_0 = (b0_0 << 1) | p0;
        b1_0 = (b1_0 << 1) | p1;
        b0_1 = (b0_1 << 1) | p2;
        b1_1 = (b1_1 << 1) | p3;
        b0_2 = (b0_2 << 1) | p4;
        b1_2 = (b1_2 << 1) | p5;

        int subset = BC7PartitionTable3[partition * 16 + pixelIndex];
        int anchor0 = 0;
        int anchor1 = BC7AnchorIndex3_1[partition];
        int anchor2 = BC7AnchorIndex3_2[partition];

        int idx = 0;
        for (int i = 0; i < 16; i++)
        {
            int bits = 3;
            if (i == anchor0 || i == anchor1 || i == anchor2)
                bits = 2;
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
            case 1:
                er0 = r0_1;
                er1 = r1_1;
                eg0 = g0_1;
                eg1 = g1_1;
                eb0 = b0_1;
                eb1 = b1_1;
                break;
            case 2:
                er0 = r0_2;
                er1 = r1_2;
                eg0 = g0_2;
                eg1 = g1_2;
                eb0 = b0_2;
                eb1 = b1_2;
                break;
            default:
                er0 = r0_0;
                er1 = r1_0;
                eg0 = g0_0;
                eg1 = g1_0;
                eb0 = b0_0;
                eb1 = b1_0;
                break;
        }

        int w = BC7Weights3[idx];
        r = BC7Interpolate(BC7Unquantize(er0, 5), BC7Unquantize(er1, 5), w) * Byte2Float;
        g = BC7Interpolate(BC7Unquantize(eg0, 5), BC7Unquantize(eg1, 5), w) * Byte2Float;
        b = BC7Interpolate(BC7Unquantize(eb0, 5), BC7Unquantize(eb1, 5), w) * Byte2Float;
        a = 1f;
    }

    private static void DecodeBC7Mode1(
        ref BitReader reader,
        int pixelIndex,
        out float r,
        out float g,
        out float b,
        out float a
    )
    {
        int partition = reader.ReadBits(6);

        int r0_0 = reader.ReadBits(6),
            r0_1 = reader.ReadBits(6);
        int r1_0 = reader.ReadBits(6),
            r1_1 = reader.ReadBits(6);
        int g0_0 = reader.ReadBits(6),
            g0_1 = reader.ReadBits(6);
        int g1_0 = reader.ReadBits(6),
            g1_1 = reader.ReadBits(6);
        int b0_0 = reader.ReadBits(6),
            b0_1 = reader.ReadBits(6);
        int b1_0 = reader.ReadBits(6),
            b1_1 = reader.ReadBits(6);

        int p0 = reader.ReadBits(1),
            p1 = reader.ReadBits(1);

        r0_0 = (r0_0 << 1) | p0;
        r1_0 = (r1_0 << 1) | p0;
        r0_1 = (r0_1 << 1) | p1;
        r1_1 = (r1_1 << 1) | p1;
        g0_0 = (g0_0 << 1) | p0;
        g1_0 = (g1_0 << 1) | p0;
        g0_1 = (g0_1 << 1) | p1;
        g1_1 = (g1_1 << 1) | p1;
        b0_0 = (b0_0 << 1) | p0;
        b1_0 = (b1_0 << 1) | p0;
        b0_1 = (b0_1 << 1) | p1;
        b1_1 = (b1_1 << 1) | p1;

        int subset = BC7PartitionTable2[partition * 16 + pixelIndex];
        int anchor1 = BC7AnchorIndex2_1[partition];

        int idx = 0;
        for (int i = 0; i < 16; i++)
        {
            int bits = 3;
            if (i == 0 || i == anchor1)
                bits = 2;
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
        if (subset == 1)
        {
            er0 = r0_1;
            er1 = r1_1;
            eg0 = g0_1;
            eg1 = g1_1;
            eb0 = b0_1;
            eb1 = b1_1;
        }
        else
        {
            er0 = r0_0;
            er1 = r1_0;
            eg0 = g0_0;
            eg1 = g1_0;
            eb0 = b0_0;
            eb1 = b1_0;
        }

        int w = BC7Weights3[idx];
        r = BC7Interpolate(BC7Unquantize(er0, 7), BC7Unquantize(er1, 7), w) * Byte2Float;
        g = BC7Interpolate(BC7Unquantize(eg0, 7), BC7Unquantize(eg1, 7), w) * Byte2Float;
        b = BC7Interpolate(BC7Unquantize(eb0, 7), BC7Unquantize(eb1, 7), w) * Byte2Float;
        a = 1f;
    }

    private static void DecodeBC7Mode2(
        ref BitReader reader,
        int pixelIndex,
        out float r,
        out float g,
        out float b,
        out float a
    )
    {
        int partition = reader.ReadBits(6);

        int r0_0 = reader.ReadBits(5),
            r0_1 = reader.ReadBits(5),
            r0_2 = reader.ReadBits(5);
        int r1_0 = reader.ReadBits(5),
            r1_1 = reader.ReadBits(5),
            r1_2 = reader.ReadBits(5);
        int g0_0 = reader.ReadBits(5),
            g0_1 = reader.ReadBits(5),
            g0_2 = reader.ReadBits(5);
        int g1_0 = reader.ReadBits(5),
            g1_1 = reader.ReadBits(5),
            g1_2 = reader.ReadBits(5);
        int b0_0 = reader.ReadBits(5),
            b0_1 = reader.ReadBits(5),
            b0_2 = reader.ReadBits(5);
        int b1_0 = reader.ReadBits(5),
            b1_1 = reader.ReadBits(5),
            b1_2 = reader.ReadBits(5);

        int subset = BC7PartitionTable3[partition * 16 + pixelIndex];
        int anchor0 = 0;
        int anchor1 = BC7AnchorIndex3_1[partition];
        int anchor2 = BC7AnchorIndex3_2[partition];

        int idx = 0;
        for (int i = 0; i < 16; i++)
        {
            int bits = 2;
            if (i == anchor0 || i == anchor1 || i == anchor2)
                bits = 1;
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
            case 1:
                er0 = r0_1;
                er1 = r1_1;
                eg0 = g0_1;
                eg1 = g1_1;
                eb0 = b0_1;
                eb1 = b1_1;
                break;
            case 2:
                er0 = r0_2;
                er1 = r1_2;
                eg0 = g0_2;
                eg1 = g1_2;
                eb0 = b0_2;
                eb1 = b1_2;
                break;
            default:
                er0 = r0_0;
                er1 = r1_0;
                eg0 = g0_0;
                eg1 = g1_0;
                eb0 = b0_0;
                eb1 = b1_0;
                break;
        }

        int w = BC7Weights2[idx];
        r = BC7Interpolate(BC7Unquantize(er0, 5), BC7Unquantize(er1, 5), w) * Byte2Float;
        g = BC7Interpolate(BC7Unquantize(eg0, 5), BC7Unquantize(eg1, 5), w) * Byte2Float;
        b = BC7Interpolate(BC7Unquantize(eb0, 5), BC7Unquantize(eb1, 5), w) * Byte2Float;
        a = 1f;
    }

    private static void DecodeBC7Mode3(
        ref BitReader reader,
        int pixelIndex,
        out float r,
        out float g,
        out float b,
        out float a
    )
    {
        int partition = reader.ReadBits(6);

        int r0_0 = reader.ReadBits(7),
            r0_1 = reader.ReadBits(7);
        int r1_0 = reader.ReadBits(7),
            r1_1 = reader.ReadBits(7);
        int g0_0 = reader.ReadBits(7),
            g0_1 = reader.ReadBits(7);
        int g1_0 = reader.ReadBits(7),
            g1_1 = reader.ReadBits(7);
        int b0_0 = reader.ReadBits(7),
            b0_1 = reader.ReadBits(7);
        int b1_0 = reader.ReadBits(7),
            b1_1 = reader.ReadBits(7);

        int p0 = reader.ReadBits(1),
            p1 = reader.ReadBits(1);
        int p2 = reader.ReadBits(1),
            p3 = reader.ReadBits(1);

        r0_0 = (r0_0 << 1) | p0;
        r1_0 = (r1_0 << 1) | p1;
        r0_1 = (r0_1 << 1) | p2;
        r1_1 = (r1_1 << 1) | p3;
        g0_0 = (g0_0 << 1) | p0;
        g1_0 = (g1_0 << 1) | p1;
        g0_1 = (g0_1 << 1) | p2;
        g1_1 = (g1_1 << 1) | p3;
        b0_0 = (b0_0 << 1) | p0;
        b1_0 = (b1_0 << 1) | p1;
        b0_1 = (b0_1 << 1) | p2;
        b1_1 = (b1_1 << 1) | p3;

        int subset = BC7PartitionTable2[partition * 16 + pixelIndex];
        int anchor1 = BC7AnchorIndex2_1[partition];

        int idx = 0;
        for (int i = 0; i < 16; i++)
        {
            int bits = 2;
            if (i == 0 || i == anchor1)
                bits = 1;
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
        if (subset == 1)
        {
            er0 = r0_1;
            er1 = r1_1;
            eg0 = g0_1;
            eg1 = g1_1;
            eb0 = b0_1;
            eb1 = b1_1;
        }
        else
        {
            er0 = r0_0;
            er1 = r1_0;
            eg0 = g0_0;
            eg1 = g1_0;
            eb0 = b0_0;
            eb1 = b1_0;
        }

        int w = BC7Weights2[idx];
        r = BC7Interpolate(BC7Unquantize(er0, 8), BC7Unquantize(er1, 8), w) * Byte2Float;
        g = BC7Interpolate(BC7Unquantize(eg0, 8), BC7Unquantize(eg1, 8), w) * Byte2Float;
        b = BC7Interpolate(BC7Unquantize(eb0, 8), BC7Unquantize(eb1, 8), w) * Byte2Float;
        a = 1f;
    }

    private static void DecodeBC7Mode4(
        ref BitReader reader,
        int pixelIndex,
        out float r,
        out float g,
        out float b,
        out float a
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

        int idx2 = 0,
            idx3 = 0;
        for (int i = 0; i < 16; i++)
        {
            int bits = (i == 0) ? 1 : 2;
            int val = reader.ReadBits(bits);
            if (i == pixelIndex)
                idx2 = val;
        }
        for (int i = 0; i < 16; i++)
        {
            int bits = (i == 0) ? 2 : 3;
            int val = reader.ReadBits(bits);
            if (i == pixelIndex)
                idx3 = val;
        }

        int colorIdx = idxMode == 0 ? idx2 : idx3;
        int alphaIdx = idxMode == 0 ? idx3 : idx2;
        int colorWeight = idxMode == 0 ? BC7Weights2[colorIdx] : BC7Weights3[colorIdx];
        int alphaWeight = idxMode == 0 ? BC7Weights3[alphaIdx] : BC7Weights2[alphaIdx];

        int ri = BC7Interpolate(BC7Unquantize(r0, 5), BC7Unquantize(r1, 5), colorWeight);
        int gi = BC7Interpolate(BC7Unquantize(g0, 5), BC7Unquantize(g1, 5), colorWeight);
        int bi = BC7Interpolate(BC7Unquantize(b0, 5), BC7Unquantize(b1, 5), colorWeight);
        int ai = BC7Interpolate(BC7Unquantize(a0, 6), BC7Unquantize(a1, 6), alphaWeight);

        ApplyBC7Rotation(rotation, ref ri, ref gi, ref bi, ref ai);
        r = ri * Byte2Float;
        g = gi * Byte2Float;
        b = bi * Byte2Float;
        a = ai * Byte2Float;
    }

    private static void DecodeBC7Mode5(
        ref BitReader reader,
        int pixelIndex,
        out float r,
        out float g,
        out float b,
        out float a
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

        int colorIdx = 0,
            alphaIdx = 0;
        for (int i = 0; i < 16; i++)
        {
            int bits = (i == 0) ? 1 : 2;
            int val = reader.ReadBits(bits);
            if (i == pixelIndex)
                colorIdx = val;
        }
        for (int i = 0; i < 16; i++)
        {
            int bits = (i == 0) ? 1 : 2;
            int val = reader.ReadBits(bits);
            if (i == pixelIndex)
                alphaIdx = val;
        }

        int ri = BC7Interpolate(BC7Unquantize(r0, 7), BC7Unquantize(r1, 7), BC7Weights2[colorIdx]);
        int gi = BC7Interpolate(BC7Unquantize(g0, 7), BC7Unquantize(g1, 7), BC7Weights2[colorIdx]);
        int bi = BC7Interpolate(BC7Unquantize(b0, 7), BC7Unquantize(b1, 7), BC7Weights2[colorIdx]);
        int ai = BC7Interpolate(BC7Unquantize(a0, 8), BC7Unquantize(a1, 8), BC7Weights2[alphaIdx]);

        ApplyBC7Rotation(rotation, ref ri, ref gi, ref bi, ref ai);
        r = ri * Byte2Float;
        g = gi * Byte2Float;
        b = bi * Byte2Float;
        a = ai * Byte2Float;
    }

    private static void DecodeBC7Mode6(
        ref BitReader reader,
        int pixelIndex,
        out float r,
        out float g,
        out float b,
        out float a
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

        int p0 = reader.ReadBits(1),
            p1 = reader.ReadBits(1);
        r0 = (r0 << 1) | p0;
        r1 = (r1 << 1) | p1;
        g0 = (g0 << 1) | p0;
        g1 = (g1 << 1) | p1;
        b0 = (b0 << 1) | p0;
        b1 = (b1 << 1) | p1;
        a0 = (a0 << 1) | p0;
        a1 = (a1 << 1) | p1;

        int idx = 0;
        for (int i = 0; i < 16; i++)
        {
            int bits = (i == 0) ? 3 : 4;
            int val = reader.ReadBits(bits);
            if (i == pixelIndex)
                idx = val;
        }

        int w = BC7Weights4[idx];
        r = BC7Interpolate(BC7Unquantize(r0, 8), BC7Unquantize(r1, 8), w) * Byte2Float;
        g = BC7Interpolate(BC7Unquantize(g0, 8), BC7Unquantize(g1, 8), w) * Byte2Float;
        b = BC7Interpolate(BC7Unquantize(b0, 8), BC7Unquantize(b1, 8), w) * Byte2Float;
        a = BC7Interpolate(BC7Unquantize(a0, 8), BC7Unquantize(a1, 8), w) * Byte2Float;
    }

    private static void DecodeBC7Mode7(
        ref BitReader reader,
        int pixelIndex,
        out float r,
        out float g,
        out float b,
        out float a
    )
    {
        int partition = reader.ReadBits(6);

        int r0_0 = reader.ReadBits(5),
            r0_1 = reader.ReadBits(5);
        int r1_0 = reader.ReadBits(5),
            r1_1 = reader.ReadBits(5);
        int g0_0 = reader.ReadBits(5),
            g0_1 = reader.ReadBits(5);
        int g1_0 = reader.ReadBits(5),
            g1_1 = reader.ReadBits(5);
        int b0_0 = reader.ReadBits(5),
            b0_1 = reader.ReadBits(5);
        int b1_0 = reader.ReadBits(5),
            b1_1 = reader.ReadBits(5);
        int a0_0 = reader.ReadBits(5),
            a0_1 = reader.ReadBits(5);
        int a1_0 = reader.ReadBits(5),
            a1_1 = reader.ReadBits(5);

        int p0 = reader.ReadBits(1),
            p1 = reader.ReadBits(1);
        int p2 = reader.ReadBits(1),
            p3 = reader.ReadBits(1);

        r0_0 = (r0_0 << 1) | p0;
        r1_0 = (r1_0 << 1) | p1;
        r0_1 = (r0_1 << 1) | p2;
        r1_1 = (r1_1 << 1) | p3;
        g0_0 = (g0_0 << 1) | p0;
        g1_0 = (g1_0 << 1) | p1;
        g0_1 = (g0_1 << 1) | p2;
        g1_1 = (g1_1 << 1) | p3;
        b0_0 = (b0_0 << 1) | p0;
        b1_0 = (b1_0 << 1) | p1;
        b0_1 = (b0_1 << 1) | p2;
        b1_1 = (b1_1 << 1) | p3;
        a0_0 = (a0_0 << 1) | p0;
        a1_0 = (a1_0 << 1) | p1;
        a0_1 = (a0_1 << 1) | p2;
        a1_1 = (a1_1 << 1) | p3;

        int subset = BC7PartitionTable2[partition * 16 + pixelIndex];
        int anchor1 = BC7AnchorIndex2_1[partition];

        int idx = 0;
        for (int i = 0; i < 16; i++)
        {
            int bits = 2;
            if (i == 0 || i == anchor1)
                bits = 1;
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
        if (subset == 1)
        {
            er0 = r0_1;
            er1 = r1_1;
            eg0 = g0_1;
            eg1 = g1_1;
            eb0 = b0_1;
            eb1 = b1_1;
            ea0 = a0_1;
            ea1 = a1_1;
        }
        else
        {
            er0 = r0_0;
            er1 = r1_0;
            eg0 = g0_0;
            eg1 = g1_0;
            eb0 = b0_0;
            eb1 = b1_0;
            ea0 = a0_0;
            ea1 = a1_0;
        }

        int w = BC7Weights2[idx];
        r = BC7Interpolate(BC7Unquantize(er0, 6), BC7Unquantize(er1, 6), w) * Byte2Float;
        g = BC7Interpolate(BC7Unquantize(eg0, 6), BC7Unquantize(eg1, 6), w) * Byte2Float;
        b = BC7Interpolate(BC7Unquantize(eb0, 6), BC7Unquantize(eb1, 6), w) * Byte2Float;
        a = BC7Interpolate(BC7Unquantize(ea0, 6), BC7Unquantize(ea1, 6), w) * Byte2Float;
    }

    private static void ApplyBC7Rotation(int rotation, ref int r, ref int g, ref int b, ref int a)
    {
        int temp;
        switch (rotation)
        {
            case 1:
                temp = a;
                a = r;
                r = temp;
                break;
            case 2:
                temp = a;
                a = g;
                g = temp;
                break;
            case 3:
                temp = a;
                a = b;
                b = temp;
                break;
        }
    }

    // ---- BC6H ----

    private struct BC6HModeInfo
    {
        public int numSubsets;
        public int partitionBits;
        public int endpointBits;
        public int deltaBitsR,
            deltaBitsG,
            deltaBitsB;
        public bool transformed;
        public int indexBits;
    }

    private static readonly BC6HModeInfo[] BC6HModes =
    {
        new BC6HModeInfo
        {
            numSubsets = 2,
            partitionBits = 5,
            endpointBits = 10,
            deltaBitsR = 5,
            deltaBitsG = 5,
            deltaBitsB = 5,
            transformed = true,
            indexBits = 3,
        },
        new BC6HModeInfo
        {
            numSubsets = 2,
            partitionBits = 5,
            endpointBits = 7,
            deltaBitsR = 6,
            deltaBitsG = 6,
            deltaBitsB = 6,
            transformed = true,
            indexBits = 3,
        },
        new BC6HModeInfo
        {
            numSubsets = 2,
            partitionBits = 5,
            endpointBits = 11,
            deltaBitsR = 5,
            deltaBitsG = 4,
            deltaBitsB = 4,
            transformed = true,
            indexBits = 3,
        },
        new BC6HModeInfo
        {
            numSubsets = 2,
            partitionBits = 5,
            endpointBits = 11,
            deltaBitsR = 4,
            deltaBitsG = 5,
            deltaBitsB = 4,
            transformed = true,
            indexBits = 3,
        },
        new BC6HModeInfo
        {
            numSubsets = 2,
            partitionBits = 5,
            endpointBits = 11,
            deltaBitsR = 4,
            deltaBitsG = 4,
            deltaBitsB = 5,
            transformed = true,
            indexBits = 3,
        },
        new BC6HModeInfo
        {
            numSubsets = 2,
            partitionBits = 5,
            endpointBits = 9,
            deltaBitsR = 5,
            deltaBitsG = 5,
            deltaBitsB = 5,
            transformed = true,
            indexBits = 3,
        },
        new BC6HModeInfo
        {
            numSubsets = 2,
            partitionBits = 5,
            endpointBits = 8,
            deltaBitsR = 6,
            deltaBitsG = 5,
            deltaBitsB = 5,
            transformed = true,
            indexBits = 3,
        },
        new BC6HModeInfo
        {
            numSubsets = 2,
            partitionBits = 5,
            endpointBits = 8,
            deltaBitsR = 5,
            deltaBitsG = 6,
            deltaBitsB = 5,
            transformed = true,
            indexBits = 3,
        },
        new BC6HModeInfo
        {
            numSubsets = 2,
            partitionBits = 5,
            endpointBits = 8,
            deltaBitsR = 5,
            deltaBitsG = 5,
            deltaBitsB = 6,
            transformed = true,
            indexBits = 3,
        },
        new BC6HModeInfo
        {
            numSubsets = 2,
            partitionBits = 5,
            endpointBits = 6,
            deltaBitsR = 6,
            deltaBitsG = 6,
            deltaBitsB = 6,
            transformed = false,
            indexBits = 3,
        },
        new BC6HModeInfo
        {
            numSubsets = 1,
            partitionBits = 0,
            endpointBits = 10,
            deltaBitsR = 10,
            deltaBitsG = 10,
            deltaBitsB = 10,
            transformed = false,
            indexBits = 4,
        },
        new BC6HModeInfo
        {
            numSubsets = 1,
            partitionBits = 0,
            endpointBits = 11,
            deltaBitsR = 9,
            deltaBitsG = 9,
            deltaBitsB = 9,
            transformed = true,
            indexBits = 4,
        },
        new BC6HModeInfo
        {
            numSubsets = 1,
            partitionBits = 0,
            endpointBits = 12,
            deltaBitsR = 8,
            deltaBitsG = 8,
            deltaBitsB = 8,
            transformed = true,
            indexBits = 4,
        },
        new BC6HModeInfo
        {
            numSubsets = 1,
            partitionBits = 0,
            endpointBits = 16,
            deltaBitsR = 4,
            deltaBitsG = 4,
            deltaBitsB = 4,
            transformed = true,
            indexBits = 4,
        },
    };

    // csharpier-ignore-start
    private static readonly byte[] BC6HPartitionTable =
    {
        0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1,
        0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1,
        0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1,
        0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1,
        0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 1,
        0, 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1,
        0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1,
        0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 1,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1,
        0, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1,
        0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
        0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1,
        0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1, 1,
        0, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0,
        0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0,
        0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0,
        0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0,
        0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0,
        0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0,
        0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0,
        0, 0, 1, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 0, 0,
        0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0,
        0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0,
        0, 1, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0,
        0, 0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0, 0,
    };

    private static readonly byte[] BC6HAnchorIndex =
    {
        15, 15, 15, 15, 15, 15, 15, 15,
        15, 15, 15, 15, 15, 15, 15, 15,
        15,  2,  8,  2,  2,  8,  8, 15,
         2,  8,  2,  2,  8,  8,  2,  2,
    };
    // csharpier-ignore-end

    private static int BC6HGetMode(NativeArray<byte> data, int offset)
    {
        int b0 = data[offset];
        if ((b0 & 0x03) == 0x00)
            return 0;
        if ((b0 & 0x03) == 0x01)
            return 1;
        if ((b0 & 0x1F) == 0x02)
            return 2;
        if ((b0 & 0x1F) == 0x06)
            return 3;
        if ((b0 & 0x1F) == 0x0A)
            return 4;
        if ((b0 & 0x1F) == 0x0E)
            return 5;
        if ((b0 & 0x1F) == 0x12)
            return 6;
        if ((b0 & 0x1F) == 0x16)
            return 7;
        if ((b0 & 0x1F) == 0x1A)
            return 8;
        if ((b0 & 0x1F) == 0x1E)
            return 9;
        if ((b0 & 0x1F) == 0x03)
            return 10;
        if ((b0 & 0x1F) == 0x07)
            return 11;
        if ((b0 & 0x1F) == 0x0B)
            return 12;
        if ((b0 & 0x1F) == 0x0F)
            return 13;
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SignExtend(int val, int bits)
    {
        int shift = 32 - bits;
        return (val << shift) >> shift;
    }

    private static float HalfToFloat(int h)
    {
        int sign = (h >> 15) & 1;
        int exp = (h >> 10) & 0x1F;
        int mantissa = h & 0x3FF;

        if (exp == 0)
        {
            if (mantissa == 0)
                return sign == 1 ? -0f : 0f;
            float f = mantissa / 1024f * (1f / 16384f);
            return sign == 1 ? -f : f;
        }
        if (exp == 31)
        {
            return mantissa == 0
                ? (sign == 1 ? float.NegativeInfinity : float.PositiveInfinity)
                : float.NaN;
        }

        float result = (1f + mantissa / 1024f) * (float)Math.Pow(2f, exp - 15);
        return sign == 1 ? -result : result;
    }

    private static int UnquantizeBC6H(int val, int bits, bool signed)
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

    private static float FinishUnquantizeBC6H(int val, bool signed)
    {
        if (signed)
        {
            int s = 0;
            if (val < 0)
            {
                s = 0x8000;
                val = -val;
            }
            int h = s | ((val * 31) >> 5);
            return HalfToFloat(h);
        }
        else
        {
            int h = (val * 31) >> 6;
            return HalfToFloat(h);
        }
    }

    internal static void DecodeBC6HPixel(
        NativeArray<byte> data,
        int blockOffset,
        int localX,
        int localY,
        bool signed,
        out float r,
        out float g,
        out float b
    )
    {
        int mode = BC6HGetMode(data, blockOffset);
        if (mode < 0 || mode >= 14)
        {
            r = g = b = 0f;
            return;
        }

        var modeInfo = BC6HModes[mode];

        // Endpoints: e0=subset0_lo, e1=subset0_hi, e2=subset1_lo, e3=subset1_hi
        int e0r,
            e0g,
            e0b,
            e1r,
            e1g,
            e1b,
            e2r,
            e2g,
            e2b,
            e3r,
            e3g,
            e3b;
        int partition;

        DecodeBC6HEndpoints(
            data,
            blockOffset,
            mode,
            modeInfo,
            out e0r,
            out e0g,
            out e0b,
            out e1r,
            out e1g,
            out e1b,
            out e2r,
            out e2g,
            out e2b,
            out e3r,
            out e3g,
            out e3b,
            out partition
        );

        if (modeInfo.transformed)
        {
            e1r = SignExtend(e1r, modeInfo.deltaBitsR) + e0r;
            e1g = SignExtend(e1g, modeInfo.deltaBitsG) + e0g;
            e1b = SignExtend(e1b, modeInfo.deltaBitsB) + e0b;

            if (modeInfo.numSubsets == 2)
            {
                e2r = SignExtend(e2r, modeInfo.deltaBitsR) + e0r;
                e2g = SignExtend(e2g, modeInfo.deltaBitsG) + e0g;
                e2b = SignExtend(e2b, modeInfo.deltaBitsB) + e0b;
                e3r = SignExtend(e3r, modeInfo.deltaBitsR) + e0r;
                e3g = SignExtend(e3g, modeInfo.deltaBitsG) + e0g;
                e3b = SignExtend(e3b, modeInfo.deltaBitsB) + e0b;
            }

            int mask = (1 << modeInfo.endpointBits) - 1;
            if (signed)
            {
                e0r = SignExtend(e0r & mask, modeInfo.endpointBits);
                e0g = SignExtend(e0g & mask, modeInfo.endpointBits);
                e0b = SignExtend(e0b & mask, modeInfo.endpointBits);
                e1r = SignExtend(e1r & mask, modeInfo.endpointBits);
                e1g = SignExtend(e1g & mask, modeInfo.endpointBits);
                e1b = SignExtend(e1b & mask, modeInfo.endpointBits);
                e2r = SignExtend(e2r & mask, modeInfo.endpointBits);
                e2g = SignExtend(e2g & mask, modeInfo.endpointBits);
                e2b = SignExtend(e2b & mask, modeInfo.endpointBits);
                e3r = SignExtend(e3r & mask, modeInfo.endpointBits);
                e3g = SignExtend(e3g & mask, modeInfo.endpointBits);
                e3b = SignExtend(e3b & mask, modeInfo.endpointBits);
            }
            else
            {
                e0r &= mask;
                e0g &= mask;
                e0b &= mask;
                e1r &= mask;
                e1g &= mask;
                e1b &= mask;
                e2r &= mask;
                e2g &= mask;
                e2b &= mask;
                e3r &= mask;
                e3g &= mask;
                e3b &= mask;
            }
        }

        e0r = UnquantizeBC6H(e0r, modeInfo.endpointBits, signed);
        e0g = UnquantizeBC6H(e0g, modeInfo.endpointBits, signed);
        e0b = UnquantizeBC6H(e0b, modeInfo.endpointBits, signed);
        e1r = UnquantizeBC6H(e1r, modeInfo.endpointBits, signed);
        e1g = UnquantizeBC6H(e1g, modeInfo.endpointBits, signed);
        e1b = UnquantizeBC6H(e1b, modeInfo.endpointBits, signed);
        e2r = UnquantizeBC6H(e2r, modeInfo.endpointBits, signed);
        e2g = UnquantizeBC6H(e2g, modeInfo.endpointBits, signed);
        e2b = UnquantizeBC6H(e2b, modeInfo.endpointBits, signed);
        e3r = UnquantizeBC6H(e3r, modeInfo.endpointBits, signed);
        e3g = UnquantizeBC6H(e3g, modeInfo.endpointBits, signed);
        e3b = UnquantizeBC6H(e3b, modeInfo.endpointBits, signed);

        int pixelIndex = localY * 4 + localX;
        int subset;
        if (modeInfo.numSubsets == 2)
            subset = BC6HPartitionTable[partition * 16 + pixelIndex];
        else
            subset = 0;

        int anchor0 = 0;
        int anchor1 = modeInfo.numSubsets == 2 ? BC6HAnchorIndex[partition] : -1;

        int indexStart = 128 - (modeInfo.numSubsets == 2 ? 46 : 63);
        var idxReader = new BitReader(data, blockOffset);
        idxReader.SkipBits(indexStart);

        int idx = 0;
        for (int i = 0; i < 16; i++)
        {
            int bits = modeInfo.indexBits;
            if (i == anchor0 || i == anchor1)
                bits--;
            int val = idxReader.ReadBits(bits);
            if (i == pixelIndex)
                idx = val;
        }

        int lor,
            log,
            lob,
            hir,
            hig,
            hib;
        if (subset == 0)
        {
            lor = e0r;
            log = e0g;
            lob = e0b;
            hir = e1r;
            hig = e1g;
            hib = e1b;
        }
        else
        {
            lor = e2r;
            log = e2g;
            lob = e2b;
            hir = e3r;
            hig = e3g;
            hib = e3b;
        }

        int w = modeInfo.indexBits == 3 ? BC7Weights3[idx] : BC7Weights4[idx];

        int finalR = ((64 - w) * lor + w * hir + 32) >> 6;
        int finalG = ((64 - w) * log + w * hig + 32) >> 6;
        int finalB = ((64 - w) * lob + w * hib + 32) >> 6;

        r = FinishUnquantizeBC6H(finalR, signed);
        g = FinishUnquantizeBC6H(finalG, signed);
        b = FinishUnquantizeBC6H(finalB, signed);
    }

    private static void DecodeBC6HEndpoints(
        NativeArray<byte> data,
        int offset,
        int mode,
        BC6HModeInfo modeInfo,
        out int e0r,
        out int e0g,
        out int e0b,
        out int e1r,
        out int e1g,
        out int e1b,
        out int e2r,
        out int e2g,
        out int e2b,
        out int e3r,
        out int e3g,
        out int e3b,
        out int partition
    )
    {
        e0r = e0g = e0b = e1r = e1g = e1b = 0;
        e2r = e2g = e2b = e3r = e3g = e3b = 0;
        partition = 0;

        int GetBit(int pos) => (data[offset + (pos >> 3)] >> (pos & 7)) & 1;
        int GetBits(int start, int count)
        {
            int val = 0;
            for (int i = 0; i < count; i++)
                val |= GetBit(start + i) << i;
            return val;
        }

        switch (mode)
        {
            case 0:
            {
                int gy4 = GetBit(2);
                int by4 = GetBit(3);
                int bz4 = GetBit(4);
                e0r = GetBits(5, 10);
                e0g = GetBits(15, 10);
                e0b = GetBits(25, 10);
                e1r = GetBits(35, 5);
                e1g = GetBits(53, 5);
                e1b = GetBits(64, 5);
                e2r = GetBits(44, 5);
                e2g = GetBits(49, 4) | (gy4 << 4);
                e2b = GetBits(60, 4) | (by4 << 4);
                int gz4 = GetBit(59);
                int gz30 = GetBits(40, 4);
                e3r = GetBits(72, 5);
                e3g = gz30 | (gz4 << 4);
                int bz0 = GetBit(58);
                int bz31 = GetBits(69, 3);
                e3b = bz0 | (bz31 << 1) | (bz4 << 4);
                partition = GetBits(77, 5);
                break;
            }
            case 1:
            {
                int gy5 = GetBit(2);
                e0r = GetBits(5, 7);
                e0g = GetBits(15, 7);
                e0b = GetBits(24, 7);
                e1r = GetBits(34, 6);
                e1g = GetBits(51, 6);
                e1b = GetBits(61, 6);
                e2r = GetBits(41, 6);
                e2g = GetBits(47, 4) | (GetBit(40) << 4) | (gy5 << 5);
                e2b = GetBits(67, 4) | (GetBit(14) << 4) | (GetBit(22) << 5);
                e3r = GetBits(71, 6);
                e3g = GetBits(57, 4) | (GetBit(3) << 4) | (GetBit(4) << 5);
                e3b =
                    GetBit(12)
                    | (GetBit(13) << 1)
                    | (GetBit(23) << 2)
                    | (GetBit(31) << 3)
                    | (GetBit(33) << 4)
                    | (GetBit(32) << 5);
                partition = GetBits(77, 5);
                break;
            }
            default:
            {
                DecodeBC6HEndpointsGeneric(
                    data,
                    offset,
                    mode,
                    modeInfo,
                    out e0r,
                    out e0g,
                    out e0b,
                    out e1r,
                    out e1g,
                    out e1b,
                    out e2r,
                    out e2g,
                    out e2b,
                    out e3r,
                    out e3g,
                    out e3b,
                    out partition
                );
                break;
            }
        }
    }

    private static void DecodeBC6HEndpointsGeneric(
        NativeArray<byte> data,
        int offset,
        int mode,
        BC6HModeInfo modeInfo,
        out int e0r,
        out int e0g,
        out int e0b,
        out int e1r,
        out int e1g,
        out int e1b,
        out int e2r,
        out int e2g,
        out int e2b,
        out int e3r,
        out int e3g,
        out int e3b,
        out int partition
    )
    {
        var reader = new BitReader(data, offset);
        if (mode <= 1)
            reader.SkipBits(2);
        else
            reader.SkipBits(5);

        int epBits = modeInfo.endpointBits;
        int dBitsR = modeInfo.deltaBitsR;
        int dBitsG = modeInfo.deltaBitsG;
        int dBitsB = modeInfo.deltaBitsB;

        e0r = reader.ReadBits(epBits);
        e0g = reader.ReadBits(epBits);
        e0b = reader.ReadBits(epBits);

        if (modeInfo.numSubsets == 1)
        {
            e1r = reader.ReadBits(dBitsR);
            e1g = reader.ReadBits(dBitsG);
            e1b = reader.ReadBits(dBitsB);
            e2r = e2g = e2b = e3r = e3g = e3b = 0;
            partition = 0;
        }
        else
        {
            e1r = reader.ReadBits(dBitsR);
            e1g = reader.ReadBits(dBitsG);
            e1b = reader.ReadBits(dBitsB);
            e2r = reader.ReadBits(dBitsR);
            e2g = reader.ReadBits(dBitsG);
            e2b = reader.ReadBits(dBitsB);
            e3r = reader.ReadBits(dBitsR);
            e3g = reader.ReadBits(dBitsG);
            e3b = reader.ReadBits(dBitsB);
            partition = reader.ReadBits(5);
        }
    }
}
