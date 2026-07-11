using System;
using KSPTextureLoader.Utils;
using Unity.Burst.Intrinsics;
using UnityEngine;
using static Unity.Burst.Intrinsics.X86;

namespace KSPTextureLoader.CPU.Block;

/// <summary>
/// SIMD-accelerated BC6H (HDR RGB) block decoder.
///
/// The block is a 128-bit little-endian value split into <c>lo</c> (bits 0..63)
/// and <c>hi</c> (bits 64..127). The mode, partition and per-subset endpoints are
/// parsed with a small scalar bit reader that is byte-for-byte identical to the
/// reference decoder in <c>CPU/Format/BC6H.cs</c> (the endpoint bit layouts are
/// irregular per-mode and are copied verbatim).
///
/// The two vectorised stages are:
///   * Index extraction — the 16 per-pixel palette indices are pulled out of the
///     block with BMI2 <c>pext</c> (grabs the variable-width contiguous index run,
///     even when it straddles the lo/hi boundary) and scattered into byte lanes
///     with <c>pdep</c> (anchor pixels, which carry one fewer index bit, get a zero
///     MSB for free). This is the shared <see cref="ExtractIndices"/> helper.
///   * Palette build + gather — for each subset the (2^indexBits) interpolated
///     endpoints are computed with AVX2 integer math, finished to HDR floats, and
///     then gathered per pixel with <c>vpermps</c>
///     (<c>mm256_permutevar8x32_ps</c>).
///
/// Every intrinsic path is guarded by its capability check and has a scalar
/// fallback that produces bit-identical results.
/// </summary>
internal static class BC6H
{
    // csharpier-ignore-start

    struct ModeInfo
    {
        public int numSubsets;
        public int endpointBits;
        public int deltaBitsR, deltaBitsG, deltaBitsB;
        public bool transformed;
        public int indexBits;
    }

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

    // Anchor index for the second subset in 2-subset partitions
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

    // ================================================================
    // Public API
    // ================================================================

    internal static unsafe Color DecodePixel(ulong lo, ulong hi, int pixelIndex, bool signed)
    {
        int mode = GetMode(lo);
        if (mode < 0)
            return new Color(0f, 0f, 0f, 1f);

        var info = Modes[mode];
        PrepareEndpoints(lo, hi, mode, signed, out EP ep, out int partition);

        bool two = info.numSubsets == 2;
        int anchor1 = two ? AnchorIndex[partition] : -1;
        int anchorBits = two ? (1 | (1 << anchor1)) : 1;
        int indexStart = 128 - (two ? 46 : 63);

        byte* idx = stackalloc byte[16];
        ExtractIndices(lo, hi, indexStart, info.indexBits, anchorBits, idx);

        int subset = two ? PartitionTable[partition * 16 + pixelIndex] : 0;
        int loR = subset == 0 ? ep.e0r : ep.e2r,
            hiR = subset == 0 ? ep.e1r : ep.e3r;
        int loG = subset == 0 ? ep.e0g : ep.e2g,
            hiG = subset == 0 ? ep.e1g : ep.e3g;
        int loB = subset == 0 ? ep.e0b : ep.e2b,
            hiB = subset == 0 ? ep.e1b : ep.e3b;

        int w = (info.indexBits == 3 ? Weights3 : Weights4)[idx[pixelIndex]];
        int fr = ((64 - w) * loR + w * hiR + 32) >> 6;
        int fg = ((64 - w) * loG + w * hiG + 32) >> 6;
        int fb = ((64 - w) * loB + w * hiB + 32) >> 6;

        return new Color(
            FinishUnquantize(fr, signed),
            FinishUnquantize(fg, signed),
            FinishUnquantize(fb, signed),
            1f
        );
    }

    internal static unsafe FixedArray16<Color> DecodeBlock(ulong lo, ulong hi, bool signed)
    {
        FixedArray16<Color> output = default;

        int mode = GetMode(lo);
        if (mode < 0)
        {
            for (int i = 0; i < 16; i++)
                output[i] = new Color(0f, 0f, 0f, 1f);
            return output;
        }

        var info = Modes[mode];
        PrepareEndpoints(lo, hi, mode, signed, out EP ep, out int partition);

        bool two = info.numSubsets == 2;
        int numWeights = info.indexBits == 3 ? 8 : 16;
        int anchor1 = two ? AnchorIndex[partition] : -1;
        int anchorBits = two ? (1 | (1 << anchor1)) : 1;
        int indexStart = 128 - (two ? 46 : 63);

        // Pull the 16 palette indices into byte lanes (BMI2 pext/pdep or scalar).
        byte* idx = stackalloc byte[16];
        ExtractIndices(lo, hi, indexStart, info.indexBits, anchorBits, idx);

        // Flatten (subset, index) into a single 0..15 palette slot per pixel.
        byte* combined = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            int subset = two ? PartitionTable[partition * 16 + i] : 0;
            combined[i] = (byte)(subset * numWeights + idx[i]);
        }

