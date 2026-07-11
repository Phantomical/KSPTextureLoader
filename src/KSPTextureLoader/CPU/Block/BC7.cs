using KSPTextureLoader.Utils;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using UnityEngine;
using static Unity.Burst.Intrinsics.X86;

namespace KSPTextureLoader.CPU.Block;

/// <summary>
/// SIMD-accelerated BC7 block decoder.
///
/// The block is treated as a 128-bit little-endian value split into
/// <c>lo</c> (bits 0..63) and <c>hi</c> (bits 64..127). Endpoints and p-bits are
/// parsed with a small scalar bit reader (identical to the reference decoder in
/// <c>CPU/Format/BC7.cs</c>), and the 16 per-pixel palette indices are pulled out
/// of the block with BMI2 <c>pext</c>/<c>pdep</c>:
///   * <c>pext</c> pulls the variable-width contiguous index run out of the
///     (possibly lo/hi-straddling) 128-bit value into a packed integer, and
///   * <c>pdep</c> scatters that packed run so each index lands right-aligned in
///     its own byte lane (anchor pixels, which carry one fewer index bit, get a
///     zero MSB for free).
/// Final byte->float conversion of the 16 RGBA pixels is done with AVX2.
///
/// Every intrinsic path is guarded and has a scalar fallback that produces
/// bit-identical results.
/// </summary>
[BurstCompile]
internal static class BC7
{
    #region Lookup tables and constants

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

    // Vector-constant forms of the interpolation weight tables, laid out so lane i
    // holds table[i]. A single pshufb (Ssse3.shuffle_epi8) then translates 16
    // in-range indices to their 16 weights at once. Palette indices are always in
    // range (0..15 for Weights4, 0..7 for Weights3, 0..3 for Weights2), so the high
    // bit of every shuffle-control lane is clear and pshufb never zeroes a lane.
    // Unused high lanes are filled with 0 and are never selected. set_epi8 takes
    // e15..e0 (e0 -> lane 0), so the argument order below is reversed from the array.
    static readonly v128 Weights2Vec = Sse2.set_epi8(
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 64, 43, 21, 0
    );
    static readonly v128 Weights3Vec = Sse2.set_epi8(
        0, 0, 0, 0, 0, 0, 0, 0, 64, 55, 46, 37, 27, 18, 9, 0
    );
    static readonly v128 Weights4Vec = Sse2.set_epi8(
        64, 60, 55, 51, 47, 43, 38, 34, 30, 26, 21, 17, 13, 9, 4, 0
    );
    // csharpier-ignore-end

    #endregion

    #region Public API

