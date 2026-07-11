using System;
using KSP.Testing;
using KSPTextureLoader;
using KSPTextureLoader.Utils;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoaderTests;

/// <summary>
/// Validates the new SIMD block decoder <see cref="KSPTextureLoader.CPU.Block.BC6H"/>
/// against the existing, Unity-validated <see cref="KSPTextureLoader.CPUTexture2D.BC6H"/>
/// decoder (treated as ground truth).
///
/// A BC6H block is 16 bytes = a 128-bit little-endian value split into
/// <c>lo</c> (bytes 0..7) and <c>hi</c> (bytes 8..15) — exactly how
/// <c>CPUTexture2D.BC6H</c> reinterprets the raw bytes into its <c>Block { ulong lo, hi }</c>.
/// The helper takes those same two ulongs directly.
///
/// The format encodes HDR RGB (no alpha; alpha is always 1.0) across 14 modes:
///   * Modes 0-8:  2 subsets, 3-bit indices, transformed (delta) endpoints.
///   * Mode 9:     2 subsets, 3-bit indices, NON-transformed endpoints.
///   * Modes 10-13: 1 subset, 4-bit indices (10 direct; 11/12/13 transformed,
///                  12/13 with reversed high bits).
/// Comes in signed (SF16) and unsigned (UF16) variants. Reserved 5-bit codes
/// decode to opaque black.
///
/// For every raw block we build the ground-truth <c>CPUTexture2D.BC6H</c> and, on the
/// same bytes (as lo/hi), assert the helper's <c>DecodePixel</c> (per pixel) and
/// <c>DecodeBlock</c> (whole block) agree with it for all 16 pixels.
///
/// For a single 4x4 block the ground-truth decoder maps pixel index i to
/// (x = i % 4, y = i / 4) in row-major order — the order the helper emits.
/// </summary>
public class BC6HBlockTests : KSPTextureLoaderTestBase
{
    // Both decoders perform bit-identical integer interpolation + half->float
    // finish; this mirrors the tolerance used by the Unity-validated
    // CPUTexture2D.BC6H suite.
    const float BC6HTol = 0.001f;

    // ---- Bit writer for constructing BC6H blocks (mirrors CPUTexture2D/BC6HTests.cs) ----

    struct BitWriter
    {
        readonly byte[] data;
        int pos;

        public BitWriter(byte[] data)
        {
            this.data = data;
            pos = 0;
        }

        public void Write(int value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int byteIdx = pos >> 3;
                int bitIdx = pos & 7;
                data[byteIdx] |= (byte)(((value >> i) & 1) << bitIdx);
                pos++;
            }
        }
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

    // ---- Mode block builders (copied from the Unity-validated BC6H suite) ----