        // Build a flat 16-entry HDR-float palette (AVX2 interpolation or scalar).
        float* palR = stackalloc float[16];
        float* palG = stackalloc float[16];
        float* palB = stackalloc float[16];
        BuildPalette(in ep, info, numWeights, signed, palR, palG, palB);

        // Gather per-pixel colors from the palette (AVX2 vpermps or scalar).
        float* r = stackalloc float[16];
        float* g = stackalloc float[16];
        float* b = stackalloc float[16];
        GatherColors(palR, palG, palB, combined, r, g, b);

        for (int i = 0; i < 16; i++)
            output[i] = new Color(r[i], g[i], b[i], 1f);

        return output;
    }

    // ================================================================
    // Palette build (AVX2 integer interpolation + scalar finish)
    // ================================================================

    static unsafe void BuildPalette(
        in EP ep,
        ModeInfo info,
        int numWeights,
        bool signed,
        float* palR,
        float* palG,
        float* palB
    )
    {
        for (int s = 0; s < info.numSubsets; s++)
        {
            int loR = s == 0 ? ep.e0r : ep.e2r,
                hiR = s == 0 ? ep.e1r : ep.e3r;
            int loG = s == 0 ? ep.e0g : ep.e2g,
                hiG = s == 0 ? ep.e1g : ep.e3g;
            int loB = s == 0 ? ep.e0b : ep.e2b,
                hiB = s == 0 ? ep.e1b : ep.e3b;
            int baseI = s * numWeights;

            if (Avx2.IsAvx2Supported)
            {
                if (numWeights == 8)
                {
                    // Weights3, lanes 0..7
                    v256 wv = Avx.mm256_set_epi32(64, 55, 46, 37, 27, 18, 9, 0);
                    InterpChunk(wv, loR, hiR, loG, hiG, loB, hiB, signed, palR, palG, palB, baseI);
                }
                else
                {
                    // Weights4, lanes 0..7 then 8..15
                    v256 wlo = Avx.mm256_set_epi32(30, 26, 21, 17, 13, 9, 4, 0);
                    v256 whi = Avx.mm256_set_epi32(64, 60, 55, 51, 47, 43, 38, 34);
                    InterpChunk(wlo, loR, hiR, loG, hiG, loB, hiB, signed, palR, palG, palB, baseI);
                    InterpChunk(
                        whi,
                        loR,
                        hiR,
                        loG,
                        hiG,
                        loB,
                        hiB,
                        signed,
                        palR,
                        palG,
                        palB,
                        baseI + 8
                    );
                }
            }
            else
            {
                var wtbl = numWeights == 8 ? Weights3 : Weights4;
                for (int v = 0; v < numWeights; v++)
                {
                    int w = wtbl[v];
                    palR[baseI + v] = FinishUnquantize(
                        ((64 - w) * loR + w * hiR + 32) >> 6,
                        signed
                    );
                    palG[baseI + v] = FinishUnquantize(
                        ((64 - w) * loG + w * hiG + 32) >> 6,
                        signed
                    );
                    palB[baseI + v] = FinishUnquantize(
                        ((64 - w) * loB + w * hiB + 32) >> 6,
                        signed
                    );
                }
            }
        }
    }

    /// <summary>
    /// Interpolates 8 palette entries for one subset with AVX2 integer math:
    /// <c>((64 - w) * lo + w * hi + 32) &gt;&gt; 6</c> per channel, then finishes
    /// each to an HDR float. Bit-identical to the scalar formula.
    /// </summary>
    static unsafe void InterpChunk(
        v256 wv,
        int loR,
        int hiR,
        int loG,
        int hiG,
        int loB,
        int hiB,
        bool signed,
        float* palR,
        float* palG,
        float* palB,
        int baseI
    )
    {
        // Burst compiles this helper for the SSE2 baseline in addition to AVX2, and it requires
        // the capability guard in the same method as the intrinsics — matching the per-method
        // `if (IsAvx2Supported)` wrapping every other AVX path in this assembly. The only caller
        // (BuildPalette) already gates on AVX2, so the guard is never false at runtime.
        if (Avx2.IsAvx2Supported)
        {
            v256 c32 = Avx.mm256_set1_epi32(32);
            v256 comp = Avx2.mm256_sub_epi32(Avx.mm256_set1_epi32(64), wv);

            v256 rr = Avx2.mm256_srai_epi32(
                Avx2.mm256_add_epi32(
                    Avx2.mm256_add_epi32(
                        Avx2.mm256_mullo_epi32(comp, Avx.mm256_set1_epi32(loR)),
                        Avx2.mm256_mullo_epi32(wv, Avx.mm256_set1_epi32(hiR))
                    ),
                    c32
                ),
                6
            );
            v256 gg = Avx2.mm256_srai_epi32(
                Avx2.mm256_add_epi32(
                    Avx2.mm256_add_epi32(
                        Avx2.mm256_mullo_epi32(comp, Avx.mm256_set1_epi32(loG)),
                        Avx2.mm256_mullo_epi32(wv, Avx.mm256_set1_epi32(hiG))
                    ),
                    c32
                ),
                6
            );
            v256 bb = Avx2.mm256_srai_epi32(
                Avx2.mm256_add_epi32(
                    Avx2.mm256_add_epi32(
                        Avx2.mm256_mullo_epi32(comp, Avx.mm256_set1_epi32(loB)),
                        Avx2.mm256_mullo_epi32(wv, Avx.mm256_set1_epi32(hiB))
                    ),
                    c32
                ),
                6
            );

            int* tr = stackalloc int[8];
            int* tg = stackalloc int[8];
            int* tb = stackalloc int[8];
            Avx.mm256_storeu_ps((float*)tr, rr);
            Avx.mm256_storeu_ps((float*)tg, gg);
            Avx.mm256_storeu_ps((float*)tb, bb);

            for (int k = 0; k < 8; k++)
            {
                palR[baseI + k] = FinishUnquantize(tr[k], signed);
                palG[baseI + k] = FinishUnquantize(tg[k], signed);
                palB[baseI + k] = FinishUnquantize(tb[k], signed);
            }
        }
    }

    // ================================================================
    // Per-pixel palette gather (AVX2 vpermps + scalar fallback)
    // ================================================================

    static unsafe void GatherColors(
        float* palR,
        float* palG,
        float* palB,
        byte* combined,
        float* r,
        float* g,
        float* b
    )
    {
        if (Avx2.IsAvx2Supported)
        {
            v256 pr0 = Avx.mm256_loadu_ps(palR),
                pr1 = Avx.mm256_loadu_ps(palR + 8);
            v256 pg0 = Avx.mm256_loadu_ps(palG),
                pg1 = Avx.mm256_loadu_ps(palG + 8);
            v256 pb0 = Avx.mm256_loadu_ps(palB),
                pb1 = Avx.mm256_loadu_ps(palB + 8);
            v256 seven = Avx.mm256_set1_epi32(7);

            for (int grp = 0; grp < 16; grp += 8)
            {
                v128 bytes = Sse2.cvtsi64x_si128(*(long*)(combined + grp));
                v256 ci = Avx2.mm256_cvtepu8_epi32(bytes);
                v256 low3 = Avx2.mm256_and_si256(ci, seven);
                v256 mask = Avx2.mm256_cmpgt_epi32(ci, seven);

                Avx.mm256_storeu_ps(
                    r + grp,
                    Avx.mm256_blendv_ps(
                        Avx2.mm256_permutevar8x32_ps(pr0, low3),
                        Avx2.mm256_permutevar8x32_ps(pr1, low3),
                        mask
                    )
                );
                Avx.mm256_storeu_ps(
                    g + grp,
                    Avx.mm256_blendv_ps(
                        Avx2.mm256_permutevar8x32_ps(pg0, low3),
                        Avx2.mm256_permutevar8x32_ps(pg1, low3),
                        mask
                    )
                );
                Avx.mm256_storeu_ps(
                    b + grp,
                    Avx.mm256_blendv_ps(
                        Avx2.mm256_permutevar8x32_ps(pb0, low3),
                        Avx2.mm256_permutevar8x32_ps(pb1, low3),
                        mask
                    )
                );
            }
        }
        else
        {
            for (int i = 0; i < 16; i++)
            {
                int c = combined[i];
                r[i] = palR[c];
                g[i] = palG[c];
                b[i] = palB[c];
            }
        }
    }

    // ================================================================
    // Index-run extraction (BMI2 pext/pdep + scalar fallback)
    // ================================================================

    /// <summary>
    /// Reads <paramref name="count"/> bits (LSB-first) starting at absolute bit
    /// <paramref name="start"/> from the 128-bit value (lo | hi&lt;&lt;64). count &lt;= 64.
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
    /// Extracts the 16 palette indices into byte lanes <paramref name="outIdx"/>[0..15].
    /// Each pixel carries <paramref name="width"/> bits, except pixels flagged in
    /// <paramref name="anchorBits"/> which carry <paramref name="width"/>-1 bits (their
    /// missing MSB reads as 0).
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

    // ================================================================
    // Scalar header parse (matches the reference decoder verbatim)
    // ================================================================

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

    // ---- Mode detection ----

    static int GetMode(ulong lo)
    {
        int low5 = (int)(lo & 0x1F);
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

    struct EP
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
        return (float)new Half((ushort)h);
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

    // ================================================================
    // Endpoint decode + transform + unquantize (scalar, verbatim layout)
    // ================================================================

    static void PrepareEndpoints(
        ulong lo,
        ulong hi,
        int mode,
        bool signed,
        out EP ep,
        out int partition
    )
    {
        var info = Modes[mode];

        var reader = new BitReader(lo, hi);
        reader.SkipBits(mode <= 1 ? 2 : 5);

        DecodeEndpoints(ref reader, mode, out ep, out partition);

        // Apply transforms (delta decoding).
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

        // Unquantize all endpoints.
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
    }

    // ---- Endpoint extraction for all 14 modes (bit layouts from bcdec) ----

    static void DecodeEndpoints(ref BitReader r, int mode, out EP ep, out int partition)
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
}