    internal static unsafe Color32 DecodePixel(ulong lo, ulong hi, int pixelIndex)
    {
        byte* rgba = stackalloc byte[64];
        DecodeBytes(lo, hi, rgba);
        int i = pixelIndex * 4;
        return new Color32(rgba[i + 0], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }

    internal static unsafe FixedArray16<Color> DecodeBlock(ulong lo, ulong hi)
    {
        byte* rgba = stackalloc byte[64];
        DecodeBytes(lo, hi, rgba);
        return BytesToColors(rgba);
    }

    [BurstCompile]
    internal static unsafe void DecodeBlock(ulong lo, ulong hi, Color32* colors) =>
        DecodeBytes(lo, hi, (byte*)colors);

    #endregion

    #region Bit reader (scalar header parse — matches the reference decoder)

    struct BitReader
    {
        readonly ulong lo;
        readonly ulong hi;
        int bitPos;

        public BitReader(ulong lo, ulong hi)
        {
            this.lo = lo;
            this.hi = hi;
            bitPos = 0;
        }

        public int BitPos => bitPos;

        public int ReadBits(int count)
        {
            int result;
            int bitIdx = bitPos & 63;
            ulong mask = (1ul << count) - 1;

            if (bitPos < 64)
            {
                result = (int)((lo >> bitIdx) & mask);
                if (bitIdx + count > 64)
                {
                    int loBits = 64 - bitIdx;
                    result |= (int)(hi & ((1ul << (count - loBits)) - 1)) << loBits;
                }
            }
            else
            {
                result = (int)((hi >> bitIdx) & mask);
            }

            bitPos += count;
            return result;
        }

        public void SkipBits(int count) => bitPos += count;
    }

    static int Interpolate(int e0, int e1, int weight) =>
        (e0 * (64 - weight) + e1 * weight + 32) >> 6;

    static int Unquantize(int val, int bits)
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

    static int CountTrailingZeros(byte b)
    {
        if (Bmi1.IsBmi1Supported)
            return (int)Bmi1.tzcnt_u32((uint)b | 0x100);

        if (b == 0)
            return 8;
        int n = 0;
        while ((b & 1) == 0)
        {
            b >>= 1;
            n++;
        }
        return n;
    }

    #endregion

    #region Bit-run extraction / index scatter (BMI2 pext/pdep + scalar fallback)

    /// <summary>
    /// Reads <paramref name="count"/> bits (LSB-first) starting at absolute bit
    /// <paramref name="start"/> from the 128-bit value (lo|hi&lt;&lt;64). count &lt;= 64.
    /// </summary>
    static ulong ReadWindow(ulong lo, ulong hi, int start, int count)
    {
        if (count == 0)
            return 0;
        ulong shifted;
        if (start == 0)
            shifted = lo;
        else if (start < 64)
            shifted = (lo >> start) | (hi << (64 - start));
        else
            shifted = hi >> (start - 64);
        ulong mask = count >= 64 ? ~0ul : ((1ul << count) - 1);
        return shifted & mask;
    }

    /// <summary>
    /// Pulls the contiguous <paramref name="count"/>-bit run at <paramref name="start"/>
    /// out of the 128-bit value into a packed integer. Uses <c>pext</c> across the
    /// lo/hi boundary when BMI2 is available, otherwise a funnel shift. count &lt;= 64.
    /// </summary>
    static ulong PullRun(ulong lo, ulong hi, int start, int count)
    {
        if (count == 0)
            return 0;

        if (Bmi2.IsBmi2Supported)
        {
            if (start >= 64)
            {
                ulong mask = count >= 64 ? ~0ul : ((1ul << count) - 1);
                return Bmi2.pext_u64(hi, mask << (start - 64));
            }

            int loBits = 64 - start;
            if (count <= loBits)
            {
                ulong mask = ((count >= 64 ? ~0ul : ((1ul << count) - 1)) << start);
                return Bmi2.pext_u64(lo, mask);
            }

            ulong loMask = ~0ul << start; // bits [start, 64)
            ulong loPart = Bmi2.pext_u64(lo, loMask);
            int rem = count - loBits;
            ulong hiMask = rem >= 64 ? ~0ul : ((1ul << rem) - 1);
            ulong hiPart = Bmi2.pext_u64(hi, hiMask);
            return loPart | (hiPart << loBits);
        }

        return ReadWindow(lo, hi, start, count);
    }

    /// <summary>
    /// Extracts the 16 palette indices for one index set into byte lanes
    /// <paramref name="outIdx"/>[0..15]. Each pixel carries <paramref name="width"/>
    /// bits, except pixels flagged in <paramref name="anchorBits"/> which carry
    /// <paramref name="width"/>-1 bits (their missing MSB reads as 0).
    /// </summary>
    static unsafe void ExtractIndices(
        ulong lo,
        ulong hi,
        int start,
        int width,
        int anchorBits,
        byte* outIdx
    )
    {
        if (Bmi2.IsBmi2Supported)
        {
            ulong maskLo = 0,
                maskHi = 0;
            int loCount = 0,
                hiCount = 0;
            for (int i = 0; i < 16; i++)
            {
                int w = ((anchorBits >> i) & 1) != 0 ? width - 1 : width;
                ulong laneMask = (1ul << w) - 1;
                if (i < 8)
                {
                    maskLo |= laneMask << (8 * i);
                    loCount += w;
                }
                else
                {
                    maskHi |= laneMask << (8 * (i - 8));
                    hiCount += w;
                }
            }

            ulong packed = PullRun(lo, hi, start, loCount + hiCount);
            *(ulong*)outIdx = Bmi2.pdep_u64(packed, maskLo);
            *(ulong*)(outIdx + 8) = Bmi2.pdep_u64(packed >> loCount, maskHi);
        }
        else
        {
            int pos = start;
            for (int i = 0; i < 16; i++)
            {
                int w = ((anchorBits >> i) & 1) != 0 ? width - 1 : width;
                outIdx[i] = (byte)ReadWindow(lo, hi, pos, w);
                pos += w;
            }
        }
    }

    /// <summary>
    /// Translates the 16 palette indices in <paramref name="idx"/>[0..15] to their
    /// interpolation weights in <paramref name="outW"/>[0..15] with a single
    /// <c>pshufb</c> against the vector weight table <paramref name="tableVec"/>.
    /// Falls back to a scalar lookup in <paramref name="tableScalar"/> when SSSE3 is
    /// unavailable; both paths are bit-identical because every index is in range.
    /// </summary>
    static unsafe void MapWeights(byte* idx, v128 tableVec, byte[] tableScalar, byte* outW)
    {
        if (Ssse3.IsSsse3Supported && Sse2.IsSse2Supported)
        {
            Sse2.storeu_si128(outW, Ssse3.shuffle_epi8(tableVec, Sse2.loadu_si128(idx)));
        }
        else
        {
            for (int i = 0; i < 16; i++)
                outW[i] = tableScalar[idx[i]];
        }
    }

    #endregion

    #region Byte → Color float conversion (AVX2 + scalar fallback)

    static unsafe FixedArray16<Color> BytesToColors(byte* rgba)
    {
        FixedArray16<Color> output = default;
        float* tmp = stackalloc float[64];

        if (Avx2.IsAvx2Supported && Avx.IsAvxSupported)
        {
            v256 inv = Avx.mm256_set1_ps(1f / 255f);
            for (int k = 0; k < 64; k += 8)
            {
                // Load 8 bytes -> 8 u8 -> 8 i32 -> 8 f32, scale by 1/255, store.
                v128 bytes = Sse2.cvtsi64x_si128(*(long*)(rgba + k));
                v256 ints = Avx2.mm256_cvtepu8_epi32(bytes);
                v256 fl = Avx.mm256_mul_ps(Avx.mm256_cvtepi32_ps(ints), inv);
                Avx.mm256_storeu_ps(tmp + k, fl);
            }
        }
        else
        {
            const float invs = 1f / 255f;
            for (int k = 0; k < 64; k++)
                tmp[k] = rgba[k] * invs;
        }

        for (int i = 0; i < 16; i++)
            output[i] = new Color(tmp[i * 4 + 0], tmp[i * 4 + 1], tmp[i * 4 + 2], tmp[i * 4 + 3]);
        return output;
    }

    #endregion

    #region Scalar per-mode byte decoders (fill 64 bytes = 16 RGBA pixels)

    static unsafe void DecodeBytes(ulong lo, ulong hi, byte* rgba)
    {
        int mode = CountTrailingZeros((byte)(lo & 0xFF));
        if (mode >= 8)
        {
            for (int i = 0; i < 64; i++)
                rgba[i] = 0;
            return;
        }

        var reader = new BitReader(lo, hi);
        reader.SkipBits(mode + 1);

        switch (mode)
        {
            case 0:
                if (Bmi2.IsBmi2Supported)
                    Mode0Avx(lo, hi, rgba);
                else
                    Mode0(ref reader, lo, hi, rgba);
                break;
            case 1:
                Mode1(ref reader, lo, hi, rgba);
                break;
            case 2:
                Mode2(ref reader, lo, hi, rgba);
                break;
            case 3:
                Mode3(ref reader, lo, hi, rgba);
                break;
            case 4:
                Mode4(ref reader, lo, hi, rgba);
                break;
            case 5:
                Mode5(ref reader, lo, hi, rgba);
                break;
            case 6:
                Mode6(ref reader, lo, hi, rgba);
                break;
            default:
                Mode7(ref reader, lo, hi, rgba);
                break;
        }
    }

    static unsafe void Write(byte* rgba, int i, int r, int g, int b, int a)
    {
        int o = i * 4;
        rgba[o + 0] = (byte)r;
        rgba[o + 1] = (byte)g;
        rgba[o + 2] = (byte)b;
        rgba[o + 3] = (byte)a;
    }

    // Mode 0: 3 subsets, 4-bit RGB endpoints, unique p-bit per endpoint, 3-bit indices.
    static unsafe void Mode0(ref BitReader reader, ulong lo, ulong hi, byte* rgba)
    {
        int partition = reader.ReadBits(4);

        int rS0E0 = reader.ReadBits(4),
            rS0E1 = reader.ReadBits(4);
        int rS1E0 = reader.ReadBits(4),
            rS1E1 = reader.ReadBits(4);
        int rS2E0 = reader.ReadBits(4),
            rS2E1 = reader.ReadBits(4);
        int gS0E0 = reader.ReadBits(4),
            gS0E1 = reader.ReadBits(4);
        int gS1E0 = reader.ReadBits(4),
            gS1E1 = reader.ReadBits(4);
        int gS2E0 = reader.ReadBits(4),
            gS2E1 = reader.ReadBits(4);
        int bS0E0 = reader.ReadBits(4),
            bS0E1 = reader.ReadBits(4);
        int bS1E0 = reader.ReadBits(4),
            bS1E1 = reader.ReadBits(4);
        int bS2E0 = reader.ReadBits(4),
            bS2E1 = reader.ReadBits(4);

        int pb0 = reader.ReadBits(1),
            pb1 = reader.ReadBits(1);
        int pb2 = reader.ReadBits(1),
            pb3 = reader.ReadBits(1);
        int pb4 = reader.ReadBits(1),
            pb5 = reader.ReadBits(1);

        int ur0s0 = Unquantize((rS0E0 << 1) | pb0, 5),
            ur1s0 = Unquantize((rS0E1 << 1) | pb1, 5);
        int ur0s1 = Unquantize((rS1E0 << 1) | pb2, 5),
            ur1s1 = Unquantize((rS1E1 << 1) | pb3, 5);
        int ur0s2 = Unquantize((rS2E0 << 1) | pb4, 5),
            ur1s2 = Unquantize((rS2E1 << 1) | pb5, 5);
        int ug0s0 = Unquantize((gS0E0 << 1) | pb0, 5),
            ug1s0 = Unquantize((gS0E1 << 1) | pb1, 5);
        int ug0s1 = Unquantize((gS1E0 << 1) | pb2, 5),
            ug1s1 = Unquantize((gS1E1 << 1) | pb3, 5);
        int ug0s2 = Unquantize((gS2E0 << 1) | pb4, 5),
            ug1s2 = Unquantize((gS2E1 << 1) | pb5, 5);
        int ub0s0 = Unquantize((bS0E0 << 1) | pb0, 5),
            ub1s0 = Unquantize((bS0E1 << 1) | pb1, 5);
        int ub0s1 = Unquantize((bS1E0 << 1) | pb2, 5),
            ub1s1 = Unquantize((bS1E1 << 1) | pb3, 5);
        int ub0s2 = Unquantize((bS2E0 << 1) | pb4, 5),
            ub1s2 = Unquantize((bS2E1 << 1) | pb5, 5);

        int anchor1 = AnchorIndex3_1[partition];
        int anchor2 = AnchorIndex3_2[partition];
        int anchorBits = 1 | (1 << anchor1) | (1 << anchor2);

        byte* idx = stackalloc byte[16];
        ExtractIndices(lo, hi, reader.BitPos, 3, anchorBits, idx);

        byte* weights = stackalloc byte[16];
        MapWeights(idx, Weights3Vec, Weights3, weights);

        for (int i = 0; i < 16; i++)
        {
            int subset = PartitionTable3[partition * 16 + i];
            int w = weights[i];
            int ri,
                gi,
                bi;
            switch (subset)
            {
                case 0:
                    ri = Interpolate(ur0s0, ur1s0, w);
                    gi = Interpolate(ug0s0, ug1s0, w);
                    bi = Interpolate(ub0s0, ub1s0, w);
                    break;
                case 1:
                    ri = Interpolate(ur0s1, ur1s1, w);
                    gi = Interpolate(ug0s1, ug1s1, w);
                    bi = Interpolate(ub0s1, ub1s1, w);
                    break;
                default:
                    ri = Interpolate(ur0s2, ur1s2, w);
                    gi = Interpolate(ug0s2, ug1s2, w);
                    bi = Interpolate(ub0s2, ub1s2, w);
                    break;
            }
            Write(rgba, i, ri, gi, bi, 255);
        }
    }

    // Mode 1: 2 subsets, 6-bit RGB endpoints, shared p-bit per subset, 3-bit indices.
    static unsafe void Mode1(ref BitReader reader, ulong lo, ulong hi, byte* rgba)
    {
        int partition = reader.ReadBits(6);

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

        int pb0 = reader.ReadBits(1),
            pb1 = reader.ReadBits(1);

        int ur0s0 = Unquantize((rS0E0 << 1) | pb0, 7),
            ur1s0 = Unquantize((rS0E1 << 1) | pb0, 7);
        int ur0s1 = Unquantize((rS1E0 << 1) | pb1, 7),
            ur1s1 = Unquantize((rS1E1 << 1) | pb1, 7);
        int ug0s0 = Unquantize((gS0E0 << 1) | pb0, 7),
            ug1s0 = Unquantize((gS0E1 << 1) | pb0, 7);
        int ug0s1 = Unquantize((gS1E0 << 1) | pb1, 7),
            ug1s1 = Unquantize((gS1E1 << 1) | pb1, 7);
        int ub0s0 = Unquantize((bS0E0 << 1) | pb0, 7),
            ub1s0 = Unquantize((bS0E1 << 1) | pb0, 7);
        int ub0s1 = Unquantize((bS1E0 << 1) | pb1, 7),
            ub1s1 = Unquantize((bS1E1 << 1) | pb1, 7);

        int anchor1 = AnchorIndex2_1[partition];
        int anchorBits = 1 | (1 << anchor1);

        byte* idx = stackalloc byte[16];
        ExtractIndices(lo, hi, reader.BitPos, 3, anchorBits, idx);

        byte* weights = stackalloc byte[16];
        MapWeights(idx, Weights3Vec, Weights3, weights);

        for (int i = 0; i < 16; i++)
        {
            int subset = PartitionTable2[partition * 16 + i];
            int w = weights[i];
            int ri,
                gi,
                bi;
            if (subset == 0)
            {
                ri = Interpolate(ur0s0, ur1s0, w);
                gi = Interpolate(ug0s0, ug1s0, w);
                bi = Interpolate(ub0s0, ub1s0, w);
            }
            else
            {
                ri = Interpolate(ur0s1, ur1s1, w);
                gi = Interpolate(ug0s1, ug1s1, w);
                bi = Interpolate(ub0s1, ub1s1, w);
            }
            Write(rgba, i, ri, gi, bi, 255);
        }
    }

    // Mode 2: 3 subsets, 5-bit RGB endpoints, no p-bit, 2-bit indices.
    static unsafe void Mode2(ref BitReader reader, ulong lo, ulong hi, byte* rgba)
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

        int ur0s0 = Unquantize(rS0E0, 5),
            ur1s0 = Unquantize(rS0E1, 5);
        int ur0s1 = Unquantize(rS1E0, 5),
            ur1s1 = Unquantize(rS1E1, 5);
        int ur0s2 = Unquantize(rS2E0, 5),
            ur1s2 = Unquantize(rS2E1, 5);
        int ug0s0 = Unquantize(gS0E0, 5),
            ug1s0 = Unquantize(gS0E1, 5);
        int ug0s1 = Unquantize(gS1E0, 5),
            ug1s1 = Unquantize(gS1E1, 5);
        int ug0s2 = Unquantize(gS2E0, 5),
            ug1s2 = Unquantize(gS2E1, 5);
        int ub0s0 = Unquantize(bS0E0, 5),
            ub1s0 = Unquantize(bS0E1, 5);
        int ub0s1 = Unquantize(bS1E0, 5),
            ub1s1 = Unquantize(bS1E1, 5);
        int ub0s2 = Unquantize(bS2E0, 5),
            ub1s2 = Unquantize(bS2E1, 5);

        int anchor1 = AnchorIndex3_1[partition];
        int anchor2 = AnchorIndex3_2[partition];
        int anchorBits = 1 | (1 << anchor1) | (1 << anchor2);

        byte* idx = stackalloc byte[16];
        ExtractIndices(lo, hi, reader.BitPos, 2, anchorBits, idx);

        byte* weights = stackalloc byte[16];
        MapWeights(idx, Weights2Vec, Weights2, weights);

        for (int i = 0; i < 16; i++)
        {
            int subset = PartitionTable3[partition * 16 + i];
            int w = weights[i];
            int ri,
                gi,
                bi;
            switch (subset)
            {
                case 0:
                    ri = Interpolate(ur0s0, ur1s0, w);
                    gi = Interpolate(ug0s0, ug1s0, w);
                    bi = Interpolate(ub0s0, ub1s0, w);
                    break;
                case 1:
                    ri = Interpolate(ur0s1, ur1s1, w);
                    gi = Interpolate(ug0s1, ug1s1, w);
                    bi = Interpolate(ub0s1, ub1s1, w);
                    break;
                default:
                    ri = Interpolate(ur0s2, ur1s2, w);
                    gi = Interpolate(ug0s2, ug1s2, w);
                    bi = Interpolate(ub0s2, ub1s2, w);
                    break;
            }
            Write(rgba, i, ri, gi, bi, 255);
        }
    }

