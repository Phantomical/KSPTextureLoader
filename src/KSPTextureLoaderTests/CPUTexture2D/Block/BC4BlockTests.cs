using System;
using KSP.Testing;
using KSPTextureLoader;
using KSPTextureLoader.Utils;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoaderTests;

/// <summary>
/// Validates the new SIMD block decoder <see cref="KSPTextureLoader.CPU.Block.BC4"/>
/// against the existing, Unity-validated <see cref="KSPTextureLoader.CPUTexture2D.BC4"/>
/// decoder (treated as ground truth).
///
/// A BC4 block is 8 bytes packed little-endian into one ulong:
///   bits[0:8]   = endpoint e0 (byte)
///   bits[8:16]  = endpoint e1 (byte)
///   bits[16:64] = 16 x 3-bit palette indices (pixel i at bit 16 + i*3)
///
///   If e0 &gt; e1:  8-value interpolated palette (weights /7).
///   If e0 &lt;= e1: 6-value interpolated palette + code 6 = 0.0, code 7 = 1.0 (weights /5).
///
/// For every raw block we build the ground-truth <c>CPUTexture2D.BC4</c> and call the
/// helper's <c>DecodeChannel</c> (per pixel) and <c>DecodeBlock</c> (whole block),
/// asserting both match the ground truth for all 16 pixels.
///
/// For a single 4x4 block the ground-truth decoder maps pixel index i to
/// (x = i % 4, y = i / 4) in row-major order, which is exactly the order the
/// helper emits.
/// </summary>
public class BC4BlockTests : KSPTextureLoaderTestBase
{
    // Both decoders perform bit-identical float arithmetic; this mirrors the
    // tolerance used by the Unity-validated CPUTexture2D.BC4 suite.
    const float BC4Tol = 0.005f;

    // ---- Block construction helpers (mirrors CPUTexture2D/BC4Tests.cs) ----

    /// <summary>Build a single BC4 block (8 bytes) from endpoints + 16 3-bit indices.</summary>
    static byte[] BuildBlock(byte r0, byte r1, byte[] indices)
    {
        var block = new byte[8];
        block[0] = r0;
        block[1] = r1;

        // Pack 16 x 3-bit indices into 48 bits (6 bytes), LSB-first.
        ulong bits = 0;
        for (int i = 0; i < 16; i++)
            bits |= (ulong)(indices[i] & 7) << (i * 3);
        for (int i = 0; i < 6; i++)
            block[2 + i] = (byte)(bits >> (i * 8));

        return block;
    }

    /// <summary>
    /// Deterministically derive an 8-byte block from a seed using an arithmetic
    /// PRNG (a plain LCG). No System.Random, no DateTime — same seed → same bytes.
    /// </summary>
    static byte[] PseudoRandomBlock(int seed)
    {
        uint state = (uint)(seed * 747796405 + 2891336453);

        byte Next()
        {
            state = state * 747796405u + 2891336453u;
            // Take a high byte so successive draws differ substantially.
            return (byte)((state >> 24) & 0xFF);
        }

        byte r0 = Next();
        byte r1 = Next();
        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)(Next() & 7);

