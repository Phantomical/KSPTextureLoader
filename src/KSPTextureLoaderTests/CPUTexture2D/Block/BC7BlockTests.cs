using System;
using KSP.Testing;
using KSPTextureLoader;
using KSPTextureLoader.Utils;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoaderTests;

/// <summary>
/// Validates the new SIMD block decoder <see cref="KSPTextureLoader.CPU.Block.BC7"/>
/// against the existing, Unity-validated <see cref="KSPTextureLoader.CPUTexture2D.BC7"/>
/// decoder (treated as ground truth).
///
/// A BC7 block is 16 bytes = a 128-bit little-endian value split into
/// <c>lo</c> (bytes 0..7) and <c>hi</c> (bytes 8..15) — exactly how
/// <c>CPUTexture2D.BC7</c> reinterprets the raw bytes into its <c>Block { ulong lo, hi }</c>.
/// The helper takes those same two ulongs directly.
///
/// The format encodes LDR RGBA across 8 modes (mode = trailing-zero count of the low
/// byte; a low byte of 0 means mode >= 8 which decodes to transparent black):
///   * Mode 0: 3 subsets, 4-bit RGB, unique p-bit/endpoint, 3-bit indices.
///   * Mode 1: 2 subsets, 6-bit RGB, shared p-bit/subset, 3-bit indices.
///   * Mode 2: 3 subsets, 5-bit RGB, no p-bit, 2-bit indices.
///   * Mode 3: 2 subsets, 7-bit RGB, unique p-bit/endpoint, 2-bit indices.
///   * Mode 4: 1 subset, 5-bit RGB + 6-bit A, rotation + idxMode (2-/3-bit index swap).
///   * Mode 5: 1 subset, 7-bit RGB + 8-bit A, rotation, separate color/alpha indices.
///   * Mode 6: 1 subset, 7-bit RGBA, unique p-bit/endpoint, 4-bit indices.
///   * Mode 7: 2 subsets, 5-bit RGBA, unique p-bit/endpoint, 2-bit indices.
/// Second/third subset anchors carry one fewer index bit; the anchor position depends on
/// the partition, so the whole index bitstream must be consumed in order even when a
/// single pixel is requested.
///
/// For every raw block we build the ground-truth <c>CPUTexture2D.BC7</c> and, on the same
/// bytes (as lo/hi), assert the helper's <c>DecodePixel</c> (per pixel) and
/// <c>DecodeBlock</c> (whole block) agree with it for all 16 pixels. For a single 4x4
/// block the ground-truth decoder maps pixel index i to (x = i % 4, y = i / 4) in
/// row-major order — the order the helper emits.
/// </summary>
public class BC7BlockTests : KSPTextureLoaderTestBase
{
    // Both decoders run bit-identical integer interpolation; DecodePixel returns the exact
    // bytes GetPixel32 returns, so Color32 is compared exactly. DecodeBlock finishes in
    // float (byte * 1/255), matching CPUTexture2D.BC7's ToColor; a hair of tolerance
    // guards float rounding while still catching any whole-integer (>= 1/255) mismatch.
    const int BC7ByteTol = 0;
    const float BC7Tol = 0.001f;

    // ---- Bit writer for constructing BC7 blocks (mirrors CPUTexture2D/BC7Tests.cs) ----

    struct BitWriter
    {
        readonly byte[] data;
        int pos;

        public BitWriter(byte[] data)
        {
            this.data = data;
            pos = 0;
        }

        /// <summary>Write <paramref name="count"/> bits of <paramref name="value"/>, LSB first.</summary>
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

        /// <summary>Write the same value N times, each <paramref name="bits"/> wide.</summary>
        public void WriteN(int value, int bits, int n)
        {
            for (int i = 0; i < n; i++)
                Write(value, bits);
        }
    }

    // ---- Solid / gradient block builders (ported from the Unity-validated BC7 suite) ----