    // Mode 3: 2 subsets, 7-bit RGB endpoints, unique p-bit per endpoint, 2-bit indices.
    static unsafe void Mode3(ref BitReader reader, ulong lo, ulong hi, byte* rgba)
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

        int pb0 = reader.ReadBits(1),
            pb1 = reader.ReadBits(1);
        int pb2 = reader.ReadBits(1),
            pb3 = reader.ReadBits(1);

        int ur0s0 = Unquantize((rS0E0 << 1) | pb0, 8),
            ur1s0 = Unquantize((rS0E1 << 1) | pb1, 8);
        int ur0s1 = Unquantize((rS1E0 << 1) | pb2, 8),
            ur1s1 = Unquantize((rS1E1 << 1) | pb3, 8);
        int ug0s0 = Unquantize((gS0E0 << 1) | pb0, 8),
            ug1s0 = Unquantize((gS0E1 << 1) | pb1, 8);
        int ug0s1 = Unquantize((gS1E0 << 1) | pb2, 8),
            ug1s1 = Unquantize((gS1E1 << 1) | pb3, 8);
        int ub0s0 = Unquantize((bS0E0 << 1) | pb0, 8),
            ub1s0 = Unquantize((bS0E1 << 1) | pb1, 8);
        int ub0s1 = Unquantize((bS1E0 << 1) | pb2, 8),
            ub1s1 = Unquantize((bS1E1 << 1) | pb3, 8);