        return BuildBlock(r0, r1, indices);
    }

    /// <summary>
    /// Construct the ground-truth CPUTexture2D.BC4 for one raw block and, on the same
    /// bytes (as a ulong), assert the helper's DecodeChannel and DecodeBlock agree for
    /// all 16 pixels.
    /// </summary>
    void CompareBlock(string label, byte[] block)
    {
        if (block.Length != 8)
            throw new Exception($"{label}: block must be 8 bytes, got {block.Length}");

        ulong bits = BitConverter.ToUInt64(block, 0);

        var native = new NativeArray<byte>(block, Allocator.Temp);
        try
        {
            var truth = new CPUTexture2D.BC4(
                LargeNativeArray<byte>.FromNativeArray(native),
                4,
                4,
                1
            );

            // Whole-block decode via the helper.
            FixedArray16<float> decoded = KSPTextureLoader.CPU.Block.BC4.DecodeBlock(bits);

            for (int i = 0; i < 16; i++)
            {
                int x = i % 4;
                int y = i / 4;

                float expected = truth.GetPixel(x, y).r;

                // (a) per-pixel helper channel decode
                float channel = KSPTextureLoader.CPU.Block.BC4.DecodeChannel(bits, i);
                assertFloatEquals($"{label}.DecodeChannel[{i}]", channel, expected, BC4Tol);

                // (b) whole-block helper decode
                assertFloatEquals($"{label}.DecodeBlock[{i}]", decoded[i], expected, BC4Tol);
            }
        }
        finally
        {
            native.Dispose();
        }
    }

    // ================================================================
    // 1. Edge case: 8-value path (e0 > e1) — all 8 codes present.
    // ================================================================

    [TestInfo("CPUTexture2D_BC4Block_EightValue")]
    public void TestEightValuePath()
    {
        // e0=240 > e1=30 selects the 8-value interpolated palette.
        var block = BuildBlock(
            240,
            30,
            new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0 }
        );
        CompareBlock("EightValue", block);
    }

    // ================================================================
    // 2. Edge case: 6-value path (e0 <= e1) — code 6 = 0.0, code 7 = 1.0.
    // ================================================================

    [TestInfo("CPUTexture2D_BC4Block_SixValue")]
    public void TestSixValuePath()
    {
        // e0=50 <= e1=200 selects the 6-value palette; codes 6/7 are literal 0/1.
        var block = BuildBlock(
            50,
            200,
            new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0 }
        );
        CompareBlock("SixValue", block);
    }

    // ================================================================
    // 3. Edge case: e0 == e1 degenerate — takes the 6-value path.
    // ================================================================

    [TestInfo("CPUTexture2D_BC4Block_EqualEndpoints")]
    public void TestEqualEndpoints()
    {
        // e0 == e1 → e0 <= e1 → 6-value path. Exercise every code including 6/7.
        var block = BuildBlock(
            128,
            128,
            new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7 }
        );
        CompareBlock("EqualEndpoints", block);
    }

    // ================================================================
    // 4. Edge case: e0 == e1 == 0 degenerate (6-value path).
    // ================================================================

    [TestInfo("CPUTexture2D_BC4Block_ZeroEndpoints")]
    public void TestZeroEndpoints()
    {
        var block = BuildBlock(0, 0, new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7 });
        CompareBlock("ZeroEndpoints", block);
    }

    // ================================================================
    // 5. Edge case: e0 == e1 == 255 degenerate (6-value path).
    // ================================================================

    [TestInfo("CPUTexture2D_BC4Block_MaxEndpoints")]
    public void TestMaxEndpoints()
    {
        var block = BuildBlock(
            255,
            255,
            new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7 }
        );
        CompareBlock("MaxEndpoints", block);
    }

    // ================================================================
    // 6. Edge case: mode boundary e0=1, e1=0 → 8-value path by one.
    // ================================================================

    [TestInfo("CPUTexture2D_BC4Block_ModeBoundary")]
    public void TestModeBoundary()
    {
        var block = BuildBlock(1, 0, new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0 });
        CompareBlock("ModeBoundary", block);
    }

    // ================================================================
    // 7. Edge case: adjacent endpoints e0=100, e1=101 → 6-value path.
    // ================================================================

    [TestInfo("CPUTexture2D_BC4Block_AdjacentEndpoints")]
    public void TestAdjacentEndpoints()
    {
        var block = BuildBlock(
            100,
            101,
            new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0 }
        );
        CompareBlock("AdjacentEndpoints", block);
    }

    // ================================================================
    // 8. Edge case: 3-bit indices straddling byte boundaries.
    //     The 48-bit index field spans 6 bytes; a pixel's 3 bits cross a byte
    //     boundary at pixel indices 2, 5, 7, 10, 13, 15. A forward+reverse run
    //     places distinct codes on both sides of every boundary.
    // ================================================================

    [TestInfo("CPUTexture2D_BC4Block_IndexStraddle")]
    public void TestIndexStraddlesByteBoundary()
    {
        // 8-value endpoints so all 8 codes decode to distinct values, making any
        // mis-extraction across a byte boundary observable.
        var block = BuildBlock(
            230,
            20,
            new byte[] { 7, 6, 5, 4, 3, 2, 1, 0, 1, 3, 5, 7, 2, 4, 6, 0 }
        );
        CompareBlock("IndexStraddle", block);
    }

    // ================================================================
    // 9. Edge case: solid block (all indices code 0).
    // ================================================================

    [TestInfo("CPUTexture2D_BC4Block_Solid")]
    public void TestSolidBlock()
    {
        var block = BuildBlock(180, 180, new byte[16]);
        CompareBlock("Solid", block);
    }

    // ================================================================
    // 10. Deterministic pseudo-random blocks — a spread of mixed paths.
    //      Bytes derive arithmetically from the loop index (no RNG/DateTime).
    // ================================================================

    [TestInfo("CPUTexture2D_BC4Block_PseudoRandom")]
    public void TestPseudoRandomBlocks()
    {
        for (int seed = 0; seed < 64; seed++)
        {
            var block = PseudoRandomBlock(seed);
            CompareBlock($"PseudoRandom[{seed}]", block);
        }
    }

    // ================================================================
    // 11. Deterministic pseudo-random blocks forced onto the 8-value path.
    //      Endpoints are arithmetically constrained so e0 > e1.
    // ================================================================

    [TestInfo("CPUTexture2D_BC4Block_PseudoRandom8Value")]
    public void TestPseudoRandom8ValueBlocks()
    {
        for (int seed = 0; seed < 32; seed++)
        {
            var block = PseudoRandomBlock(seed * 7 + 1);
            // Force e0 > e1: high endpoint at least 1 above the low endpoint.
            byte lo = (byte)(block[1] % 200);
            byte hi = (byte)(lo + 1 + (block[0] % (255 - lo)));
            block[0] = hi;
            block[1] = lo;
            CompareBlock($"PseudoRandom8Value[{seed}]", block);
        }
    }

    // ================================================================
    // 12. Deterministic pseudo-random blocks forced onto the 6-value path.
    //      Endpoints are arithmetically constrained so e0 <= e1.
    // ================================================================

    [TestInfo("CPUTexture2D_BC4Block_PseudoRandom6Value")]
    public void TestPseudoRandom6ValueBlocks()
    {
        for (int seed = 0; seed < 32; seed++)
        {
            var block = PseudoRandomBlock(seed * 13 + 5);
            // Force e0 <= e1.
            byte lo = (byte)(block[0] % 200);
            byte hi = (byte)(lo + (block[1] % (256 - lo)));
            block[0] = lo;
            block[1] = hi;
            CompareBlock($"PseudoRandom6Value[{seed}]", block);
        }
    }
}