    /// <summary>Mode 10: 1 subset, 10-bit direct endpoints, no transform. Solid block.</summary>
    static byte[] BuildSolidMode10(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x03, 5); // mode 10 = 00011
        w.Write(rv, 10);
        w.Write(gv, 10);
        w.Write(bv, 10);
        w.Write(rv, 10);
        w.Write(gv, 10);
        w.Write(bv, 10);
        return blk;
    }

    /// <summary>Mode 10 with two distinct endpoints and ascending 4-bit indices.</summary>
    static byte[] BuildGradientMode10(int r0, int r1, int g0, int g1, int b0, int b1)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x03, 5); // mode 10
        w.Write(r0, 10);
        w.Write(g0, 10);
        w.Write(b0, 10);
        w.Write(r1, 10);
        w.Write(g1, 10);
        w.Write(b1, 10);
        w.Write(0, 3); // pixel 0 anchor (index bits reduced by one)
        for (int i = 1; i < 16; i++)
            w.Write(i, 4);
        return blk;
    }

    /// <summary>Mode 11: 1 subset, 11-bit base + 9-bit delta, transformed. Solid block.</summary>
    static byte[] BuildSolidMode11(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x07, 5); // mode 11 = 00111
        w.Write(rv & 0x3FF, 10);
        w.Write(gv & 0x3FF, 10);
        w.Write(bv & 0x3FF, 10);
        w.Write(0, 9); // rx delta = 0
        w.Write((rv >> 10) & 1, 1);
        w.Write(0, 9); // gx delta = 0
        w.Write((gv >> 10) & 1, 1);
        w.Write(0, 9); // bx delta = 0
        w.Write((bv >> 10) & 1, 1);
        return blk;
    }

    /// <summary>
    /// Mode 11 with non-zero (signed 9-bit) deltas + max-weight indices, exercising the
    /// transformed endpoint path (base endpoint on the anchor, delta endpoint elsewhere).
    /// </summary>
    static byte[] BuildDeltaMode11(int baseR, int baseG, int baseB, int dR, int dG, int dB)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x07, 5); // mode 11
        w.Write(baseR & 0x3FF, 10);
        w.Write(baseG & 0x3FF, 10);
        w.Write(baseB & 0x3FF, 10);
        w.Write(dR & 0x1FF, 9);
        w.Write((baseR >> 10) & 1, 1);
        w.Write(dG & 0x1FF, 9);
        w.Write((baseG >> 10) & 1, 1);
        w.Write(dB & 0x1FF, 9);
        w.Write((baseB >> 10) & 1, 1);
        w.Write(0, 3); // pixel 0 anchor
        for (int i = 1; i < 16; i++)
            w.Write(15, 4); // max weight -> delta endpoint
        return blk;
    }

    /// <summary>Mode 12: 1 subset, 12-bit base (10 + 2 reversed high bits), 8-bit delta.</summary>
    static byte[] BuildSolidMode12(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x0B, 5); // mode 12 = 01011
        w.Write(rv & 0x3FF, 10);
        w.Write(gv & 0x3FF, 10);
        w.Write(bv & 0x3FF, 10);
        w.Write(0, 8);
        w.Write(ReverseBits((rv >> 10) & 3, 2), 2);
        w.Write(0, 8);
        w.Write(ReverseBits((gv >> 10) & 3, 2), 2);
        w.Write(0, 8);
        w.Write(ReverseBits((bv >> 10) & 3, 2), 2);
        return blk;
    }

    /// <summary>Mode 13: 1 subset, 16-bit base (10 + 6 reversed high bits), 4-bit delta.</summary>
    static byte[] BuildSolidMode13(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x0F, 5); // mode 13 = 01111
        w.Write(rv & 0x3FF, 10);
        w.Write(gv & 0x3FF, 10);
        w.Write(bv & 0x3FF, 10);
        w.Write(0, 4);
        w.Write(ReverseBits((rv >> 10) & 0x3F, 6), 6);
        w.Write(0, 4);
        w.Write(ReverseBits((gv >> 10) & 0x3F, 6), 6);
        w.Write(0, 4);
        w.Write(ReverseBits((bv >> 10) & 0x3F, 6), 6);
        return blk;
    }

    /// <summary>Mode 0: 2 subsets, 10-bit base + 5/5/5 delta. Solid block, partition 0.</summary>
    static byte[] BuildSolidMode0(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x00, 2); // mode 0 = 00
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(rv, 10);
        w.Write(gv, 10);
        w.Write(bv, 10);
        // remaining deltas + partition all zero
        return blk;
    }

    /// <summary>
    /// Mode 0 with a non-zero partition and non-zero deltas — exercises the 2-subset
    /// partition table plus anchor-index reduction on the second subset.
    /// </summary>
    static byte[] BuildPartitionMode0()
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x00, 2); // mode 0
        w.Write(0, 1); // gy[4]
        w.Write(0, 1); // by[4]
        w.Write(0, 1); // bz[4]
        w.Write(400, 10); // rw
        w.Write(200, 10); // gw
        w.Write(100, 10); // bw
        w.Write(5, 5); // rx delta
        w.Write(0, 1); // gz[4]
        w.Write(0, 4); // gy[3:0]
        w.Write(3, 5); // gx delta
        w.Write(0, 1); // bz[0]
        w.Write(0, 4); // gz[3:0]
        w.Write(2, 5); // bx delta
        w.Write(0, 1); // bz[1]
        w.Write(0, 4); // by[3:0]
        w.Write(7, 5); // ry delta (subset 1)
        w.Write(0, 1); // bz[2]
        w.Write(4, 5); // rz delta (subset 1)
        w.Write(0, 1); // bz[3]
        w.Write(13, 5); // partition = 13 (anchor at pixel 8)
        return blk;
    }

    /// <summary>
    /// Mode 9: 2 subsets, 6-bit base, 6/6/6 (NON-transformed) endpoints. Solid block with
    /// all four endpoints equal (values &lt;= 15 so scattered high bits stay zero).
    /// </summary>
    static byte[] BuildSolidMode9(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x1E, 5); // mode 9 = 11110
        w.Write(rv, 6); // rw
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(gv, 6); // gw
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(bv, 6); // bw
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(rv, 6); // rx
        w.Write(gv, 4); // gy[3:0]
        w.Write(gv, 6); // gx
        w.Write(gv, 4); // gz[3:0]
        w.Write(bv, 6); // bx
        w.Write(bv, 4); // by[3:0]
        w.Write(rv, 6); // ry
        w.Write(rv, 6); // rz
        w.Write(0, 5); // partition = 0
        return blk;
    }

    // ---- Deterministic pseudo-random block builders (no System.Random / DateTime) ----

    // 5-bit mode codes indexed by mode number 0..13.
    // csharpier-ignore
    static readonly int[] ModeCodes =
        { 0x00, 0x01, 0x02, 0x06, 0x0A, 0x0E, 0x12, 0x16, 0x1A, 0x1E, 0x03, 0x07, 0x0B, 0x0F };

    /// <summary>
    /// Deterministically derive a byte from a seed + position with an arithmetic hash
    /// (integer mixing only — same inputs always yield the same byte).
    /// </summary>
    static byte PseudoByte(int seed, int i)
    {
        uint s = (uint)(seed * 747796405 + i * 2891336453u + 0x9E3779B9u);
        s ^= s >> 16;
        s *= 0x21F0AAADu;
        s ^= s >> 15;
        s *= 0x735A2D97u;
        s ^= s >> 15;
        return (byte)(s >> 24);
    }

    /// <summary>Sixteen fully deterministic pseudo-random bytes.</summary>
    static byte[] PseudoRandomBlock(int seed)
    {
        var blk = new byte[16];
        for (int i = 0; i < 16; i++)
            blk[i] = PseudoByte(seed, i);
        return blk;
    }

    /// <summary>
    /// A pseudo-random block whose low 5 mode bits are forced to a chosen mode, so every
    /// mode's endpoint-decode path is exercised with a random (but valid) payload.
    /// </summary>
    static byte[] PseudoRandomModeBlock(int mode, int seed)
    {
        var blk = PseudoRandomBlock(seed);
        blk[0] = (byte)((blk[0] & 0xE0) | ModeCodes[mode]);
        return blk;
    }

    // ---- Comparison harness ----

    /// <summary>Compare two HDR channel values, treating NaN==NaN and matching infinities.</summary>
    void AssertHDRFloat(string name, float actual, float expected)
    {
        if (float.IsNaN(expected) || float.IsNaN(actual))
        {
            if (float.IsNaN(expected) && float.IsNaN(actual))
                return;
            throw new Exception($"TEST {name}: FAIL! {actual} != {expected} (NaN mismatch)");
        }
        if (float.IsInfinity(expected) || float.IsInfinity(actual))
        {
            if (expected == actual)
                return;
            throw new Exception($"TEST {name}: FAIL! {actual} != {expected} (Inf mismatch)");
        }
        assertFloatEquals(name, actual, expected, BC6HTol);
    }

    void AssertHDRColor(string name, Color actual, Color expected)
    {
        AssertHDRFloat($"{name}.R", actual.r, expected.r);
        AssertHDRFloat($"{name}.G", actual.g, expected.g);
        AssertHDRFloat($"{name}.B", actual.b, expected.b);
        // Alpha is always 1.0 for BC6H.
        assertFloatEquals($"{name}.A", actual.a, expected.a, 0.0001f);
        assertFloatEquals($"{name}.A1", actual.a, 1f, 0.0001f);
    }

    /// <summary>
    /// Build the ground-truth CPUTexture2D.BC6H for one raw block and, on the same bytes
    /// (as lo/hi), assert the helper's DecodePixel and DecodeBlock agree for all 16 pixels.
    /// </summary>
    void CompareBlock(string label, byte[] block, bool signed)
    {
        if (block.Length != 16)
            throw new Exception($"{label}: block must be 16 bytes, got {block.Length}");

        ulong lo = BitConverter.ToUInt64(block, 0);
        ulong hi = BitConverter.ToUInt64(block, 8);

        var native = new NativeArray<byte>(block, Allocator.Temp);
        try
        {
            var truth = new CPUTexture2D.BC6H(native, 4, 4, 1, signed);

            // Whole-block decode via the helper.
            FixedArray16<Color> decoded = KSPTextureLoader.CPU.Block.BC6H.DecodeBlock(
                lo,
                hi,
                signed
            );

            for (int i = 0; i < 16; i++)
            {
                int x = i % 4;
                int y = i / 4;
                Color expected = truth.GetPixel(x, y);

                // (a) per-pixel helper decode
                Color pixel = KSPTextureLoader.CPU.Block.BC6H.DecodePixel(lo, hi, i, signed);
                AssertHDRColor($"{label}.DecodePixel[{i}]", pixel, expected);

                // (b) whole-block helper decode
                AssertHDRColor($"{label}.DecodeBlock[{i}]", decoded[i], expected);
            }
        }
        finally
        {
            native.Dispose();
        }
    }

    /// <summary>Compare the same block in both unsigned and signed interpretations.</summary>
    void CompareBothSigns(string label, byte[] block)
    {
        CompareBlock($"{label}.unsigned", block, signed: false);
        CompareBlock($"{label}.signed", block, signed: true);
    }

    // ================================================================
    // 1. Mode 10 (1 subset, 10-bit direct): solid, zero, max, asymmetric.
    //    Exercises the simplest path plus Half->float extremes.
    // ================================================================

    [TestInfo("CPUTexture2D_BC6HBlock_Mode10_Direct")]
    public void TestMode10Direct()
    {
        CompareBothSigns("M10_Solid", BuildSolidMode10(512, 256, 100));
        CompareBothSigns("M10_Zero", BuildSolidMode10(0, 0, 0));
        CompareBothSigns("M10_Max", BuildSolidMode10(1023, 1023, 1023));
        CompareBothSigns("M10_Asym", BuildSolidMode10(1023, 0, 512));
    }

    // ================================================================
    // 2. Mode 10 gradient: two endpoints, ascending 4-bit indices with
    //    anchor-index reduction on pixel 0.
    // ================================================================

    [TestInfo("CPUTexture2D_BC6HBlock_Mode10_Gradient")]
    public void TestMode10Gradient()
    {
        CompareBothSigns("M10_Grad", BuildGradientMode10(100, 900, 200, 800, 50, 500));
        CompareBothSigns("M10_GradFull", BuildGradientMode10(0, 1023, 0, 1023, 0, 1023));
    }

    // ================================================================
    // 3. Mode 11 (1 subset transformed): solid, max, and delta-encoded
    //    endpoints (both positive and negative signed 9-bit deltas).
    // ================================================================

    [TestInfo("CPUTexture2D_BC6HBlock_Mode11_Transformed")]
    public void TestMode11Transformed()
    {
        CompareBothSigns("M11_Solid", BuildSolidMode11(1500, 800, 200));
        CompareBothSigns("M11_Max", BuildSolidMode11(2047, 2047, 2047));
        CompareBothSigns("M11_DeltaPos", BuildDeltaMode11(1000, 1000, 1000, 100, 50, 200));
        CompareBothSigns("M11_DeltaNeg", BuildDeltaMode11(1500, 1500, 1500, -200, -50, -100));
    }

    // ================================================================
    // 4. Modes 12 & 13 (reversed high-bit base): verify the reversed 2-/6-bit
    //    high-bit reconstruction on both channels.
    // ================================================================

    [TestInfo("CPUTexture2D_BC6HBlock_ReversedBits")]
    public void TestReversedBitModes()
    {
        CompareBothSigns("M12_Solid", BuildSolidMode12(3000, 2000, 1000));
        CompareBothSigns("M12_HighBits", BuildSolidMode12(3072, 1024, 2048));
        CompareBothSigns("M13_Solid", BuildSolidMode13(30000, 15000, 5000));
        CompareBothSigns("M13_HighBits", BuildSolidMode13(0xABCD, 0x1234, 0xFFFF));
    }

    // ================================================================
    // 5. Mode 0 (2 subsets, transformed): solid partition 0, plus a
    //    non-zero partition with deltas (anchor-index reduction on subset 1).
    // ================================================================

    [TestInfo("CPUTexture2D_BC6HBlock_Mode0_TwoSubset")]
    public void TestMode0TwoSubset()
    {
        CompareBothSigns("M0_Solid", BuildSolidMode0(400, 200, 100));
        CompareBothSigns("M0_Partition", BuildPartitionMode0());
    }

    // ================================================================
    // 6. Mode 9 (2 subsets, NON-transformed): independent 6-bit endpoints.
    // ================================================================

    [TestInfo("CPUTexture2D_BC6HBlock_Mode9_NonTransformed")]
    public void TestMode9NonTransformed()
    {
        CompareBothSigns("M9_Solid", BuildSolidMode9(10, 8, 5));
        CompareBothSigns("M9_Solid2", BuildSolidMode9(15, 0, 12));
    }

    // ================================================================
    // 7. Reserved / invalid 5-bit mode codes decode to opaque black.
    // ================================================================

    [TestInfo("CPUTexture2D_BC6HBlock_Reserved")]
    public void TestReservedModes()
    {
        // 0x13 (10011) and 0x17 (10111) are reserved 5-bit codes.
        var a = new byte[16];
        a[0] = 0x13;
        CompareBothSigns("Reserved_0x13", a);

        var b = new byte[16];
        b[0] = 0x17;
        CompareBothSigns("Reserved_0x17", b);
    }

    // ================================================================
    // 8. Alpha is always 1.0 across a gradient (checked inside AssertHDRColor).
    // ================================================================

    [TestInfo("CPUTexture2D_BC6HBlock_Alpha")]
    public void TestAlphaAlwaysOne()
    {
        // AssertHDRColor already asserts a == 1 for every pixel; a gradient block
        // makes the check span the full weight range.
        CompareBothSigns("Alpha", BuildGradientMode10(0, 1023, 0, 1023, 0, 1023));
    }

    // ================================================================
    // 9. Deterministic pseudo-random blocks, one forced per mode (0-13) over
    //    several seeds — exercises every endpoint-decode path with random
    //    payloads, in both signed and unsigned interpretations.
    // ================================================================

    [TestInfo("CPUTexture2D_BC6HBlock_PseudoRandom_AllModes")]
    public void TestPseudoRandomAllModes()
    {
        for (int mode = 0; mode < 14; mode++)
        {
            for (int v = 0; v < 4; v++)
            {
                int seed = mode * 101 + v * 7 + 1;
                var block = PseudoRandomModeBlock(mode, seed);
                CompareBlock($"Rand_M{mode}_s{v}.unsigned", block, signed: false);
                CompareBlock($"Rand_M{mode}_s{v}.signed", block, signed: true);
            }
        }
    }

    // ================================================================
    // 10. Deterministic fully-random blocks (unconstrained low bits, so a mix of
    //     valid and reserved modes) — a broad differential fuzz of the helper.
    // ================================================================

    [TestInfo("CPUTexture2D_BC6HBlock_PseudoRandom_Full")]
    public void TestPseudoRandomFull()
    {
        for (int seed = 0; seed < 48; seed++)
        {
            var block = PseudoRandomBlock(seed * 31 + 3);
            CompareBlock($"Full[{seed}].unsigned", block, signed: false);
            CompareBlock($"Full[{seed}].signed", block, signed: true);
        }
    }
}