        int anchor1 = AnchorIndex2_1[partition];
        int anchorBits = 1 | (1 << anchor1);

        byte* idx = stackalloc byte[16];
        ExtractIndices(lo, hi, reader.BitPos, 2, anchorBits, idx);

        byte* weights = stackalloc byte[16];
        MapWeights(idx, Weights2Vec, Weights2, weights);

        for (int i = 0; i < 16; i++)
        {
            int subset = PartitionTable2[partition * 16 + i];
            int w = weights[i];
            int ri,
                gi,
                bi;
            if (subset == 0)
            {
                ri = Interpolate(ur0s0, ur1s0, w);
                gi = Interpolate(ug0s0, ug1s0, w);
                bi = Interpolate(ub0s0, ub1s0, w);
            }
            else
            {
                ri = Interpolate(ur0s1, ur1s1, w);
                gi = Interpolate(ug0s1, ug1s1, w);
                bi = Interpolate(ub0s1, ub1s1, w);
            }
            Write(rgba, i, ri, gi, bi, 255);
        }
    }

    // Mode 4: 1 subset, 5-bit RGB + 6-bit A, rotation, 2-bit + 3-bit index sets (idxMode swaps).
    static unsafe void Mode4(ref BitReader reader, ulong lo, ulong hi, byte* rgba)
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

        int ur0 = Unquantize(r0, 5),
            ur1 = Unquantize(r1, 5);
        int ug0 = Unquantize(g0, 5),
            ug1 = Unquantize(g1, 5);
        int ub0 = Unquantize(b0, 5),
            ub1 = Unquantize(b1, 5);
        int ua0 = Unquantize(a0, 6),
            ua1 = Unquantize(a1, 6);

        // First index set: 2 bits (pixel 0 anchored to 1 bit).
        int start2 = reader.BitPos;
        byte* idx2 = stackalloc byte[16];
        ExtractIndices(lo, hi, start2, 2, 1, idx2);

        // Second index set: 3 bits (pixel 0 anchored to 2 bits), directly after set 1.
        int start3 = start2 + (16 * 2 - 1);
        byte* idx3 = stackalloc byte[16];
        ExtractIndices(lo, hi, start3, 3, 1, idx3);

        byte* w2 = stackalloc byte[16];
        MapWeights(idx2, Weights2Vec, Weights2, w2);
        byte* w3 = stackalloc byte[16];
        MapWeights(idx3, Weights3Vec, Weights3, w3);

        for (int i = 0; i < 16; i++)
        {
            int colorWeight,
                alphaWeight;
            if (idxMode == 0)
            {
                colorWeight = w2[i];
                alphaWeight = w3[i];
            }
            else
            {
                colorWeight = w3[i];
                alphaWeight = w2[i];
            }

            int ri = Interpolate(ur0, ur1, colorWeight);
            int gi = Interpolate(ug0, ug1, colorWeight);
            int bi = Interpolate(ub0, ub1, colorWeight);
            int ai = Interpolate(ua0, ua1, alphaWeight);

            ApplyRotation(rotation, ref ri, ref gi, ref bi, ref ai);
            Write(rgba, i, ri, gi, bi, ai);
        }
    }

    // Mode 5: 1 subset, 7-bit RGB + 8-bit A, rotation, separate 2-bit color and alpha indices.
    static unsafe void Mode5(ref BitReader reader, ulong lo, ulong hi, byte* rgba)
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

        int ur0 = Unquantize(r0, 7),
            ur1 = Unquantize(r1, 7);
        int ug0 = Unquantize(g0, 7),
            ug1 = Unquantize(g1, 7);
        int ub0 = Unquantize(b0, 7),
            ub1 = Unquantize(b1, 7);
        int ua0 = Unquantize(a0, 8),
            ua1 = Unquantize(a1, 8);

        int startC = reader.BitPos;
        byte* cIdx = stackalloc byte[16];
        ExtractIndices(lo, hi, startC, 2, 1, cIdx);

        int startA = startC + (16 * 2 - 1);
        byte* aIdx = stackalloc byte[16];
        ExtractIndices(lo, hi, startA, 2, 1, aIdx);

        byte* cw2 = stackalloc byte[16];
        MapWeights(cIdx, Weights2Vec, Weights2, cw2);
        byte* aw2 = stackalloc byte[16];
        MapWeights(aIdx, Weights2Vec, Weights2, aw2);

        for (int i = 0; i < 16; i++)
        {
            int cw = cw2[i];
            int aw = aw2[i];
            int ri = Interpolate(ur0, ur1, cw);
            int gi = Interpolate(ug0, ug1, cw);
            int bi = Interpolate(ub0, ub1, cw);
            int ai = Interpolate(ua0, ua1, aw);

            ApplyRotation(rotation, ref ri, ref gi, ref bi, ref ai);
            Write(rgba, i, ri, gi, bi, ai);
        }
    }

    // Mode 6: 1 subset, 7-bit RGBA + unique p-bit per endpoint, 4-bit indices.
    static unsafe void Mode6(ref BitReader reader, ulong lo, ulong hi, byte* rgba)
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

        int ur0 = Unquantize((r0 << 1) | pb0, 8),
            ur1 = Unquantize((r1 << 1) | pb1, 8);
        int ug0 = Unquantize((g0 << 1) | pb0, 8),
            ug1 = Unquantize((g1 << 1) | pb1, 8);
        int ub0 = Unquantize((b0 << 1) | pb0, 8),
            ub1 = Unquantize((b1 << 1) | pb1, 8);
        int ua0 = Unquantize((a0 << 1) | pb0, 8),
            ua1 = Unquantize((a1 << 1) | pb1, 8);

        byte* idx = stackalloc byte[16];
        ExtractIndices(lo, hi, reader.BitPos, 4, 1, idx);

        byte* weights = stackalloc byte[16];
        MapWeights(idx, Weights4Vec, Weights4, weights);

        for (int i = 0; i < 16; i++)
        {
            int w = weights[i];
            Write(
                rgba,
                i,
                Interpolate(ur0, ur1, w),
                Interpolate(ug0, ug1, w),
                Interpolate(ub0, ub1, w),
                Interpolate(ua0, ua1, w)
            );
        }
    }

    // Mode 7: 2 subsets, 5-bit RGBA + unique p-bit per endpoint, 2-bit indices.
    static unsafe void Mode7(ref BitReader reader, ulong lo, ulong hi, byte* rgba)
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

        int ur0s0 = Unquantize((rS0E0 << 1) | pb0, 6),
            ur1s0 = Unquantize((rS0E1 << 1) | pb1, 6);
        int ur0s1 = Unquantize((rS1E0 << 1) | pb2, 6),
            ur1s1 = Unquantize((rS1E1 << 1) | pb3, 6);
        int ug0s0 = Unquantize((gS0E0 << 1) | pb0, 6),
            ug1s0 = Unquantize((gS0E1 << 1) | pb1, 6);
        int ug0s1 = Unquantize((gS1E0 << 1) | pb2, 6),
            ug1s1 = Unquantize((gS1E1 << 1) | pb3, 6);
        int ub0s0 = Unquantize((bS0E0 << 1) | pb0, 6),
            ub1s0 = Unquantize((bS0E1 << 1) | pb1, 6);
        int ub0s1 = Unquantize((bS1E0 << 1) | pb2, 6),
            ub1s1 = Unquantize((bS1E1 << 1) | pb3, 6);
        int ua0s0 = Unquantize((aS0E0 << 1) | pb0, 6),
            ua1s0 = Unquantize((aS0E1 << 1) | pb1, 6);
        int ua0s1 = Unquantize((aS1E0 << 1) | pb2, 6),
            ua1s1 = Unquantize((aS1E1 << 1) | pb3, 6);

        int anchor1 = AnchorIndex2_1[partition];
        int anchorBits = 1 | (1 << anchor1);

        byte* idx = stackalloc byte[16];
        ExtractIndices(lo, hi, reader.BitPos, 2, anchorBits, idx);

        byte* weights = stackalloc byte[16];
        MapWeights(idx, Weights2Vec, Weights2, weights);

        for (int i = 0; i < 16; i++)
        {
            int subset = PartitionTable2[partition * 16 + i];
            int w = weights[i];
            int ri,
                gi,
                bi,
                ai;
            if (subset == 0)
            {
                ri = Interpolate(ur0s0, ur1s0, w);
                gi = Interpolate(ug0s0, ug1s0, w);
                bi = Interpolate(ub0s0, ub1s0, w);
                ai = Interpolate(ua0s0, ua1s0, w);
            }
            else
            {
                ri = Interpolate(ur0s1, ur1s1, w);
                gi = Interpolate(ug0s1, ug1s1, w);
                bi = Interpolate(ub0s1, ub1s1, w);
                ai = Interpolate(ua0s1, ua1s1, w);
            }
            Write(rgba, i, ri, gi, bi, ai);
        }
    }

    #endregion

    #region AVX2 / BMI2-accelerated per-mode decoders

    // Absolute bit offset of the Mode 0 index stream: 1 mode terminator + 4 partition
    // + 18*4 endpoint + 6 p-bit = 83. The Mode 0 header is fixed-size, so this is a
    // constant rather than a running bit position.
    const int Mode0IndexStart = 83;

    // Mode 0 (BMI2 fast path). The fixed-size header is parsed by pulling each
    // contiguous field straight out of the 128-bit block and scattering the packed
    // 4-bit endpoints / 1-bit p-bits into individual byte lanes with pdep, replacing the
    // per-field scalar BitReader. Index extraction, weight mapping and the interpolation
    // loop are shared with the scalar Mode0 and stay bit-identical.
    static unsafe void Mode0Avx(ulong lo, ulong hi, byte* rgba)
    {
        int partition = (int)((lo >> 1) & 0xF);

        // The three 24-bit endpoint runs (6 nibbles each) plus the 6-bit p-bit run.
        // R and G lie wholly in lo; B straddles the lo/hi boundary; the p-bits are in hi.
        ulong rRun = (lo >> 5) & 0xFFFFFF;
        ulong gRun = (lo >> 29) & 0xFFFFFF;
        ulong bRun = ((lo >> 53) | (hi << 11)) & 0xFFFFFF;
        ulong pRun = (hi >> 13) & 0x3F;

        // Scatter each 4-bit endpoint into its own byte lane and each p-bit into bit 0 of
        // its lane. Lane 2s+e then holds subset s, endpoint e (matching the scalar order).
        const ulong NibbleLanes = 0x0F0F0F0F0F0Ful;
        const ulong PbitLanes = 0x010101010101ul;
        ulong rN = Bmi2.pdep_u64(rRun, NibbleLanes);
        ulong gN = Bmi2.pdep_u64(gRun, NibbleLanes);
        ulong bN = Bmi2.pdep_u64(bRun, NibbleLanes);
        ulong pB = Bmi2.pdep_u64(pRun, PbitLanes);

        // endpoint5 = (nibble << 1) | pbit, then unquantize 5 -> 8 bits, all lanes at once.
        byte* ur = stackalloc byte[8];
        byte* ug = stackalloc byte[8];
        byte* ub = stackalloc byte[8];
        *(ulong*)ur = Unquantize5x8((rN << 1) | pB);
        *(ulong*)ug = Unquantize5x8((gN << 1) | pB);
        *(ulong*)ub = Unquantize5x8((bN << 1) | pB);

        int anchor1 = AnchorIndex3_1[partition];
        int anchor2 = AnchorIndex3_2[partition];
        int anchorBits = 1 | (1 << anchor1) | (1 << anchor2);

        byte* idx = stackalloc byte[16];
        ExtractIndices(lo, hi, Mode0IndexStart, 3, anchorBits, idx);

        byte* weights = stackalloc byte[16];
        MapWeights(idx, Weights3Vec, Weights3, weights);

        for (int i = 0; i < 16; i++)
        {
            int subset = PartitionTable3[partition * 16 + i];
            int w = weights[i];
            int e0 = subset * 2;
            Write(
                rgba,
                i,
                Interpolate(ur[e0], ur[e0 + 1], w),
                Interpolate(ug[e0], ug[e0 + 1], w),
                Interpolate(ub[e0], ub[e0 + 1], w),
                255
            );
        }
    }

    /// <summary>
    /// Unquantizes eight independent 5-bit values (one per byte lane) to 8 bits, matching
    /// the scalar <c>Unquantize(v, 5)</c> (<c>v &lt;&lt;= 3; v |= v &gt;&gt; 5</c>) per lane.
    /// Each lane's value is &lt;= 0x1F so the byte-wide <c>&lt;&lt; 3</c> never overflows its
    /// lane, and the <c>0x07</c> mask keeps the <c>&gt;&gt; 5</c> from pulling neighbouring
    /// lanes' low bits into a lane's high bits.
    /// </summary>
    static ulong Unquantize5x8(ulong five)
    {
        ulong q = (five & 0x1F1F1F1F1F1F1F1Ful) << 3;
        return q | ((q >> 5) & 0x0707070707070707ul);
    }

    #endregion
}
