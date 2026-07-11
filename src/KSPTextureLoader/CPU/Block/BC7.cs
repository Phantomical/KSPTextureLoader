using KSPTextureLoader.Utils;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using UnityEngine;
using static Unity.Burst.Intrinsics.X86;
using static Unity.Burst.Intrinsics.X86.Avx;
using static Unity.Burst.Intrinsics.X86.Avx2;
using static Unity.Burst.Intrinsics.X86.Bmi2;
using static Unity.Burst.Intrinsics.X86.Sse2;
using static Unity.Burst.Intrinsics.X86.Ssse3;

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

    // [BurstCompile]
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
                if (Avx2.IsAvx2Supported)
                    Mode0Avx(lo, hi, rgba);
                else
                    Mode0(ref reader, lo, hi, rgba);
                break;
            case 1:
                if (Avx2.IsAvx2Supported)
                    Mode1Avx(lo, hi, rgba);
                else
                    Mode1(ref reader, lo, hi, rgba);
                break;
            case 2:
                if (Avx2.IsAvx2Supported)
                    Mode2Avx(lo, hi, rgba);
                else
                    Mode2(ref reader, lo, hi, rgba);
                break;
            case 3:
                if (Avx2.IsAvx2Supported)
                    Mode3Avx(lo, hi, rgba);
                else
                    Mode3(ref reader, lo, hi, rgba);
                break;
            case 4:
                if (Avx2.IsAvx2Supported)
                    Mode4Avx(lo, hi, rgba);
                else
                    Mode4(ref reader, lo, hi, rgba);
                break;
            case 5:
                if (Avx2.IsAvx2Supported)
                    Mode5Avx(lo, hi, rgba);
                else
                    Mode5(ref reader, lo, hi, rgba);
                break;
            case 6:
                if (Avx2.IsAvx2Supported)
                    Mode6Avx(lo, hi, rgba);
                else
                    Mode6(ref reader, lo, hi, rgba);
                break;
            default:
                if (Avx2.IsAvx2Supported)
                    Mode7Avx(lo, hi, rgba);
                else
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

    #region Mode 0

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

    #endregion

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

    #region Mode 0
    // The Mode 0 index stream starts at bit 83 (1 mode terminator + 4 partition + 18*4
    // endpoint + 6 p-bit) and is always exactly 45 bits (16*3 minus the 3 anchor MSBs), so
    // it lies entirely in hi (bits 19..63) and right-aligns with a single shift — no pext
    // needed.
    //
    // Per-partition pdep scatter masks for the Mode 0 index stream. The partition is only
    // 4 bits, so all 16 layouts are precomputed here instead of rebuilt per block: lane i
    // takes pixel i's 3-bit index, except the 3 anchor lanes which take 2 bits so their
    // forced-zero MSB falls out for free. Mode0IndexLoBits[p] = popcount(maskLo[p]) is how
    // far to shift the packed run before depositing lanes 8..15. Derived directly from
    // AnchorIndex3_1/AnchorIndex3_2 with width 3 (identical layout to ExtractIndices).
    // csharpier-ignore-start
    static readonly ulong[] Mode0IndexMaskLo =
    [
        0x0707070703070703UL, 0x0707070703070703UL, 0x0707070707070703UL, 0x0707070703070703UL,
        0x0707070707070703UL, 0x0707070703070703UL, 0x0707070703070703UL, 0x0707070707070703UL,
        0x0707070707070703UL, 0x0707070707070703UL, 0x0703070707070703UL, 0x0703070707070703UL,
        0x0703070707070703UL, 0x0707030707070703UL, 0x0707070703070703UL, 0x0707070703070703UL,
    ];
    static readonly ulong[] Mode0IndexMaskHi =
    [
        0x0307070707070707UL, 0x0707070707070703UL, 0x0307070707070703UL, 0x0307070707070707UL,
        0x0307070707070703UL, 0x0307070707070707UL, 0x0307070707070707UL, 0x0307070707070703UL,
        0x0307070707070703UL, 0x0307070707070703UL, 0x0307070707070707UL, 0x0307070707070707UL,
        0x0307070707070707UL, 0x0307070707070707UL, 0x0307070707070707UL, 0x0707070707070703UL,
    ];
    static readonly byte[] Mode0IndexLoBits =
    [
        22, 22, 23, 22, 23, 22, 22, 23, 23, 23, 22, 22, 22, 22, 22, 22,
    ];

    // Per-partition endpoint-gather control for the interpolation stage: 16 pixels x
    // (e0, e1) byte selectors = 32 lanes, packed as 4 ulongs per partition. Lane 2i / 2i+1
    // hold 2*subset[i] / 2*subset[i]+1 — the [e0 e1] pair for pixel i's subset — and feed
    // mm256_shuffle_epi8 to pull each pixel's endpoint pair out of the 8-byte lane vector.
    static readonly ulong[] Mode0Gather =
    [
        0x0302030201000100UL, 0x0302030201000100UL, 0x0302050405040100UL, 0x0504050405040504UL, // p0
        0x0302010001000100UL, 0x0302030201000100UL, 0x0302030205040504UL, 0x0302050405040504UL, // p1
        0x0100010001000100UL, 0x0302010001000504UL, 0x0302030205040504UL, 0x0302030205040504UL, // p2
        0x0504050405040100UL, 0x0504050401000100UL, 0x0302030201000100UL, 0x0302030203020100UL, // p3
        0x0100010001000100UL, 0x0100010001000100UL, 0x0504050403020302UL, 0x0504050403020302UL, // p4
        0x0302030201000100UL, 0x0302030201000100UL, 0x0504050401000100UL, 0x0504050401000100UL, // p5
        0x0504050401000100UL, 0x0504050401000100UL, 0x0302030203020302UL, 0x0302030203020302UL, // p6
        0x0302030201000100UL, 0x0302030201000100UL, 0x0302030205040504UL, 0x0302030205040504UL, // p7
        0x0100010001000100UL, 0x0100010001000100UL, 0x0302030203020302UL, 0x0504050405040504UL, // p8
        0x0100010001000100UL, 0x0302030203020302UL, 0x0302030203020302UL, 0x0504050405040504UL, // p9
        0x0100010001000100UL, 0x0302030203020302UL, 0x0504050405040504UL, 0x0504050405040504UL, // p10
        0x0504030201000100UL, 0x0504030201000100UL, 0x0504030201000100UL, 0x0504030201000100UL, // p11
        0x0504030203020100UL, 0x0504030203020100UL, 0x0504030203020100UL, 0x0504030203020100UL, // p12
        0x0504050403020100UL, 0x0504050403020100UL, 0x0504050403020100UL, 0x0504050403020100UL, // p13
        0x0302030201000100UL, 0x0504030203020100UL, 0x0504050403020302UL, 0x0504050405040302UL, // p14
        0x0302030201000100UL, 0x0302010001000504UL, 0x0100010005040504UL, 0x0100050405040504UL, // p15
    ];
    // csharpier-ignore-end

    // BC7 Mode 0
    //
    // Mode 0 is laid out like this
    // | Bit Range | Width | Field
    // |  0        |   1   | mode marker
    // |  1 .. 4   |   4   | partition index        (0..15)
    // |  5 .. 28  |  24   | red   endpoints R0..R5 (6 x 4 bits)
    // | 29 .. 52  |  24   | green endpoints G0..G5 (6 x 4 bits)
    // | 53 .. 76  |  24   | blue  endpoints B0..B5 (6 x 4 bits)
    // | 77 .. 82  |   6   | P-bits                 (6 x 1 bits)
    // | 83 .. 127 |  45   | colour indices         (16 texels, 2/3 bits each)
    //
    // Mode 0 decoding should work like this:
    //   - The 4-bit partition index selects one of 16 fixed patterns (see the partition table)
    //     that splits the 16 texels into 3 subsets, assigning each texel a subset 0/1/2.
    //   - Each subset has two RGB endpoints; every endpoint is 4 bits per channel plus one
    //     shared p-bit, forming a 5-bit value unquantized to 8 bits as (v << 3) | (v >> 2).
    //   - Each texel carries a colour index selecting an interpolation weight from the 3-bit
    //     weight table. Indices are 3 bits, except the anchor texel of each subset, which is
    //     2 bits with an implied high bit of 0 (subset 0's anchor is texel 0; subsets 1 and 2
    //     use the fixed anchor tables).
    //   - Texel i's colour is, per channel, interpolate(e0, e1, w) = (e0*(64-w) + e1*w + 32) >> 6,
    //     where e0/e1 are the two endpoints of that texel's subset and w is its weight.
    //   - Alpha has no bits in this mode and is always opaque (255).
    static unsafe void Mode0Avx(ulong lo, ulong hi, byte* rgba)
    {
        static ulong Unquantize(ulong v) => (v << 3) | ((v >> 2) & 0x070707070707);

        if (IsAvx2Supported)
        {
            const ulong DepMask = 0x0F0F0F0F0F0Ful << 1;
            const ulong PbtMask = 0x010101010101ul;

            // Extract partition and endpoints
            int partition = (int)((lo >> 1) & 0xF);
            ulong rB = pdep_u64(lo >> 5, DepMask);
            ulong gB = pdep_u64(lo >> 29, DepMask);
            ulong bB = pdep_u64((lo >> 53) | (hi << 11), DepMask);
            ulong pB = pdep_u64(hi >> 13, PbtMask);

            // Unquantize: replicate top 3 bits to the bottom, shift by 5
            rB = Unquantize(rB | pB);
            gB = Unquantize(gB | pB);
            bB = Unquantize(bB | pB);

            ulong idata = hi >> (83 - 64);
            ulong ilo = pdep_u64(idata, Mode0IndexMaskLo[partition]);
            ulong ihi = pdep_u64(idata >> Mode0IndexLoBits[partition], Mode0IndexMaskHi[partition]);

            v128 w = shuffle_epi8(Weights3Vec, new(ilo, ihi));
            v128 invw = sub_epi8(new((byte)64), w); // 64 - w

            // Interleave (64-w, w) so we can use a pmaddubs to compute e0*(64-w) + e1*w
            v256 pairs = new(unpacklo_epi8(invw, w), unpackhi_epi8(invw, w));

            // Endpoint gather control for this partition
            v256 gather = new(
                Mode0Gather[partition * 4 + 0],
                Mode0Gather[partition * 4 + 1],
                Mode0Gather[partition * 4 + 2],
                Mode0Gather[partition * 4 + 3]
            );

            // e0*(64-w) + e1*w
            v256 resR = mm256_maddubs_epi16(mm256_shuffle_epi8(new(rB), gather), pairs);
            v256 resG = mm256_maddubs_epi16(mm256_shuffle_epi8(new(gB), gather), pairs);
            v256 resB = mm256_maddubs_epi16(mm256_shuffle_epi8(new(bB), gather), pairs);

            // (res + 32) >> 6
            resR = mm256_srli_epi16(mm256_add_epi16(resR, new((ushort)32)), 6);
            resG = mm256_srli_epi16(mm256_add_epi16(resG, new((ushort)32)), 6);
            resB = mm256_srli_epi16(mm256_add_epi16(resB, new((ushort)32)), 6);

            // We now need to transpose from
            // resR = 0R0R0R0R0R0R0R0R
            // resB = 0B0B0B0B0B0B0B0B
            // resG = 0G0G0G0G0G0G0G0G
            // resA = 0A0A0A0A0A0A0A0A
            //
            // to
            // lo = RGBARGBARGBARGBA
            // hi = RGBARGBARGBARGBA

            var rg = mm256_or_si256(resR, mm256_slli_epi16(resG, 8));
            var ba = mm256_or_si256(resB, new((ushort)0xFF00));
            var rgbaLo = mm256_unpacklo_epi16(rg, ba); // RGBA, pixels {0..3, 8..11}
            var rgbaHi = mm256_unpackhi_epi16(rg, ba); // RGBA, pixels {4..7, 12..15}

            var output = (v256*)rgba;
            output[0] = mm256_permute2x128_si256(rgbaLo, rgbaHi, 0x20); // pixels 0..7
            output[1] = mm256_permute2x128_si256(rgbaLo, rgbaHi, 0x31); // pixels 8..15
        }
    }
    #endregion

    #region Mode 1
    // The Mode 1 index stream starts at bit 82 (2 mode terminator + 6 partition + 12*6
    // endpoint + 2 p-bit) and is always exactly 46 bits (16*3 minus the 2 anchor MSBs), so
    // it lies entirely in hi (bits 18..63) and right-aligns with a single shift.
    //
    // Per-partition pdep scatter masks for the Mode 1 index stream, precomputed for all 64
    // partitions: lane i takes pixel i's 3-bit index, except the 2 anchor lanes which take
    // 2 bits so their forced-zero MSB falls out for free. Mode1IndexLoBits[p] = popcount of
    // maskLo is how far to shift the packed run before depositing lanes 8..15. Derived from
    // AnchorIndex2_1 (subset 1's anchor; subset 0's is always pixel 0) with width 3.
    // csharpier-ignore-start
    static readonly ulong[] Mode1IndexMaskLo =
    [
        0x0707070707070703UL, 0x0707070707070703UL, 0x0707070707070703UL, 0x0707070707070703UL,
        0x0707070707070703UL, 0x0707070707070703UL, 0x0707070707070703UL, 0x0707070707070703UL,
        0x0707070707070703UL, 0x0707070707070703UL, 0x0707070707070703UL, 0x0707070707070703UL,
        0x0707070707070703UL, 0x0707070707070703UL, 0x0707070707070703UL, 0x0707070707070703UL,
        0x0707070707070703UL, 0x0707070707030703UL, 0x0707070707070703UL, 0x0707070707030703UL,
        0x0707070707030703UL, 0x0707070707070703UL, 0x0707070707070703UL, 0x0707070707070703UL,
        0x0707070707030703UL, 0x0707070707070703UL, 0x0707070707030703UL, 0x0707070707030703UL,
        0x0707070707070703UL, 0x0707070707070703UL, 0x0707070707030703UL, 0x0707070707030703UL,
        0x0707070707070703UL, 0x0707070707070703UL, 0x0703070707070703UL, 0x0707070707070703UL,
        0x0707070707030703UL, 0x0707070707070703UL, 0x0707070707070703UL, 0x0707070707070703UL,
        0x0707070707030703UL, 0x0707070707070703UL, 0x0707070707030703UL, 0x0707070707030703UL,
        0x0707070707030703UL, 0x0707070707070703UL, 0x0707070707070703UL, 0x0703070707070703UL,
        0x0703070707070703UL, 0x0707070707030703UL, 0x0703070707070703UL, 0x0707070707070703UL,
        0x0707070707070703UL, 0x0707070707070703UL, 0x0707070707030703UL, 0x0707070707030703UL,
        0x0707070707070703UL, 0x0707070707070703UL, 0x0707070707070703UL, 0x0707070707070703UL,
        0x0707070707070703UL, 0x0707070707030703UL, 0x0707070707030703UL, 0x0707070707070703UL,
    ];
    static readonly ulong[] Mode1IndexMaskHi =
    [
        0x0307070707070707UL, 0x0307070707070707UL, 0x0307070707070707UL, 0x0307070707070707UL,
        0x0307070707070707UL, 0x0307070707070707UL, 0x0307070707070707UL, 0x0307070707070707UL,
        0x0307070707070707UL, 0x0307070707070707UL, 0x0307070707070707UL, 0x0307070707070707UL,
        0x0307070707070707UL, 0x0307070707070707UL, 0x0307070707070707UL, 0x0307070707070707UL,
        0x0307070707070707UL, 0x0707070707070707UL, 0x0707070707070703UL, 0x0707070707070707UL,
        0x0707070707070707UL, 0x0707070707070703UL, 0x0707070707070703UL, 0x0307070707070707UL,
        0x0707070707070707UL, 0x0707070707070703UL, 0x0707070707070707UL, 0x0707070707070707UL,
        0x0707070707070703UL, 0x0707070707070703UL, 0x0707070707070707UL, 0x0707070707070707UL,
        0x0307070707070707UL, 0x0307070707070707UL, 0x0707070707070707UL, 0x0707070707070703UL,
        0x0707070707070707UL, 0x0707070707070703UL, 0x0307070707070707UL, 0x0307070707070707UL,
        0x0707070707070707UL, 0x0707070707070703UL, 0x0707070707070707UL, 0x0707070707070707UL,
        0x0707070707070707UL, 0x0307070707070707UL, 0x0307070707070707UL, 0x0707070707070707UL,
        0x0707070707070707UL, 0x0707070707070707UL, 0x0707070707070707UL, 0x0707070707070703UL,
        0x0307070707070707UL, 0x0307070707070707UL, 0x0707070707070707UL, 0x0707070707070707UL,
        0x0307070707070707UL, 0x0307070707070707UL, 0x0307070707070707UL, 0x0307070707070707UL,
        0x0307070707070707UL, 0x0707070707070707UL, 0x0707070707070707UL, 0x0307070707070707UL,
    ];
    static readonly byte[] Mode1IndexLoBits =
    [
        23, 23, 23, 23, 23, 23, 23, 23, 23, 23, 23, 23, 23, 23, 23, 23,
        23, 22, 23, 22, 22, 23, 23, 23, 22, 23, 22, 22, 23, 23, 22, 22,
        23, 23, 22, 23, 22, 23, 23, 23, 22, 23, 22, 22, 22, 23, 23, 22,
        22, 22, 22, 23, 23, 23, 22, 22, 23, 23, 23, 23, 23, 22, 22, 23,
    ];

    // Per-partition endpoint-gather control for the interpolation stage: 16 pixels x
    // (e0, e1) byte selectors = 32 lanes, packed as 4 ulongs per partition. Lane 2i / 2i+1
    // hold 2*subset[i] / 2*subset[i]+1 — the [e0 e1] pair for pixel i's subset (subset in
    // {0,1}, so selectors in {0..3}) — and feed mm256_shuffle_epi8 to pull each pixel's
    // endpoint pair out of the 8-byte lane vector.
    static readonly ulong[] Mode1Gather =
    [
        0x0302030201000100UL, 0x0302030201000100UL, 0x0302030201000100UL, 0x0302030201000100UL, // p0
        0x0302010001000100UL, 0x0302010001000100UL, 0x0302010001000100UL, 0x0302010001000100UL, // p1
        0x0302030203020100UL, 0x0302030203020100UL, 0x0302030203020100UL, 0x0302030203020100UL, // p2
        0x0302010001000100UL, 0x0302030201000100UL, 0x0302030201000100UL, 0x0302030203020100UL, // p3
        0x0100010001000100UL, 0x0302010001000100UL, 0x0302010001000100UL, 0x0302030201000100UL, // p4
        0x0302030201000100UL, 0x0302030203020100UL, 0x0302030203020100UL, 0x0302030203020302UL, // p5
        0x0302010001000100UL, 0x0302030201000100UL, 0x0302030203020100UL, 0x0302030203020302UL, // p6
        0x0100010001000100UL, 0x0302010001000100UL, 0x0302030201000100UL, 0x0302030203020100UL, // p7
        0x0100010001000100UL, 0x0100010001000100UL, 0x0302010001000100UL, 0x0302030201000100UL, // p8
        0x0302030201000100UL, 0x0302030203020100UL, 0x0302030203020302UL, 0x0302030203020302UL, // p9
        0x0100010001000100UL, 0x0302010001000100UL, 0x0302030203020100UL, 0x0302030203020302UL, // p10
        0x0100010001000100UL, 0x0100010001000100UL, 0x0302010001000100UL, 0x0302030203020100UL, // p11
        0x0302010001000100UL, 0x0302030203020100UL, 0x0302030203020302UL, 0x0302030203020302UL, // p12
        0x0100010001000100UL, 0x0100010001000100UL, 0x0302030203020302UL, 0x0302030203020302UL, // p13
        0x0100010001000100UL, 0x0302030203020302UL, 0x0302030203020302UL, 0x0302030203020302UL, // p14
        0x0100010001000100UL, 0x0100010001000100UL, 0x0100010001000100UL, 0x0302030203020302UL, // p15
        0x0100010001000100UL, 0x0100010001000302UL, 0x0100030203020302UL, 0x0302030203020302UL, // p16
        0x0302030203020100UL, 0x0302010001000100UL, 0x0100010001000100UL, 0x0100010001000100UL, // p17
        0x0100010001000100UL, 0x0100010001000100UL, 0x0100010001000302UL, 0x0100030203020302UL, // p18
        0x0302030203020100UL, 0x0302030201000100UL, 0x0302010001000100UL, 0x0100010001000100UL, // p19
        0x0302030201000100UL, 0x0302010001000100UL, 0x0100010001000100UL, 0x0100010001000100UL, // p20
        0x0100010001000100UL, 0x0100010001000302UL, 0x0100010003020302UL, 0x0100030203020302UL, // p21
        0x0100010001000100UL, 0x0100010001000100UL, 0x0100010001000302UL, 0x0100010003020302UL, // p22
        0x0302030203020100UL, 0x0302030201000100UL, 0x0302030201000100UL, 0x0302010001000100UL, // p23
        0x0302030201000100UL, 0x0302010001000100UL, 0x0302010001000100UL, 0x0100010001000100UL, // p24
        0x0100010001000100UL, 0x0100010001000302UL, 0x0100010001000302UL, 0x0100010003020302UL, // p25
        0x0100030203020100UL, 0x0100030203020100UL, 0x0100030203020100UL, 0x0100030203020100UL, // p26
        0x0302030201000100UL, 0x0100030203020100UL, 0x0100030203020100UL, 0x0100010003020302UL, // p27
        0x0302010001000100UL, 0x0302030203020100UL, 0x0100030203020302UL, 0x0100010001000302UL, // p28
        0x0100010001000100UL, 0x0302030203020302UL, 0x0302030203020302UL, 0x0100010001000100UL, // p29
        0x0302030203020100UL, 0x0302010001000100UL, 0x0100010001000302UL, 0x0100030203020302UL, // p30
        0x0302030201000100UL, 0x0302010001000302UL, 0x0302010001000302UL, 0x0100010003020302UL, // p31
        0x0302010003020100UL, 0x0302010003020100UL, 0x0302010003020100UL, 0x0302010003020100UL, // p32
        0x0100010001000100UL, 0x0302030203020302UL, 0x0100010001000100UL, 0x0302030203020302UL, // p33
        0x0302010003020100UL, 0x0100030201000302UL, 0x0302010003020100UL, 0x0100030201000302UL, // p34
        0x0302030201000100UL, 0x0302030201000100UL, 0x0100010003020302UL, 0x0100010003020302UL, // p35
        0x0302030201000100UL, 0x0100010003020302UL, 0x0302030201000100UL, 0x0100010003020302UL, // p36
        0x0302010003020100UL, 0x0302010003020100UL, 0x0100030201000302UL, 0x0100030201000302UL, // p37
        0x0100030203020100UL, 0x0302010001000302UL, 0x0100030203020100UL, 0x0302010001000302UL, // p38
        0x0302010003020100UL, 0x0100030201000302UL, 0x0100030201000302UL, 0x0302010003020100UL, // p39
        0x0302030203020100UL, 0x0302030201000100UL, 0x0100010003020302UL, 0x0100030203020302UL, // p40
        0x0302010001000100UL, 0x0302030201000100UL, 0x0100010003020302UL, 0x0100010001000302UL, // p41
        0x0302030201000100UL, 0x0100030201000100UL, 0x0100010003020100UL, 0x0100010003020302UL, // p42
        0x0302030201000100UL, 0x0302030201000302UL, 0x0302010003020302UL, 0x0100010003020302UL, // p43
        0x0100030203020100UL, 0x0302010001000302UL, 0x0302010001000302UL, 0x0100030203020100UL, // p44
        0x0302030201000100UL, 0x0100010003020302UL, 0x0100010003020302UL, 0x0302030201000100UL, // p45
        0x0100030203020100UL, 0x0100030203020100UL, 0x0302010001000302UL, 0x0302010001000302UL, // p46
        0x0100010001000100UL, 0x0100030203020100UL, 0x0100030203020100UL, 0x0100010001000100UL, // p47
        0x0100010003020100UL, 0x0100030203020302UL, 0x0100010003020100UL, 0x0100010001000100UL, // p48
        0x0100030201000100UL, 0x0302030203020100UL, 0x0100030201000100UL, 0x0100010001000100UL, // p49
        0x0100010001000100UL, 0x0100030201000100UL, 0x0302030203020100UL, 0x0100030201000100UL, // p50
        0x0100010001000100UL, 0x0100010003020100UL, 0x0100030203020302UL, 0x0100010003020100UL, // p51
        0x0100030203020100UL, 0x0100010003020302UL, 0x0302010001000302UL, 0x0302030201000100UL, // p52
        0x0302030201000100UL, 0x0100030203020100UL, 0x0100010003020302UL, 0x0302010001000302UL, // p53
        0x0100030203020100UL, 0x0302030201000100UL, 0x0302010001000302UL, 0x0100010003020302UL, // p54
        0x0302030201000100UL, 0x0302010001000302UL, 0x0100010003020302UL, 0x0100030203020100UL, // p55
        0x0100030203020100UL, 0x0100010003020302UL, 0x0100010003020302UL, 0x0302010001000302UL, // p56
        0x0100030203020100UL, 0x0302030201000100UL, 0x0302030201000100UL, 0x0302010001000302UL, // p57
        0x0302030203020100UL, 0x0100030203020302UL, 0x0100010001000302UL, 0x0302010001000100UL, // p58
        0x0302010001000100UL, 0x0100010001000302UL, 0x0100030203020302UL, 0x0302030203020100UL, // p59
        0x0100010001000100UL, 0x0302030203020302UL, 0x0302030201000100UL, 0x0302030201000100UL, // p60
        0x0302030201000100UL, 0x0302030201000100UL, 0x0302030203020302UL, 0x0100010001000100UL, // p61
        0x0100030201000100UL, 0x0100030201000100UL, 0x0100030203020302UL, 0x0100030203020302UL, // p62
        0x0100010003020100UL, 0x0100010003020100UL, 0x0302030203020100UL, 0x0302030203020100UL, // p63
    ];
    // csharpier-ignore-end

    // BC7 Mode 1
    //
    // Mode 1 is laid out like this
    // | Bit Range | Width | Field
    // |  0 ..  1  |   2   | mode marker
    // |  2 ..  7  |   6   | partition index        (0..63)
    // |  8 .. 31  |  24   | red   endpoints R0..R3 (4 x 6 bits)
    // | 32 .. 55  |  24   | green endpoints G0..G3 (4 x 6 bits)
    // | 56 .. 79  |  24   | blue  endpoints B0..B3 (4 x 6 bits)
    // | 80 .. 81  |   2   | P-bits                 (2 x 1 bits, one per subset)
    // | 82 .. 127 |  46   | colour indices         (16 texels, 2/3 bits each)
    //
    // Mode 1 decoding should work like this:
    //   - The 6-bit partition index selects one of 64 fixed patterns (see the partition table)
    //     that splits the 16 texels into 2 subsets, assigning each texel a subset 0/1.
    //   - Each subset has two RGB endpoints; every endpoint is 6 bits per channel plus a single
    //     p-bit shared by both of the subset's endpoints, forming a 7-bit value unquantized to
    //     8 bits as (v << 1) | (v >> 6).
    //   - Each texel carries a colour index selecting an interpolation weight from the 3-bit
    //     weight table. Indices are 3 bits, except the anchor texel of each subset, which is
    //     2 bits with an implied high bit of 0 (subset 0's anchor is texel 0; subset 1 uses the
    //     fixed anchor table).
    //   - Texel i's colour is, per channel, interpolate(e0, e1, w) = (e0*(64-w) + e1*w + 32) >> 6,
    //     where e0/e1 are the two endpoints of that texel's subset and w is its weight.
    //   - Alpha has no bits in this mode and is always opaque (255).
    static unsafe void Mode1Avx(ulong lo, ulong hi, byte* rgba)
    {
        static ulong Unquantize(ulong v) => (v << 1) | ((v >> 6) & 0x01010101);

        if (IsAvx2Supported)
        {
            const ulong DepMask = 0x3F3F3F3Ful << 1; // 6-bit endpoint at bits 1..6, bit 0 free

            // Extract partition and endpoints. R and G lie wholly in lo; B straddles the
            // lo/hi boundary at bit 64.
            int partition = (int)((lo >> 2) & 0x3F);
            ulong rB = pdep_u64(lo >> 8, DepMask);
            ulong gB = pdep_u64(lo >> 32, DepMask);
            ulong bB = pdep_u64((lo >> 56) | (hi << 8), DepMask);

            // Two p-bits, each shared by both endpoints of its subset: P0 (bit 80) -> bytes
            // 0,1; P1 (bit 81) -> bytes 2,3. Deposit one per subset then duplicate into the
            // subset's second endpoint.
            ulong pB = pdep_u64(hi >> 16, 0x0000000000010001ul);
            pB |= pB << 8;

            // Unquantize: replicate the top bit to the bottom, shift by 7
            rB = Unquantize(rB | pB);
            gB = Unquantize(gB | pB);
            bB = Unquantize(bB | pB);

            ulong idata = hi >> (82 - 64);
            ulong ilo = pdep_u64(idata, Mode1IndexMaskLo[partition]);
            ulong ihi = pdep_u64(idata >> Mode1IndexLoBits[partition], Mode1IndexMaskHi[partition]);

            v128 w = shuffle_epi8(Weights3Vec, new(ilo, ihi));
            v128 invw = sub_epi8(new((byte)64), w); // 64 - w

            // Interleave (64-w, w) so we can use a pmaddubs to compute e0*(64-w) + e1*w
            v256 pairs = new(unpacklo_epi8(invw, w), unpackhi_epi8(invw, w));

            // Endpoint gather control for this partition
            v256 gather = new(
                Mode1Gather[partition * 4 + 0],
                Mode1Gather[partition * 4 + 1],
                Mode1Gather[partition * 4 + 2],
                Mode1Gather[partition * 4 + 3]
            );

            // e0*(64-w) + e1*w
            v256 resR = mm256_maddubs_epi16(mm256_shuffle_epi8(new(rB), gather), pairs);
            v256 resG = mm256_maddubs_epi16(mm256_shuffle_epi8(new(gB), gather), pairs);
            v256 resB = mm256_maddubs_epi16(mm256_shuffle_epi8(new(bB), gather), pairs);

            // (res + 32) >> 6
            resR = mm256_srli_epi16(mm256_add_epi16(resR, new((ushort)32)), 6);
            resG = mm256_srli_epi16(mm256_add_epi16(resG, new((ushort)32)), 6);
            resB = mm256_srli_epi16(mm256_add_epi16(resB, new((ushort)32)), 6);

            // Transpose the three planar channels (+ constant opaque alpha) to interleaved
            // RGBA, exactly as in Mode 0.
            var rg = mm256_or_si256(resR, mm256_slli_epi16(resG, 8));
            var ba = mm256_or_si256(resB, new((ushort)0xFF00));
            var rgbaLo = mm256_unpacklo_epi16(rg, ba); // RGBA, pixels {0..3, 8..11}
            var rgbaHi = mm256_unpackhi_epi16(rg, ba); // RGBA, pixels {4..7, 12..15}

            var output = (v256*)rgba;
            output[0] = mm256_permute2x128_si256(rgbaLo, rgbaHi, 0x20); // pixels 0..7
            output[1] = mm256_permute2x128_si256(rgbaLo, rgbaHi, 0x31); // pixels 8..15
        }
    }
    #endregion

    #region Mode 2
    // The Mode 2 index stream starts at bit 99 (3 mode terminator + 6 partition + 18*5
    // endpoint, no p-bits) and is always exactly 29 bits (16*2 minus the 3 anchor MSBs), so
    // it lies entirely in hi (bits 35..63) and right-aligns with a single shift.
    //
    // Per-partition pdep scatter masks for the Mode 2 index stream, precomputed for all 64
    // partitions: lane i takes pixel i's 2-bit index, except the 3 anchor lanes which take
    // 1 bit so their forced-zero MSB falls out for free. Mode2IndexLoBits[p] = popcount of
    // maskLo is how far to shift the packed run before depositing lanes 8..15. Derived from
    // AnchorIndex3_1/AnchorIndex3_2 (subset 0's anchor is always pixel 0) with width 2.
    // csharpier-ignore-start
    static readonly ulong[] Mode2IndexMaskLo =
    [
        0x0303030301030301UL, 0x0303030301030301UL, 0x0303030303030301UL, 0x0303030301030301UL,
        0x0303030303030301UL, 0x0303030301030301UL, 0x0303030301030301UL, 0x0303030303030301UL,
        0x0303030303030301UL, 0x0303030303030301UL, 0x0301030303030301UL, 0x0301030303030301UL,
        0x0301030303030301UL, 0x0303010303030301UL, 0x0303030301030301UL, 0x0303030301030301UL,
        0x0303030301030301UL, 0x0303030301030301UL, 0x0303030303030301UL, 0x0303030301030301UL,
        0x0303030301030301UL, 0x0303030301030301UL, 0x0301030303030301UL, 0x0303030303030301UL,
        0x0303010301030301UL, 0x0303030303030301UL, 0x0301030303030301UL, 0x0301030303030301UL,
        0x0303030303030301UL, 0x0303010303030301UL, 0x0303030303030301UL, 0x0303030303030301UL,
        0x0303030303030301UL, 0x0303030301030301UL, 0x0303030301030301UL, 0x0303010303030301UL,
        0x0301030303030301UL, 0x0303030303030301UL, 0x0303030303030301UL, 0x0303030303030301UL,
        0x0301030303030301UL, 0x0303030301030301UL, 0x0303030303030301UL, 0x0303010303030301UL,
        0x0303030301030301UL, 0x0301030303030301UL, 0x0301030303030301UL, 0x0303030303030301UL,
        0x0303030301030301UL, 0x0303030301030301UL, 0x0303010303030301UL, 0x0303010303030301UL,
        0x0303010303030301UL, 0x0303030303030301UL, 0x0303010303030301UL, 0x0303030303030301UL,
        0x0303010303030301UL, 0x0303030303030301UL, 0x0303030303030301UL, 0x0303030303030301UL,
        0x0303030301030301UL, 0x0303030303030301UL, 0x0303030301030301UL, 0x0303030301030301UL,
    ];
    static readonly ulong[] Mode2IndexMaskHi =
    [
        0x0103030303030303UL, 0x0303030303030301UL, 0x0103030303030301UL, 0x0103030303030303UL,
        0x0103030303030301UL, 0x0103030303030303UL, 0x0103030303030303UL, 0x0103030303030301UL,
        0x0103030303030301UL, 0x0103030303030301UL, 0x0103030303030303UL, 0x0103030303030303UL,
        0x0103030303030303UL, 0x0103030303030303UL, 0x0103030303030303UL, 0x0303030303030301UL,
        0x0103030303030303UL, 0x0303030303030301UL, 0x0103030303030301UL, 0x0103030303030303UL,
        0x0103030303030303UL, 0x0303030303030301UL, 0x0103030303030303UL, 0x0303030303010301UL,
        0x0303030303030303UL, 0x0103030303030301UL, 0x0303030303030301UL, 0x0303030303010303UL,
        0x0103030303030301UL, 0x0103030303030303UL, 0x0103030303010303UL, 0x0103030303030301UL,
        0x0103030303030301UL, 0x0103030303030303UL, 0x0103030303030303UL, 0x0303030303010303UL,
        0x0303030303010303UL, 0x0303030303010301UL, 0x0303030303030101UL, 0x0103030303010303UL,
        0x0103030303030303UL, 0x0103030303030303UL, 0x0103030303030301UL, 0x0103030303030303UL,
        0x0103030303030303UL, 0x0103030303030303UL, 0x0103030303030303UL, 0x0103030303030301UL,
        0x0103030303030303UL, 0x0103030303030303UL, 0x0103030303030303UL, 0x0103030303030303UL,
        0x0103030303030303UL, 0x0103030303030301UL, 0x0103030303030303UL, 0x0103030303010303UL,
        0x0103030303030303UL, 0x0103030303010303UL, 0x0103030303030301UL, 0x0103010303030303UL,
        0x0103030303030303UL, 0x0103030103030303UL, 0x0103030303030303UL, 0x0303030303030301UL,
    ];
    static readonly byte[] Mode2IndexLoBits =
    [
        14, 14, 15, 14, 15, 14, 14, 15, 15, 15, 14, 14, 14, 14, 14, 14,
        14, 14, 15, 14, 14, 14, 14, 15, 13, 15, 14, 14, 15, 14, 15, 15,
        15, 14, 14, 14, 14, 15, 15, 15, 14, 14, 15, 14, 14, 14, 14, 15,
        14, 14, 14, 14, 14, 15, 14, 15, 14, 15, 15, 15, 14, 15, 14, 14,
    ];

    // Per-partition endpoint-gather control for the interpolation stage: 16 pixels x
    // (e0, e1) byte selectors = 32 lanes, packed as 4 ulongs per partition. Lane 2i / 2i+1
    // hold 2*subset[i] / 2*subset[i]+1 — the [e0 e1] pair for pixel i's subset (subset in
    // {0,1,2}, so selectors in {0..5}) — and feed mm256_shuffle_epi8 to pull each pixel's
    // endpoint pair out of the 8-byte lane vector.
    static readonly ulong[] Mode2Gather =
    [
        0x0302030201000100UL, 0x0302030201000100UL, 0x0302050405040100UL, 0x0504050405040504UL, // p0
        0x0302010001000100UL, 0x0302030201000100UL, 0x0302030205040504UL, 0x0302050405040504UL, // p1
        0x0100010001000100UL, 0x0302010001000504UL, 0x0302030205040504UL, 0x0302030205040504UL, // p2
        0x0504050405040100UL, 0x0504050401000100UL, 0x0302030201000100UL, 0x0302030203020100UL, // p3
        0x0100010001000100UL, 0x0100010001000100UL, 0x0504050403020302UL, 0x0504050403020302UL, // p4
        0x0302030201000100UL, 0x0302030201000100UL, 0x0504050401000100UL, 0x0504050401000100UL, // p5
        0x0504050401000100UL, 0x0504050401000100UL, 0x0302030203020302UL, 0x0302030203020302UL, // p6
        0x0302030201000100UL, 0x0302030201000100UL, 0x0302030205040504UL, 0x0302030205040504UL, // p7
        0x0100010001000100UL, 0x0100010001000100UL, 0x0302030203020302UL, 0x0504050405040504UL, // p8
        0x0100010001000100UL, 0x0302030203020302UL, 0x0302030203020302UL, 0x0504050405040504UL, // p9
        0x0100010001000100UL, 0x0302030203020302UL, 0x0504050405040504UL, 0x0504050405040504UL, // p10
        0x0504030201000100UL, 0x0504030201000100UL, 0x0504030201000100UL, 0x0504030201000100UL, // p11
        0x0504030203020100UL, 0x0504030203020100UL, 0x0504030203020100UL, 0x0504030203020100UL, // p12
        0x0504050403020100UL, 0x0504050403020100UL, 0x0504050403020100UL, 0x0504050403020100UL, // p13
        0x0302030201000100UL, 0x0504030203020100UL, 0x0504050403020302UL, 0x0504050405040302UL, // p14
        0x0302030201000100UL, 0x0302010001000504UL, 0x0100010005040504UL, 0x0100050405040504UL, // p15
        0x0302010001000100UL, 0x0302030201000100UL, 0x0504030203020100UL, 0x0504050403020302UL, // p16
        0x0302030203020100UL, 0x0302030201000100UL, 0x0302010001000504UL, 0x0100010005040504UL, // p17
        0x0100010001000100UL, 0x0504050403020302UL, 0x0504050403020302UL, 0x0504050403020302UL, // p18
        0x0504050401000100UL, 0x0504050401000100UL, 0x0504050401000100UL, 0x0302030203020302UL, // p19
        0x0302030203020100UL, 0x0302030203020100UL, 0x0504050405040100UL, 0x0504050405040100UL, // p20
        0x0302010001000100UL, 0x0302010001000100UL, 0x0302050405040504UL, 0x0302050405040504UL, // p21
        0x0100010001000100UL, 0x0302030201000100UL, 0x0504050403020100UL, 0x0504050403020100UL, // p22
        0x0100010001000100UL, 0x0100010003020302UL, 0x0100030205040504UL, 0x0100030205040504UL, // p23
        0x0504050403020100UL, 0x0504050403020100UL, 0x0302030201000100UL, 0x0100010001000100UL, // p24
        0x0504030201000100UL, 0x0504030201000100UL, 0x0504050403020302UL, 0x0504050405040504UL, // p25
        0x0100030203020100UL, 0x0302050405040302UL, 0x0302050405040302UL, 0x0100030203020100UL, // p26
        0x0100010001000100UL, 0x0100030203020100UL, 0x0302050405040302UL, 0x0302050405040302UL, // p27
        0x0504050401000100UL, 0x0504010003020302UL, 0x0504010003020302UL, 0x0504050401000100UL, // p28
        0x0100030203020100UL, 0x0100030203020100UL, 0x0504010001000504UL, 0x0504050405040504UL, // p29
        0x0302030201000100UL, 0x0504050403020100UL, 0x0504050403020100UL, 0x0302030201000100UL, // p30
        0x0100010001000100UL, 0x0100010001000504UL, 0x0302030205040504UL, 0x0302050405040504UL, // p31
        0x0100010001000100UL, 0x0504010001000100UL, 0x0504050403020302UL, 0x0504050405040302UL, // p32
        0x0504050405040100UL, 0x0504050401000100UL, 0x0504030201000100UL, 0x0302030201000100UL, // p33
        0x0302030201000100UL, 0x0504030201000100UL, 0x0504050401000100UL, 0x0504050405040100UL, // p34
        0x0100050403020100UL, 0x0100050403020100UL, 0x0100050403020100UL, 0x0100050403020100UL, // p35
        0x0100010001000100UL, 0x0302030203020302UL, 0x0504050405040504UL, 0x0100010001000100UL, // p36
        0x0100050403020100UL, 0x0302010005040302UL, 0x0504030201000504UL, 0x0100050403020100UL, // p37
        0x0100050403020100UL, 0x0504030201000504UL, 0x0302010005040302UL, 0x0100050403020100UL, // p38
        0x0302030201000100UL, 0x0100010005040504UL, 0x0504050403020302UL, 0x0302030201000100UL, // p39
        0x0302030201000100UL, 0x0504050403020302UL, 0x0100010005040504UL, 0x0302030201000100UL, // p40
        0x0302010003020100UL, 0x0302010003020100UL, 0x0504050405040504UL, 0x0504050405040504UL, // p41
        0x0100010001000100UL, 0x0100010001000100UL, 0x0302050403020504UL, 0x0302050403020504UL, // p42
        0x0504050401000100UL, 0x0504050403020302UL, 0x0504050401000100UL, 0x0504050403020302UL, // p43
        0x0504050401000100UL, 0x0302030201000100UL, 0x0504050401000100UL, 0x0302030201000100UL, // p44
        0x0100050405040100UL, 0x0302050405040302UL, 0x0100050405040100UL, 0x0302050405040302UL, // p45
        0x0302010003020100UL, 0x0504050405040504UL, 0x0504050405040504UL, 0x0302010003020100UL, // p46
        0x0100010001000100UL, 0x0302050403020504UL, 0x0302050403020504UL, 0x0302050403020504UL, // p47
        0x0302010003020100UL, 0x0302010003020100UL, 0x0302010003020100UL, 0x0504050405040504UL, // p48
        0x0504050405040100UL, 0x0302030203020100UL, 0x0504050405040100UL, 0x0302030203020100UL, // p49
        0x0504010001000100UL, 0x0504030203020302UL, 0x0504010001000100UL, 0x0504030203020302UL, // p50
        0x0100010001000100UL, 0x0504030203020504UL, 0x0504030203020504UL, 0x0504030203020504UL, // p51
        0x0504050405040100UL, 0x0302030203020100UL, 0x0302030203020100UL, 0x0504050405040100UL, // p52
        0x0504010001000100UL, 0x0504030203020302UL, 0x0504030203020302UL, 0x0504010001000100UL, // p53
        0x0100030203020100UL, 0x0100030203020100UL, 0x0100030203020100UL, 0x0504050405040504UL, // p54
        0x0100010001000100UL, 0x0100010001000100UL, 0x0504030203020504UL, 0x0504030203020504UL, // p55
        0x0100030203020100UL, 0x0100030203020100UL, 0x0504050405040504UL, 0x0504050405040504UL, // p56
        0x0504050401000100UL, 0x0302030201000100UL, 0x0302030201000100UL, 0x0504050401000100UL, // p57
        0x0504050401000100UL, 0x0504050403020302UL, 0x0504050403020302UL, 0x0504050401000100UL, // p58
        0x0100010001000100UL, 0x0100010001000100UL, 0x0100010001000100UL, 0x0504030203020504UL, // p59
        0x0504010001000100UL, 0x0302010001000100UL, 0x0504010001000100UL, 0x0302010001000100UL, // p60
        0x0504050405040100UL, 0x0504050405040302UL, 0x0504050405040100UL, 0x0504050405040302UL, // p61
        0x0302010003020100UL, 0x0504050405040504UL, 0x0504050405040504UL, 0x0504050405040504UL, // p62
        0x0302030203020100UL, 0x0302030201000504UL, 0x0302010005040504UL, 0x0100050405040504UL, // p63
    ];
    // csharpier-ignore-end

    // BC7 Mode 2
    //
    // Mode 2 is laid out like this
    // | Bit Range | Width | Field
    // |  0 ..  2  |   3   | mode marker
    // |  3 ..  8  |   6   | partition index        (0..63)
    // |  9 .. 38  |  30   | red   endpoints R0..R5 (6 x 5 bits)
    // | 39 .. 68  |  30   | green endpoints G0..G5 (6 x 5 bits)
    // | 69 .. 98  |  30   | blue  endpoints B0..B5 (6 x 5 bits)
    // | 99 .. 127 |  29   | colour indices         (16 texels, 1/2 bits each)
    //
    // Mode 2 decoding should work like this:
    //   - The 6-bit partition index selects one of 64 fixed patterns (see the partition table)
    //     that splits the 16 texels into 3 subsets, assigning each texel a subset 0/1/2.
    //   - Each subset has two RGB endpoints; every endpoint is 5 bits per channel with no
    //     p-bit, unquantized to 8 bits as (v << 3) | (v >> 2).
    //   - Each texel carries a colour index selecting an interpolation weight from the 2-bit
    //     weight table. Indices are 2 bits, except the anchor texel of each subset, which is
    //     1 bit with an implied high bit of 0 (subset 0's anchor is texel 0; subsets 1 and 2
    //     use the fixed anchor tables).
    //   - Texel i's colour is, per channel, interpolate(e0, e1, w) = (e0*(64-w) + e1*w + 32) >> 6,
    //     where e0/e1 are the two endpoints of that texel's subset and w is its weight.
    //   - Alpha has no bits in this mode and is always opaque (255).
    static unsafe void Mode2Avx(ulong lo, ulong hi, byte* rgba)
    {
        static ulong Unquantize(ulong v) => (v << 3) | ((v >> 2) & 0x070707070707);

        if (IsAvx2Supported)
        {
            const ulong DepMask = 0x1F1F1F1F1F1Ful; // 6 five-bit endpoints, one per byte lane

            // Extract partition and endpoints. R lies wholly in lo, B wholly in hi; G straddles
            // the lo/hi boundary at bit 64 (G0..G4 in lo, G5 in hi). No p-bits in this mode.
            int partition = (int)((lo >> 3) & 0x3F);
            ulong rB = pdep_u64(lo >> 9, DepMask);
            ulong gB = pdep_u64((lo >> 39) | (hi << 25), DepMask);
            ulong bB = pdep_u64(hi >> 5, DepMask);

            // Unquantize: replicate the top 3 bits to the bottom, shift by 5
            rB = Unquantize(rB);
            gB = Unquantize(gB);
            bB = Unquantize(bB);

            ulong idata = hi >> (99 - 64);
            ulong ilo = pdep_u64(idata, Mode2IndexMaskLo[partition]);
            ulong ihi = pdep_u64(idata >> Mode2IndexLoBits[partition], Mode2IndexMaskHi[partition]);

            v128 w = shuffle_epi8(Weights2Vec, new(ilo, ihi));
            v128 invw = sub_epi8(new((byte)64), w); // 64 - w

            // Interleave (64-w, w) so we can use a pmaddubs to compute e0*(64-w) + e1*w
            v256 pairs = new(unpacklo_epi8(invw, w), unpackhi_epi8(invw, w));

            // Endpoint gather control for this partition
            v256 gather = new(
                Mode2Gather[partition * 4 + 0],
                Mode2Gather[partition * 4 + 1],
                Mode2Gather[partition * 4 + 2],
                Mode2Gather[partition * 4 + 3]
            );

            // e0*(64-w) + e1*w
            v256 resR = mm256_maddubs_epi16(mm256_shuffle_epi8(new(rB), gather), pairs);
            v256 resG = mm256_maddubs_epi16(mm256_shuffle_epi8(new(gB), gather), pairs);
            v256 resB = mm256_maddubs_epi16(mm256_shuffle_epi8(new(bB), gather), pairs);

            // (res + 32) >> 6
            resR = mm256_srli_epi16(mm256_add_epi16(resR, new((ushort)32)), 6);
            resG = mm256_srli_epi16(mm256_add_epi16(resG, new((ushort)32)), 6);
            resB = mm256_srli_epi16(mm256_add_epi16(resB, new((ushort)32)), 6);

            // Transpose the three planar channels (+ constant opaque alpha) to interleaved
            // RGBA, exactly as in Mode 0/1.
            var rg = mm256_or_si256(resR, mm256_slli_epi16(resG, 8));
            var ba = mm256_or_si256(resB, new((ushort)0xFF00));
            var rgbaLo = mm256_unpacklo_epi16(rg, ba); // RGBA, pixels {0..3, 8..11}
            var rgbaHi = mm256_unpackhi_epi16(rg, ba); // RGBA, pixels {4..7, 12..15}

            var output = (v256*)rgba;
            output[0] = mm256_permute2x128_si256(rgbaLo, rgbaHi, 0x20); // pixels 0..7
            output[1] = mm256_permute2x128_si256(rgbaLo, rgbaHi, 0x31); // pixels 8..15
        }
    }
    #endregion

    #region Mode 3
    // The Mode 3 index stream starts at bit 98 (4 mode terminator + 6 partition + 12*7
    // endpoint + 4 p-bit) and is always exactly 30 bits (16*2 minus the 2 anchor MSBs), so
    // it lies entirely in hi (bits 34..63) and right-aligns with a single shift.
    //
    // Per-partition pdep scatter masks for the Mode 3 index stream, precomputed for all 64
    // partitions: lane i takes pixel i's 2-bit index, except the 2 anchor lanes which take
    // 1 bit so their forced-zero MSB falls out for free. Mode3IndexLoBits[p] = popcount of
    // maskLo is how far to shift the packed run before depositing lanes 8..15. Derived from
    // AnchorIndex2_1 (subset 1's anchor; subset 0's is always pixel 0) with width 2.
    //
    // The endpoint-gather control is identical to Mode 1's (both split the block into two
    // subsets by indexing PartitionTable2), so Mode1Gather is reused directly below.
    // csharpier-ignore-start
    static readonly ulong[] Mode3IndexMaskLo =
    [
        0x0303030303030301UL, 0x0303030303030301UL, 0x0303030303030301UL, 0x0303030303030301UL,
        0x0303030303030301UL, 0x0303030303030301UL, 0x0303030303030301UL, 0x0303030303030301UL,
        0x0303030303030301UL, 0x0303030303030301UL, 0x0303030303030301UL, 0x0303030303030301UL,
        0x0303030303030301UL, 0x0303030303030301UL, 0x0303030303030301UL, 0x0303030303030301UL,
        0x0303030303030301UL, 0x0303030303010301UL, 0x0303030303030301UL, 0x0303030303010301UL,
        0x0303030303010301UL, 0x0303030303030301UL, 0x0303030303030301UL, 0x0303030303030301UL,
        0x0303030303010301UL, 0x0303030303030301UL, 0x0303030303010301UL, 0x0303030303010301UL,
        0x0303030303030301UL, 0x0303030303030301UL, 0x0303030303010301UL, 0x0303030303010301UL,
        0x0303030303030301UL, 0x0303030303030301UL, 0x0301030303030301UL, 0x0303030303030301UL,
        0x0303030303010301UL, 0x0303030303030301UL, 0x0303030303030301UL, 0x0303030303030301UL,
        0x0303030303010301UL, 0x0303030303030301UL, 0x0303030303010301UL, 0x0303030303010301UL,
        0x0303030303010301UL, 0x0303030303030301UL, 0x0303030303030301UL, 0x0301030303030301UL,
        0x0301030303030301UL, 0x0303030303010301UL, 0x0301030303030301UL, 0x0303030303030301UL,
        0x0303030303030301UL, 0x0303030303030301UL, 0x0303030303010301UL, 0x0303030303010301UL,
        0x0303030303030301UL, 0x0303030303030301UL, 0x0303030303030301UL, 0x0303030303030301UL,
        0x0303030303030301UL, 0x0303030303010301UL, 0x0303030303010301UL, 0x0303030303030301UL,
    ];
    static readonly ulong[] Mode3IndexMaskHi =
    [
        0x0103030303030303UL, 0x0103030303030303UL, 0x0103030303030303UL, 0x0103030303030303UL,
        0x0103030303030303UL, 0x0103030303030303UL, 0x0103030303030303UL, 0x0103030303030303UL,
        0x0103030303030303UL, 0x0103030303030303UL, 0x0103030303030303UL, 0x0103030303030303UL,
        0x0103030303030303UL, 0x0103030303030303UL, 0x0103030303030303UL, 0x0103030303030303UL,
        0x0103030303030303UL, 0x0303030303030303UL, 0x0303030303030301UL, 0x0303030303030303UL,
        0x0303030303030303UL, 0x0303030303030301UL, 0x0303030303030301UL, 0x0103030303030303UL,
        0x0303030303030303UL, 0x0303030303030301UL, 0x0303030303030303UL, 0x0303030303030303UL,
        0x0303030303030301UL, 0x0303030303030301UL, 0x0303030303030303UL, 0x0303030303030303UL,
        0x0103030303030303UL, 0x0103030303030303UL, 0x0303030303030303UL, 0x0303030303030301UL,
        0x0303030303030303UL, 0x0303030303030301UL, 0x0103030303030303UL, 0x0103030303030303UL,
        0x0303030303030303UL, 0x0303030303030301UL, 0x0303030303030303UL, 0x0303030303030303UL,
        0x0303030303030303UL, 0x0103030303030303UL, 0x0103030303030303UL, 0x0303030303030303UL,
        0x0303030303030303UL, 0x0303030303030303UL, 0x0303030303030303UL, 0x0303030303030301UL,
        0x0103030303030303UL, 0x0103030303030303UL, 0x0303030303030303UL, 0x0303030303030303UL,
        0x0103030303030303UL, 0x0103030303030303UL, 0x0103030303030303UL, 0x0103030303030303UL,
        0x0103030303030303UL, 0x0303030303030303UL, 0x0303030303030303UL, 0x0103030303030303UL,
    ];
    static readonly byte[] Mode3IndexLoBits =
    [
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
        15, 14, 15, 14, 14, 15, 15, 15, 14, 15, 14, 14, 15, 15, 14, 14,
        15, 15, 14, 15, 14, 15, 15, 15, 14, 15, 14, 14, 14, 15, 15, 14,
        14, 14, 14, 15, 15, 15, 14, 14, 15, 15, 15, 15, 15, 14, 14, 15,
    ];
    // csharpier-ignore-end

    // BC7 Mode 3
    //
    // Mode 3 is laid out like this
    // | Bit Range | Width | Field
    // |  0 ..  3  |   4   | mode marker
    // |  4 ..  9  |   6   | partition index        (0..63)
    // | 10 .. 37  |  28   | red   endpoints R0..R3 (4 x 7 bits)
    // | 38 .. 65  |  28   | green endpoints G0..G3 (4 x 7 bits)
    // | 66 .. 93  |  28   | blue  endpoints B0..B3 (4 x 7 bits)
    // | 94 .. 97  |   4   | P-bits                 (4 x 1 bits, one per endpoint)
    // | 98 .. 127 |  30   | colour indices         (16 texels, 1/2 bits each)
    //
    // Mode 3 decoding should work like this:
    //   - The 6-bit partition index selects one of 64 fixed patterns (see the partition table)
    //     that splits the 16 texels into 2 subsets, assigning each texel a subset 0/1.
    //   - Each subset has two RGB endpoints; every endpoint is 7 bits per channel plus its own
    //     p-bit, forming a full 8-bit value directly — no unquantization is needed.
    //   - Each texel carries a colour index selecting an interpolation weight from the 2-bit
    //     weight table. Indices are 2 bits, except the anchor texel of each subset, which is
    //     1 bit with an implied high bit of 0 (subset 0's anchor is texel 0; subset 1 uses the
    //     fixed anchor table).
    //   - Texel i's colour is, per channel, interpolate(e0, e1, w) = (e0*(64-w) + e1*w + 32) >> 6,
    //     where e0/e1 are the two endpoints of that texel's subset and w is its weight.
    //   - Alpha has no bits in this mode and is always opaque (255).
    static unsafe void Mode3Avx(ulong lo, ulong hi, byte* rgba)
    {
        if (IsAvx2Supported)
        {
            const ulong DepMask = 0x7F7F7F7Ful << 1; // 7-bit endpoint at bits 1..7, bit 0 free
            const ulong PbtMask = 0x01010101ul; // 4 unique p-bits, one per endpoint byte

            // Extract partition and endpoints. R lies wholly in lo, B wholly in hi; G straddles
            // the lo/hi boundary at bit 64 (G0..G2 and G3's low 5 bits in lo, G3's top 2 in hi).
            int partition = (int)((lo >> 4) & 0x3F);
            ulong rB = pdep_u64(lo >> 10, DepMask);
            ulong gB = pdep_u64((lo >> 38) | (hi << 26), DepMask);
            ulong bB = pdep_u64(hi >> 2, DepMask);

            // Four unique p-bits, one per endpoint (pb0->s0e0, pb1->s0e1, pb2->s1e0, pb3->s1e1),
            // deposited into bit 0 of each endpoint's byte lane.
            ulong pB = pdep_u64(hi >> 30, PbtMask);

            // 7 bits + p-bit exactly fills a byte, so the merged value is already the final
            // 8-bit channel — unlike Modes 0-2 there is no unquantize step.
            rB |= pB;
            gB |= pB;
            bB |= pB;

            ulong idata = hi >> (98 - 64);
            ulong ilo = pdep_u64(idata, Mode3IndexMaskLo[partition]);
            ulong ihi = pdep_u64(idata >> Mode3IndexLoBits[partition], Mode3IndexMaskHi[partition]);

            v128 w = shuffle_epi8(Weights2Vec, new(ilo, ihi));
            v128 invw = sub_epi8(new((byte)64), w); // 64 - w

            // Interleave (64-w, w) so we can use a pmaddubs to compute e0*(64-w) + e1*w
            v256 pairs = new(unpacklo_epi8(invw, w), unpackhi_epi8(invw, w));

            // Endpoint gather control for this partition (shared with Mode 1: same 2-subset
            // PartitionTable2 layout, selectors in {0..3}).
            v256 gather = new(
                Mode1Gather[partition * 4 + 0],
                Mode1Gather[partition * 4 + 1],
                Mode1Gather[partition * 4 + 2],
                Mode1Gather[partition * 4 + 3]
            );

            // e0*(64-w) + e1*w
            v256 resR = mm256_maddubs_epi16(mm256_shuffle_epi8(new(rB), gather), pairs);
            v256 resG = mm256_maddubs_epi16(mm256_shuffle_epi8(new(gB), gather), pairs);
            v256 resB = mm256_maddubs_epi16(mm256_shuffle_epi8(new(bB), gather), pairs);

            // (res + 32) >> 6
            resR = mm256_srli_epi16(mm256_add_epi16(resR, new((ushort)32)), 6);
            resG = mm256_srli_epi16(mm256_add_epi16(resG, new((ushort)32)), 6);
            resB = mm256_srli_epi16(mm256_add_epi16(resB, new((ushort)32)), 6);

            // Transpose the three planar channels (+ constant opaque alpha) to interleaved
            // RGBA, exactly as in Mode 0/1/2.
            var rg = mm256_or_si256(resR, mm256_slli_epi16(resG, 8));
            var ba = mm256_or_si256(resB, new((ushort)0xFF00));
            var rgbaLo = mm256_unpacklo_epi16(rg, ba); // RGBA, pixels {0..3, 8..11}
            var rgbaHi = mm256_unpackhi_epi16(rg, ba); // RGBA, pixels {4..7, 12..15}

            var output = (v256*)rgba;
            output[0] = mm256_permute2x128_si256(rgbaLo, rgbaHi, 0x20); // pixels 0..7
            output[1] = mm256_permute2x128_si256(rgbaLo, rgbaHi, 0x31); // pixels 8..15
        }
    }
    #endregion

    #region Mode 4
    // BC7 Mode 4
    //
    // Mode 4 is laid out like this
    // | Bit Range | Width | Field
    // |  0 ..  4  |   5   | mode marker
    // |  5 ..  6  |   2   | rotation
    // |     7     |   1   | index selection (idxMode)
    // |  8 .. 37  |  30   | RGB endpoints R0,R1,G0,G1,B0,B1 (6 x 5 bits)
    // | 38 .. 49  |  12   | alpha endpoints A0,A1           (2 x 6 bits)
    // | 50 .. 80  |  31   | index set A (2-bit, 16 texels, texel 0 anchored to 1 bit)
    // | 81 .. 127 |  47   | index set B (3-bit, 16 texels, texel 0 anchored to 2 bits)
    //
    // Mode 4 decoding should work like this:
    //   - There is a single subset: all 16 texels share one endpoint pair per channel, so there
    //     is no partition and no per-pixel endpoint gather — every texel selects (e0, e1) through
    //     the same constant [0, 1] control.
    //   - RGB endpoints are 5 bits, unquantized 5->8 as (v << 3) | (v >> 2); alpha endpoints are
    //     6 bits, unquantized 6->8 as (v << 2) | (v >> 4). Mode 4 has no p-bits.
    //   - Two index sets are stored back to back: a 2-bit set then a 3-bit set, each with texel 0
    //     as the sole anchor (1 bit / 2 bits respectively, with an implied high bit of 0). idxMode
    //     selects which set drives colour and which drives alpha: idxMode 0 -> colour uses the
    //     2-bit set and alpha the 3-bit set; idxMode 1 -> the two are swapped.
    //   - Texel i's channel value is interpolate(e0, e1, w) = (e0*(64-w) + e1*w + 32) >> 6, where
    //     w comes from the selected weight table (Weights2 for the 2-bit set, Weights3 for the 3-bit).
    //   - Rotation then swaps the decoded alpha with one colour channel (1:R, 2:G, 3:B; 0: none).
    //
    // Because there is a single subset and a single anchor at a fixed position, the endpoint gather
    // and both index-scatter masks are compile-time constants — Mode 4 adds no lookup tables.
    static unsafe void Mode4Avx(ulong lo, ulong hi, byte* rgba)
    {
        if (IsAvx2Supported)
        {
            const ulong GatherConst = 0x0100010001000100ul; // broadcast: selects e0,e1 per texel
            const ulong Idx2MaskLo = 0x0303030303030301ul; // 2-bit set: byte 0 = 1 bit (anchor)
            const ulong Idx2MaskHi = 0x0303030303030303ul;
            const int Idx2LoBits = 15; // 1 + 7*2
            const ulong Idx3MaskLo = 0x0707070707070703ul; // 3-bit set: byte 0 = 2 bits (anchor)
            const ulong Idx3MaskHi = 0x0707070707070707ul;
            const int Idx3LoBits = 23; // 2 + 7*3

            int rotation = (int)((lo >> 5) & 0x3);
            int idxMode = (int)((lo >> 7) & 0x1);

            // Endpoints all lie in lo (no lo/hi straddle). Two per channel into byte lanes 0,1;
            // unquantize 5->8 for RGB and 6->8 for A. No p-bits.
            ulong rB = pdep_u64(lo >> 8, 0x1F1Ful);
            ulong gB = pdep_u64(lo >> 18, 0x1F1Ful);
            ulong bB = pdep_u64(lo >> 28, 0x1F1Ful);
            ulong aB = pdep_u64(lo >> 38, 0x3F3Ful);
            rB = (rB << 3) | ((rB >> 2) & 0x0707ul);
            gB = (gB << 3) | ((gB >> 2) & 0x0707ul);
            bB = (bB << 3) | ((bB >> 2) & 0x0707ul);
            aB = (aB << 2) | ((aB >> 4) & 0x0303ul);

            // Index set A: 2-bit, starts at bit 50 and straddles the lo/hi boundary.
            ulong idata2 = (lo >> 50) | (hi << 14);
            ulong ilo2 = pdep_u64(idata2, Idx2MaskLo);
            ulong ihi2 = pdep_u64(idata2 >> Idx2LoBits, Idx2MaskHi);
            v128 w2 = shuffle_epi8(Weights2Vec, new(ilo2, ihi2));

            // Index set B: 3-bit, starts at bit 81 and lies wholly in hi.
            ulong idata3 = hi >> (81 - 64);
            ulong ilo3 = pdep_u64(idata3, Idx3MaskLo);
            ulong ihi3 = pdep_u64(idata3 >> Idx3LoBits, Idx3MaskHi);
            v128 w3 = shuffle_epi8(Weights3Vec, new(ilo3, ihi3));

            // idxMode chooses which set weights colour vs. alpha.
            v128 wColor = idxMode == 0 ? w2 : w3;
            v128 wAlpha = idxMode == 0 ? w3 : w2;

            v128 invC = sub_epi8(new((byte)64), wColor); // 64 - w
            v256 pairsColor = new(unpacklo_epi8(invC, wColor), unpackhi_epi8(invC, wColor));
            v128 invA = sub_epi8(new((byte)64), wAlpha);
            v256 pairsAlpha = new(unpacklo_epi8(invA, wAlpha), unpackhi_epi8(invA, wAlpha));

            // Single subset: the gather is a constant that scatters e0,e1 to every texel.
            v256 gather = new(GatherConst);

            v256 resR = mm256_maddubs_epi16(mm256_shuffle_epi8(new(rB), gather), pairsColor);
            v256 resG = mm256_maddubs_epi16(mm256_shuffle_epi8(new(gB), gather), pairsColor);
            v256 resB = mm256_maddubs_epi16(mm256_shuffle_epi8(new(bB), gather), pairsColor);
            v256 resA = mm256_maddubs_epi16(mm256_shuffle_epi8(new(aB), gather), pairsAlpha);

            resR = mm256_srli_epi16(mm256_add_epi16(resR, new((ushort)32)), 6);
            resG = mm256_srli_epi16(mm256_add_epi16(resG, new((ushort)32)), 6);
            resB = mm256_srli_epi16(mm256_add_epi16(resB, new((ushort)32)), 6);
            resA = mm256_srli_epi16(mm256_add_epi16(resA, new((ushort)32)), 6);

            // Rotation swaps alpha with one colour channel, applied to the planar vectors so it
            // costs one vector swap rather than a per-texel shuffle.
            v256 chR = resR;
            v256 chG = resG;
            v256 chB = resB;
            v256 chA = resA;
            switch (rotation)
            {
                case 1:
                    chR = resA;
                    chA = resR;
                    break;
                case 2:
                    chG = resA;
                    chA = resG;
                    break;
                case 3:
                    chB = resA;
                    chA = resB;
                    break;
            }

            // Transpose the four planar channels to interleaved RGBA (real alpha this time).
            var rg = mm256_or_si256(chR, mm256_slli_epi16(chG, 8));
            var ba = mm256_or_si256(chB, mm256_slli_epi16(chA, 8));
            var rgbaLo = mm256_unpacklo_epi16(rg, ba); // RGBA, pixels {0..3, 8..11}
            var rgbaHi = mm256_unpackhi_epi16(rg, ba); // RGBA, pixels {4..7, 12..15}

            var output = (v256*)rgba;
            output[0] = mm256_permute2x128_si256(rgbaLo, rgbaHi, 0x20); // pixels 0..7
            output[1] = mm256_permute2x128_si256(rgbaLo, rgbaHi, 0x31); // pixels 8..15
        }
    }
    #endregion

    #region Mode 5
    // BC7 Mode 5
    //
    // Mode 5 is laid out like this
    // | Bit Range | Width | Field
    // |  0 ..  5  |   6   | mode marker
    // |  6 ..  7  |   2   | rotation
    // |  8 .. 21  |  14   | red   endpoints R0,R1 (2 x 7 bits)
    // | 22 .. 35  |  14   | green endpoints G0,G1 (2 x 7 bits)
    // | 36 .. 49  |  14   | blue  endpoints B0,B1 (2 x 7 bits)
    // | 50 .. 65  |  16   | alpha endpoints A0,A1 (2 x 8 bits)
    // | 66 .. 96  |  31   | colour indices (2-bit, 16 texels, texel 0 anchored to 1 bit)
    // | 97 .. 127 |  31   | alpha  indices (2-bit, 16 texels, texel 0 anchored to 1 bit)
    //
    // Mode 5 decoding should work like this:
    //   - Like Mode 4 there is a single subset, so there is no partition and no per-pixel endpoint
    //     gather — every texel selects (e0, e1) through the same constant [0, 1] control.
    //   - RGB endpoints are 7 bits, unquantized 7->8 as (v << 1) | (v >> 6); alpha endpoints are a
    //     full 8 bits and need no unquantization. Mode 5 has no p-bits.
    //   - There are two 2-bit index sets with fixed roles (unlike Mode 4 there is no idxMode): the
    //     first set always weights colour, the second always weights alpha, both via Weights2.
    //     Texel 0 is the sole anchor of each set (1 bit, implied high bit 0).
    //   - Texel i's channel value is interpolate(e0, e1, w) = (e0*(64-w) + e1*w + 32) >> 6.
    //   - Rotation then swaps the decoded alpha with one colour channel (1:R, 2:G, 3:B; 0: none).
    //
    // A1 is the one endpoint that straddles the lo/hi boundary (low 6 bits in lo, top 2 in hi).
    // As in Mode 4, the single subset and fixed anchor make the gather and index masks constant, so
    // Mode 5 adds no lookup tables.
    static unsafe void Mode5Avx(ulong lo, ulong hi, byte* rgba)
    {
        if (IsAvx2Supported)
        {
            const ulong GatherConst = 0x0100010001000100ul; // broadcast: selects e0,e1 per texel
            const ulong IdxMaskLo = 0x0303030303030301ul; // 2-bit set: byte 0 = 1 bit (anchor)
            const ulong IdxMaskHi = 0x0303030303030303ul;
            const int IdxLoBits = 15; // 1 + 7*2

            int rotation = (int)((lo >> 6) & 0x3);

            // R/G/B endpoints are 7-bit and lie in lo; unquantize 7->8. Alpha is 8-bit with A1
            // straddling the lo/hi boundary, so join lo and hi and take the low two bytes as-is.
            ulong rB = pdep_u64(lo >> 8, 0x7F7Ful);
            ulong gB = pdep_u64(lo >> 22, 0x7F7Ful);
            ulong bB = pdep_u64(lo >> 36, 0x7F7Ful);
            rB = (rB << 1) | ((rB >> 6) & 0x0101ul);
            gB = (gB << 1) | ((gB >> 6) & 0x0101ul);
            bB = (bB << 1) | ((bB >> 6) & 0x0101ul);
            ulong aB = ((lo >> 50) | (hi << 14)) & 0xFFFFul;

            // Colour index set: 2-bit, starts at bit 66 (hi bit 2), wholly in hi.
            ulong idataC = hi >> (66 - 64);
            ulong iloC = pdep_u64(idataC, IdxMaskLo);
            ulong ihiC = pdep_u64(idataC >> IdxLoBits, IdxMaskHi);
            v128 wColor = shuffle_epi8(Weights2Vec, new(iloC, ihiC));

            // Alpha index set: 2-bit, starts at bit 97 (hi bit 33), wholly in hi.
            ulong idataA = hi >> (97 - 64);
            ulong iloA = pdep_u64(idataA, IdxMaskLo);
            ulong ihiA = pdep_u64(idataA >> IdxLoBits, IdxMaskHi);
            v128 wAlpha = shuffle_epi8(Weights2Vec, new(iloA, ihiA));

            v128 invC = sub_epi8(new((byte)64), wColor); // 64 - w
            v256 pairsColor = new(unpacklo_epi8(invC, wColor), unpackhi_epi8(invC, wColor));
            v128 invA = sub_epi8(new((byte)64), wAlpha);
            v256 pairsAlpha = new(unpacklo_epi8(invA, wAlpha), unpackhi_epi8(invA, wAlpha));

            // Single subset: the gather is a constant that scatters e0,e1 to every texel.
            v256 gather = new(GatherConst);

            v256 resR = mm256_maddubs_epi16(mm256_shuffle_epi8(new(rB), gather), pairsColor);
            v256 resG = mm256_maddubs_epi16(mm256_shuffle_epi8(new(gB), gather), pairsColor);
            v256 resB = mm256_maddubs_epi16(mm256_shuffle_epi8(new(bB), gather), pairsColor);
            v256 resA = mm256_maddubs_epi16(mm256_shuffle_epi8(new(aB), gather), pairsAlpha);

            resR = mm256_srli_epi16(mm256_add_epi16(resR, new((ushort)32)), 6);
            resG = mm256_srli_epi16(mm256_add_epi16(resG, new((ushort)32)), 6);
            resB = mm256_srli_epi16(mm256_add_epi16(resB, new((ushort)32)), 6);
            resA = mm256_srli_epi16(mm256_add_epi16(resA, new((ushort)32)), 6);

            // Rotation swaps alpha with one colour channel, applied to the planar vectors.
            v256 chR = resR;
            v256 chG = resG;
            v256 chB = resB;
            v256 chA = resA;
            switch (rotation)
            {
                case 1:
                    chR = resA;
                    chA = resR;
                    break;
                case 2:
                    chG = resA;
                    chA = resG;
                    break;
                case 3:
                    chB = resA;
                    chA = resB;
                    break;
            }

            // Transpose the four planar channels to interleaved RGBA (real alpha).
            var rg = mm256_or_si256(chR, mm256_slli_epi16(chG, 8));
            var ba = mm256_or_si256(chB, mm256_slli_epi16(chA, 8));
            var rgbaLo = mm256_unpacklo_epi16(rg, ba); // RGBA, pixels {0..3, 8..11}
            var rgbaHi = mm256_unpackhi_epi16(rg, ba); // RGBA, pixels {4..7, 12..15}

            var output = (v256*)rgba;
            output[0] = mm256_permute2x128_si256(rgbaLo, rgbaHi, 0x20); // pixels 0..7
            output[1] = mm256_permute2x128_si256(rgbaLo, rgbaHi, 0x31); // pixels 8..15
        }
    }
    #endregion

    #region Mode 6
    // BC7 Mode 6
    //
    // Mode 6 is laid out like this
    // | Bit Range | Width | Field
    // |  0 ..  6  |   7   | mode marker
    // |  7 .. 20  |  14   | red   endpoints R0,R1 (2 x 7 bits)
    // | 21 .. 34  |  14   | green endpoints G0,G1 (2 x 7 bits)
    // | 35 .. 48  |  14   | blue  endpoints B0,B1 (2 x 7 bits)
    // | 49 .. 62  |  14   | alpha endpoints A0,A1 (2 x 7 bits)
    // | 63 .. 64  |   2   | P-bits pb0,pb1 (pb0 -> every e0, pb1 -> every e1)
    // | 65 .. 127 |  63   | RGBA indices (4-bit, 16 texels, texel 0 anchored to 3 bits)
    //
    // Mode 6 decoding should work like this:
    //   - There is a single subset, so there is no partition and no per-pixel endpoint gather —
    //     every texel selects (e0, e1) through the same constant [0, 1] control.
    //   - Every endpoint is 7 bits per channel plus a shared p-bit — pb0 for all four e0 channels,
    //     pb1 for all four e1 channels — forming a full 8-bit value directly, so no unquantization
    //     is needed (as in Mode 3).
    //   - A single 4-bit index set drives all four channels (no rotation, no idxMode). Texel 0 is
    //     the sole anchor (3 bits, implied high bit 0); the rest are 4 bits.
    //   - Texel i's channel value is interpolate(e0, e1, w) = (e0*(64-w) + e1*w + 32) >> 6, with w
    //     from the 4-bit weight table.
    //
    // Only pb1 and the index stream lie in hi; all eight endpoints and pb0 are in lo, so no
    // endpoint straddles. The single subset and fixed anchor keep the gather and index mask
    // constant, so Mode 6 adds no lookup tables.
    static unsafe void Mode6Avx(ulong lo, ulong hi, byte* rgba)
    {
        if (IsAvx2Supported)
        {
            const ulong DepMask = 0x7F7Ful << 1; // 7-bit endpoint at bits 1..7, bit 0 free
            const ulong PbtMask = 0x0101ul; // pb0 -> byte 0 bit 0, pb1 -> byte 1 bit 0
            const ulong GatherConst = 0x0100010001000100ul; // broadcast: selects e0,e1 per texel
            const ulong IdxMaskLo = 0x0F0F0F0F0F0F0F07ul; // 4-bit set: byte 0 = 3 bits (anchor)
            const ulong IdxMaskHi = 0x0F0F0F0F0F0F0F0Ful;
            const int IdxLoBits = 31; // 3 + 7*4

            // All eight endpoints lie in lo; deposit each channel's two 7-bit values into byte
            // lanes 0,1 leaving bit 0 for the p-bit.
            ulong rB = pdep_u64(lo >> 7, DepMask);
            ulong gB = pdep_u64(lo >> 21, DepMask);
            ulong bB = pdep_u64(lo >> 35, DepMask);
            ulong aB = pdep_u64(lo >> 49, DepMask);

            // Two p-bits: pb0 (bit 63, in lo) and pb1 (bit 64, in hi). pb0 fills bit 0 of every
            // e0 lane, pb1 bit 0 of every e1 lane.
            ulong pB = pdep_u64(((lo >> 63) | (hi << 1)) & 0x3ul, PbtMask);

            // 7 bits + p-bit exactly fills a byte, so the merged value is already the final 8-bit
            // channel — no unquantize.
            rB |= pB;
            gB |= pB;
            bB |= pB;
            aB |= pB;

            // Single 4-bit index set, starts at bit 65 (hi bit 1), drives all four channels.
            ulong idata = hi >> (65 - 64);
            ulong ilo = pdep_u64(idata, IdxMaskLo);
            ulong ihi = pdep_u64(idata >> IdxLoBits, IdxMaskHi);
            v128 w = shuffle_epi8(Weights4Vec, new(ilo, ihi));

            v128 invw = sub_epi8(new((byte)64), w); // 64 - w
            v256 pairs = new(unpacklo_epi8(invw, w), unpackhi_epi8(invw, w));

            // Single subset: the gather is a constant that scatters e0,e1 to every texel.
            v256 gather = new(GatherConst);

            v256 resR = mm256_maddubs_epi16(mm256_shuffle_epi8(new(rB), gather), pairs);
            v256 resG = mm256_maddubs_epi16(mm256_shuffle_epi8(new(gB), gather), pairs);
            v256 resB = mm256_maddubs_epi16(mm256_shuffle_epi8(new(bB), gather), pairs);
            v256 resA = mm256_maddubs_epi16(mm256_shuffle_epi8(new(aB), gather), pairs);

            resR = mm256_srli_epi16(mm256_add_epi16(resR, new((ushort)32)), 6);
            resG = mm256_srli_epi16(mm256_add_epi16(resG, new((ushort)32)), 6);
            resB = mm256_srli_epi16(mm256_add_epi16(resB, new((ushort)32)), 6);
            resA = mm256_srli_epi16(mm256_add_epi16(resA, new((ushort)32)), 6);

            // Transpose the four planar channels to interleaved RGBA (real alpha).
            var rg = mm256_or_si256(resR, mm256_slli_epi16(resG, 8));
            var ba = mm256_or_si256(resB, mm256_slli_epi16(resA, 8));
            var rgbaLo = mm256_unpacklo_epi16(rg, ba); // RGBA, pixels {0..3, 8..11}
            var rgbaHi = mm256_unpackhi_epi16(rg, ba); // RGBA, pixels {4..7, 12..15}

            var output = (v256*)rgba;
            output[0] = mm256_permute2x128_si256(rgbaLo, rgbaHi, 0x20); // pixels 0..7
            output[1] = mm256_permute2x128_si256(rgbaLo, rgbaHi, 0x31); // pixels 8..15
        }
    }
    #endregion

    #region Mode 7
    // BC7 Mode 7
    //
    // Mode 7 is laid out like this
    // | Bit Range | Width | Field
    // |  0 ..  7  |   8   | mode marker
    // |  8 .. 13  |   6   | partition index        (0..63)
    // | 14 .. 33  |  20   | red   endpoints rS0E0,rS0E1,rS1E0,rS1E1 (4 x 5 bits)
    // | 34 .. 53  |  20   | green endpoints                         (4 x 5 bits)
    // | 54 .. 73  |  20   | blue  endpoints                         (4 x 5 bits)
    // | 74 .. 93  |  20   | alpha endpoints                         (4 x 5 bits)
    // | 94 .. 97  |   4   | P-bits pb0..pb3 (unique, one per endpoint)
    // | 98 .. 127 |  30   | colour indices (2-bit, 16 texels, 2 anchors)
    //
    // Mode 7 decoding should work like this:
    //   - The 6-bit partition splits the 16 texels into 2 subsets (PartitionTable2), exactly like
    //     Modes 1 and 3, so the endpoint gather is Mode1Gather and the 2-bit index scatter masks
    //     are Mode 3's — both reused directly, Mode 7 adds no tables.
    //   - Each subset has two endpoints per channel; every endpoint is 5 bits plus its own p-bit
    //     (pb0->s0e0, pb1->s0e1, pb2->s1e0, pb3->s1e1), forming a 6-bit value unquantized 6->8 as
    //     (v << 2) | (v >> 4). Unlike Mode 3 (7+1=8) the unquantize is needed here.
    //   - This is the first two-subset mode with a real alpha: alpha is a fourth interpolated
    //     channel rather than a constant 255.
    //   - Each texel carries a 2-bit colour index (anchor texels are 1 bit with an implied high
    //     bit of 0: subset 0's anchor is texel 0, subset 1 uses the fixed anchor table).
    //   - Texel i's colour is, per channel, interpolate(e0, e1, w) = (e0*(64-w) + e1*w + 32) >> 6,
    //     where e0/e1 are the two endpoints of that texel's subset and w is its weight.
    //
    // R and G endpoints lie in lo; B straddles the lo/hi boundary (bits 54..63 in lo, 64..73 in
    // hi); A, the p-bits and the index stream are wholly in hi.
    static unsafe void Mode7Avx(ulong lo, ulong hi, byte* rgba)
    {
        if (IsAvx2Supported)
        {
            const ulong DepMask = 0x1F1F1F1Ful << 1; // 5-bit endpoint at bits 1..5, bit 0 free
            const ulong PbtMask = 0x01010101ul; // 4 unique p-bits, one per endpoint byte

            // Four endpoints per channel into byte lanes 0..3. R and G are in lo; B straddles the
            // lo/hi boundary; A is wholly in hi.
            int partition = (int)((lo >> 8) & 0x3F);
            ulong rB = pdep_u64(lo >> 14, DepMask);
            ulong gB = pdep_u64(lo >> 34, DepMask);
            ulong bB = pdep_u64((lo >> 54) | (hi << 10), DepMask);
            ulong aB = pdep_u64(hi >> 10, DepMask);

            // Four unique p-bits (pb0->s0e0, pb1->s0e1, pb2->s1e0, pb3->s1e1) into bit 0 of each
            // endpoint lane.
            ulong pB = pdep_u64(hi >> 30, PbtMask);
            rB |= pB;
            gB |= pB;
            bB |= pB;
            aB |= pB;

            // 5 bits + p-bit = 6 bits, so unquantize 6->8 as (v << 2) | (v >> 4).
            rB = (rB << 2) | ((rB >> 4) & 0x03030303ul);
            gB = (gB << 2) | ((gB >> 4) & 0x03030303ul);
            bB = (bB << 2) | ((bB >> 4) & 0x03030303ul);
            aB = (aB << 2) | ((aB >> 4) & 0x03030303ul);

            // Index stream is bit-identical to Mode 3's (2-bit, 2 subsets, anchors {0, anchor1}),
            // so its per-partition scatter masks are reused.
            ulong idata = hi >> (98 - 64);
            ulong ilo = pdep_u64(idata, Mode3IndexMaskLo[partition]);
            ulong ihi = pdep_u64(idata >> Mode3IndexLoBits[partition], Mode3IndexMaskHi[partition]);

            v128 w = shuffle_epi8(Weights2Vec, new(ilo, ihi));
            v128 invw = sub_epi8(new((byte)64), w); // 64 - w
            v256 pairs = new(unpacklo_epi8(invw, w), unpackhi_epi8(invw, w));

            // Two-subset endpoint gather, shared with Modes 1 and 3 (PartitionTable2 layout).
            v256 gather = new(
                Mode1Gather[partition * 4 + 0],
                Mode1Gather[partition * 4 + 1],
                Mode1Gather[partition * 4 + 2],
                Mode1Gather[partition * 4 + 3]
            );

            v256 resR = mm256_maddubs_epi16(mm256_shuffle_epi8(new(rB), gather), pairs);
            v256 resG = mm256_maddubs_epi16(mm256_shuffle_epi8(new(gB), gather), pairs);
            v256 resB = mm256_maddubs_epi16(mm256_shuffle_epi8(new(bB), gather), pairs);
            v256 resA = mm256_maddubs_epi16(mm256_shuffle_epi8(new(aB), gather), pairs);

            resR = mm256_srli_epi16(mm256_add_epi16(resR, new((ushort)32)), 6);
            resG = mm256_srli_epi16(mm256_add_epi16(resG, new((ushort)32)), 6);
            resB = mm256_srli_epi16(mm256_add_epi16(resB, new((ushort)32)), 6);
            resA = mm256_srli_epi16(mm256_add_epi16(resA, new((ushort)32)), 6);

            // Transpose the four planar channels to interleaved RGBA (real alpha).
            var rg = mm256_or_si256(resR, mm256_slli_epi16(resG, 8));
            var ba = mm256_or_si256(resB, mm256_slli_epi16(resA, 8));
            var rgbaLo = mm256_unpacklo_epi16(rg, ba); // RGBA, pixels {0..3, 8..11}
            var rgbaHi = mm256_unpackhi_epi16(rg, ba); // RGBA, pixels {4..7, 12..15}

            var output = (v256*)rgba;
            output[0] = mm256_permute2x128_si256(rgbaLo, rgbaHi, 0x20); // pixels 0..7
            output[1] = mm256_permute2x128_si256(rgbaLo, rgbaHi, 0x31); // pixels 8..15
        }
    }
    #endregion

    #endregion
}