    static byte[] BuildSolidMode0(int r, int g, int b, int pbit)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0b00000001, 1); // mode 0
        w.Write(0, 4); // partition 0
        w.WriteN(r, 4, 6); // R: 6 endpoints
        w.WriteN(g, 4, 6); // G
        w.WriteN(b, 4, 6); // B
        w.WriteN(pbit, 1, 6); // 6 unique p-bits
        return blk;
    }

    static byte[] BuildSolidMode1(int r, int g, int b, int pbit)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0b00000010, 2); // mode 1
        w.Write(0, 6); // partition 0
        w.WriteN(r, 6, 4); // R: 4 endpoints
        w.WriteN(g, 6, 4); // G
        w.WriteN(b, 6, 4); // B
        w.WriteN(pbit, 1, 2); // 2 shared p-bits
        return blk;
    }

    static byte[] BuildSolidMode2(int r, int g, int b)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0b00000100, 3); // mode 2
        w.Write(0, 6); // partition 0
        w.WriteN(r, 5, 6); // R: 6 endpoints
        w.WriteN(g, 5, 6); // G
        w.WriteN(b, 5, 6); // B
        return blk;
    }

    static byte[] BuildSolidMode3(int r, int g, int b, int pbit)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0b00001000, 4); // mode 3
        w.Write(0, 6); // partition 0
        w.WriteN(r, 7, 4); // R: 4 endpoints
        w.WriteN(g, 7, 4); // G
        w.WriteN(b, 7, 4); // B
        w.WriteN(pbit, 1, 4); // 4 unique p-bits
        return blk;
    }

    static byte[] BuildSolidMode4(int r, int g, int b, int a)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0b00010000, 5); // mode 4
        w.Write(0, 2); // rotation 0
        w.Write(0, 1); // idxMode 0
        w.WriteN(r, 5, 2); // R
        w.WriteN(g, 5, 2); // G
        w.WriteN(b, 5, 2); // B
        w.WriteN(a, 6, 2); // A
        return blk;
    }

    static byte[] BuildSolidMode5(int r, int g, int b, int a)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0b00100000, 6); // mode 5
        w.Write(0, 2); // rotation 0
        w.WriteN(r, 7, 2); // R
        w.WriteN(g, 7, 2); // G
        w.WriteN(b, 7, 2); // B
        w.WriteN(a, 8, 2); // A
        return blk;
    }

    static byte[] BuildSolidMode6(int r, int g, int b, int a)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0b01000000, 7); // mode 6
        w.WriteN(r, 7, 2); // R0, R1
        w.WriteN(g, 7, 2); // G0, G1
        w.WriteN(b, 7, 2); // B0, B1
        w.WriteN(a, 7, 2); // A0, A1
        w.WriteN(0, 1, 2); // p-bits = 0
        return blk;
    }

    static byte[] BuildSolidMode7(int r, int g, int b, int a, int pbit)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0b10000000, 8); // mode 7
        w.Write(0, 6); // partition 0
        w.WriteN(r, 5, 4); // R: 4 endpoints
        w.WriteN(g, 5, 4); // G
        w.WriteN(b, 5, 4); // B
        w.WriteN(a, 5, 4); // A
        w.WriteN(pbit, 1, 4); // 4 unique p-bits
        return blk;
    }

    /// <summary>
    /// Mode 6 block with two distinct RGBA endpoints and unique (per-endpoint) p-bits,
    /// plus ascending 4-bit indices — exercises both the interpolation ramp and unique
    /// p-bit application (ep0 uses pb0, ep1 uses pb1).
    /// </summary>
    static byte[] BuildGradientMode6(
        int r0,
        int r1,
        int g0,
        int g1,
        int b0,
        int b1,
        int a0,
        int a1,
        int pb0,
        int pb1
    )
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0b01000000, 7); // mode 6
        w.Write(r0, 7);
        w.Write(r1, 7);
        w.Write(g0, 7);
        w.Write(g1, 7);
        w.Write(b0, 7);
        w.Write(b1, 7);
        w.Write(a0, 7);
        w.Write(a1, 7);
        w.Write(pb0, 1);
        w.Write(pb1, 1);
        w.Write(0, 3); // pixel 0 (anchor): 3-bit index
        for (int i = 1; i < 16; i++)
            w.Write(i, 4); // pixels 1..15: 4-bit indices
        return blk;
    }

    static byte[] BuildMode4Block(int r, int g, int b, int a, int rotation, int idxMode)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0b00010000, 5); // mode 4
        w.Write(rotation, 2);
        w.Write(idxMode, 1);
        w.WriteN(r, 5, 2);
        w.WriteN(g, 5, 2);
        w.WriteN(b, 5, 2);
        w.WriteN(a, 6, 2);
        // idxMode swaps which of the two index sets drives color vs alpha; give them
        // distinct payloads so the swap is observable.
        // 2-bit set: pixel 0 anchor = 1 bit, rest 2 bits.
        w.Write(1, 1);
        for (int i = 1; i < 16; i++)
            w.Write(i & 3, 2);
        // 3-bit set: pixel 0 anchor = 2 bits, rest 3 bits.
        w.Write(2, 2);
        for (int i = 1; i < 16; i++)
            w.Write(i & 7, 3);
        return blk;
    }

    static byte[] BuildMode5Block(int r, int g, int b, int a, int rotation)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0b00100000, 6); // mode 5
        w.Write(rotation, 2);
        w.WriteN(r, 7, 2);
        w.WriteN(g, 7, 2);
        w.WriteN(b, 7, 2);
        w.WriteN(a, 8, 2);
        // Distinct color and alpha 2-bit index sets (pixel 0 anchored to 1 bit each).
        w.Write(1, 1);
        for (int i = 1; i < 16; i++)
            w.Write(i & 3, 2);
        w.Write(0, 1);
        for (int i = 1; i < 16; i++)
            w.Write((i + 1) & 3, 2);
        return blk;
    }

    // ---- Deterministic pseudo-random block builders (no System.Random / DateTime) ----

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
    /// Force the low bits of the block to select BC7 <paramref name="mode"/> (0..7): clear
    /// bits [0, mode) and set the terminator bit at position <paramref name="mode"/>, so the
    /// low byte has exactly <paramref name="mode"/> trailing zeros.
    /// </summary>
    static void ForceMode(byte[] blk, int mode)
    {
        int modeBits = mode + 1;
        blk[0] = (byte)((blk[0] & (0xFF << modeBits)) | (1 << mode));
    }

    /// <summary>
    /// A pseudo-random block whose low bits are forced to a chosen mode, so every mode's
    /// endpoint/index-decode path is exercised with a random (but valid) payload.
    /// </summary>
    static byte[] PseudoRandomModeBlock(int mode, int seed)
    {
        var blk = PseudoRandomBlock(seed);
        ForceMode(blk, mode);
        return blk;
    }

    /// <summary>
    /// A pseudo-random block forced to a chosen mode AND a chosen partition. The random
    /// index/endpoint payload guarantees varied indices, so the anchor-index bit-width
    /// reduction (which shifts every later pixel's bit position) is genuinely exercised —
    /// zero indices would hide such a bug. The partition field sits just above the mode
    /// bits and always lands within the low 14 bits (inside <c>lo</c>).
    /// </summary>
    static byte[] PseudoRandomPartitionBlock(int mode, int partition, int seed)
    {
        var blk = PseudoRandomBlock(seed);
        ForceMode(blk, mode);

        int modeBits = mode + 1;
        int pWidth = mode == 0 ? 4 : 6; // mode 0 has a 4-bit partition; modes 1/2/3/7 use 6
        int pOffset = modeBits;

        ulong lo = BitConverter.ToUInt64(blk, 0);
        ulong pmask = ((1ul << pWidth) - 1) << pOffset;
        lo = (lo & ~pmask) | (((ulong)(uint)partition << pOffset) & pmask);

        var bytes = BitConverter.GetBytes(lo);
        Array.Copy(bytes, 0, blk, 0, 8);
        return blk;
    }

    // ---- Comparison harness ----

    /// <summary>
    /// Build the ground-truth CPUTexture2D.BC7 for one raw block and, on the same bytes
    /// (as lo/hi), assert the helper's DecodePixel and DecodeBlock agree for all 16 pixels.
    /// </summary>
    void CompareBlock(string label, byte[] block)
    {
        if (block.Length != 16)
            throw new Exception($"{label}: block must be 16 bytes, got {block.Length}");

        ulong lo = BitConverter.ToUInt64(block, 0);
        ulong hi = BitConverter.ToUInt64(block, 8);

        var native = new NativeArray<byte>(block, Allocator.Temp);
        try
        {
            var truth = new CPUTexture2D.BC7(native, 4, 4, 1);

            // Whole-block decode via the helper.
            FixedArray16<Color> decoded = KSPTextureLoader.CPU.Block.BC7.DecodeBlock(lo, hi);

            for (int i = 0; i < 16; i++)
            {
                int x = i % 4;
                int y = i / 4;

                Color32 expected32 = truth.GetPixel32(x, y);
                Color expected = truth.GetPixel(x, y);

                // (a) per-pixel helper decode must read the correct pixel's index while
                //     still consuming the whole bitstream in order.
                Color32 pixel = KSPTextureLoader.CPU.Block.BC7.DecodePixel(lo, hi, i);
                assertColor32Equals($"{label}.DecodePixel[{i}]", pixel, expected32, BC7ByteTol);

                // (b) whole-block helper decode.
                assertColorEquals($"{label}.DecodeBlock[{i}]", decoded[i], expected, BC7Tol);
            }
        }
        finally
        {
            native.Dispose();
        }
    }

    // ================================================================
    // 1. Mode detection: one solid block per mode (0-7). The mode is read from the
    //    trailing-zero count of the low byte; each terminator bit width differs.
    // ================================================================

    [TestInfo("CPUTexture2D_BC7Block_ModeDetection")]
    public void TestModeDetection()
    {
        CompareBlock("Mode0", BuildSolidMode0(10, 5, 2, 1));
        CompareBlock("Mode1", BuildSolidMode1(40, 20, 10, 0));
        CompareBlock("Mode2", BuildSolidMode2(20, 10, 5));
        CompareBlock("Mode3", BuildSolidMode3(100, 50, 25, 0));
        CompareBlock("Mode4", BuildSolidMode4(20, 10, 5, 40));
        CompareBlock("Mode5", BuildSolidMode5(100, 50, 25, 200));
        CompareBlock("Mode6", BuildSolidMode6(50, 30, 10, 60));
        CompareBlock("Mode7", BuildSolidMode7(20, 10, 5, 25, 0));
    }

    // ================================================================
    // 2. Invalid mode (low byte == 0 => mode >= 8) decodes to transparent black.
    //    The rest of the block is deliberately non-zero to prove only the low byte matters.
    // ================================================================

    [TestInfo("CPUTexture2D_BC7Block_InvalidMode")]
    public void TestInvalidMode()
    {
        CompareBlock("Invalid_AllZero", new byte[16]);

        var blk = PseudoRandomBlock(1234);
        blk[0] = 0; // no set bit in the low byte -> mode >= 8
        CompareBlock("Invalid_LowByteZero", blk);
    }

    // ================================================================
    // 3. Anchor-index bit-width reduction, 2-subset modes (1, 3, 7). Sweeps partitions
    //    whose second-subset anchor is NOT pixel 15 (e.g. 2, 6, 8), so the reduced-width
    //    anchor sits mid-stream and shifts every following pixel's index bits.
    // ================================================================

    [TestInfo("CPUTexture2D_BC7Block_Anchor2Subset")]
    public void TestAnchor2SubsetReduction()
    {
        int[] modes = { 1, 3, 7 };
        int[] partitions = { 0, 16, 17, 18, 22, 34, 48 };
        foreach (int mode in modes)
        foreach (int part in partitions)
        {
            var blk = PseudoRandomPartitionBlock(mode, part, mode * 1000 + part + 1);
            CompareBlock($"Anchor2_M{mode}_P{part}", blk);
        }
    }

    // ================================================================
    // 4. Anchor-index bit-width reduction, 3-subset modes (0, 2). Two anchors beyond
    //    pixel 0 lose a bit; their positions depend on the partition.
    // ================================================================

    [TestInfo("CPUTexture2D_BC7Block_Anchor3Subset")]
    public void TestAnchor3SubsetReduction()
    {
        int[] modes = { 0, 2 };
        int[] partitions = { 0, 1, 5, 10, 20, 35, 60 };
        foreach (int mode in modes)
        foreach (int part in partitions)
        {
            var blk = PseudoRandomPartitionBlock(mode, part, mode * 1000 + part + 7);
            CompareBlock($"Anchor3_M{mode}_P{part}", blk);
        }
    }

    // ================================================================
    // 5. 2-subset partition table: sweep all 64 partitions for a 2-subset mode (mode 3),
    //    with random payloads, verifying subset assignment for every pixel.
    // ================================================================

    [TestInfo("CPUTexture2D_BC7Block_PartitionTable2")]
    public void TestAllPartitions2Subset()
    {
        for (int part = 0; part < 64; part++)
        {
            var blk = PseudoRandomPartitionBlock(3, part, part + 101);
            CompareBlock($"Part2_P{part}", blk);
        }
    }

    // ================================================================
    // 6. 3-subset partition table: sweep all 64 partitions for a 3-subset mode (mode 2).
    // ================================================================

    [TestInfo("CPUTexture2D_BC7Block_PartitionTable3")]
    public void TestAllPartitions3Subset()
    {
        for (int part = 0; part < 64; part++)
        {
            var blk = PseudoRandomPartitionBlock(2, part, part + 809);
            CompareBlock($"Part3_P{part}", blk);
        }
    }

    // ================================================================
    // 7. P-bit application: shared (mode 1, one p-bit per subset) vs unique (modes 0/3/6/7,
    //    one p-bit per endpoint). Vary the p-bits so their effect on the LSB is observable.
    // ================================================================

    [TestInfo("CPUTexture2D_BC7Block_PBits")]
    public void TestPBitsSharedVsUnique()
    {
        // Mode 1 shared p-bit: 0 vs 1 shifts the reconstructed endpoint LSB.
        CompareBlock("SharedPBit0", BuildSolidMode1(40, 20, 10, 0));
        CompareBlock("SharedPBit1", BuildSolidMode1(40, 20, 10, 1));

        // Unique p-bits, solid blocks with p-bit 0 and 1.
        CompareBlock("Mode0_PBit0", BuildSolidMode0(10, 5, 2, 0));
        CompareBlock("Mode0_PBit1", BuildSolidMode0(10, 5, 2, 1));
        CompareBlock("Mode3_PBit0", BuildSolidMode3(100, 50, 25, 0));
        CompareBlock("Mode3_PBit1", BuildSolidMode3(100, 50, 25, 1));
        CompareBlock("Mode7_PBit0", BuildSolidMode7(20, 10, 5, 25, 0));
        CompareBlock("Mode7_PBit1", BuildSolidMode7(20, 10, 5, 25, 1));

        // Mode 6 unique p-bits differing between the two endpoints (pb0=0, pb1=1) plus an
        // index ramp so the interpolation between the differently-rounded endpoints shows.
        CompareBlock("Mode6_PBit00", BuildGradientMode6(50, 100, 0, 60, 127, 0, 30, 90, 0, 0));
        CompareBlock("Mode6_PBit01", BuildGradientMode6(50, 100, 0, 60, 127, 0, 30, 90, 0, 1));
        CompareBlock("Mode6_PBit10", BuildGradientMode6(50, 100, 0, 60, 127, 0, 30, 90, 1, 0));
        CompareBlock("Mode6_PBit11", BuildGradientMode6(50, 100, 0, 60, 127, 0, 30, 90, 1, 1));
    }

    // ================================================================
    // 8. Channel rotation (modes 4 and 5): rotation 0-3 swaps A with R/G/B respectively.
    // ================================================================

    [TestInfo("CPUTexture2D_BC7Block_Rotation")]
    public void TestRotation()
    {
        for (int rot = 0; rot < 4; rot++)
        {
            CompareBlock($"M4_Rot{rot}", BuildMode4Block(20, 10, 5, 40, rot, 0));
            CompareBlock($"M5_Rot{rot}", BuildMode5Block(100, 50, 25, 200, rot));
        }
    }

    // ================================================================
    // 9. Mode 4 idxMode: swaps the 2-bit and 3-bit index sets between color and alpha.
    //    Built with distinct payloads in each set so the swap changes the output.
    // ================================================================

    [TestInfo("CPUTexture2D_BC7Block_Mode4IdxMode")]
    public void TestMode4IdxMode()
    {
        for (int rot = 0; rot < 4; rot++)
        {
            CompareBlock($"IdxMode0_Rot{rot}", BuildMode4Block(10, 30, 15, 20, rot, 0));
            CompareBlock($"IdxMode1_Rot{rot}", BuildMode4Block(10, 30, 15, 20, rot, 1));
        }
    }

    // ================================================================
    // 10. Deterministic pseudo-random blocks, one forced per mode (0-7) over several
    //     seeds — exercises every endpoint/index path (random partition, rotation,
    //     idxMode, p-bits, indices) with random payloads.
    // ================================================================

    [TestInfo("CPUTexture2D_BC7Block_PseudoRandom_AllModes")]
    public void TestPseudoRandomAllModes()
    {
        for (int mode = 0; mode < 8; mode++)
        for (int v = 0; v < 6; v++)
        {
            int seed = mode * 131 + v * 17 + 3;
            CompareBlock($"Rand_M{mode}_s{v}", PseudoRandomModeBlock(mode, seed));
        }
    }

    // ================================================================
    // 11. Deterministic fully-random blocks (unconstrained low bits, so a mix of valid and
    //     invalid modes) — a broad differential fuzz of the helper.
    // ================================================================

    [TestInfo("CPUTexture2D_BC7Block_PseudoRandom_Full")]
    public void TestPseudoRandomFull()
    {
        for (int seed = 0; seed < 64; seed++)
            CompareBlock($"Full[{seed}]", PseudoRandomBlock(seed * 37 + 5));
    }
}
