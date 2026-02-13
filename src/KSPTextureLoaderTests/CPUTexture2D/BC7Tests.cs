using System;
using KSP.Testing;
using KSPTextureLoader;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoaderTests;

/// <summary>
/// Comprehensive tests for <see cref="CPUTexture2D.BC7"/>.
///
/// BC7 format: 16-byte blocks covering 4x4 pixels (8 bits/pixel).
/// Supports 8 modes (0-7) with varying numbers of subsets (1-3),
/// endpoint precision (4-8 bits), index precision (2-4 bits), and optional
/// per-endpoint p-bits, channel rotation, and separate color/alpha index sets.
/// Partition tables select which pixels belong to which subset.
/// </summary>
public class BC7Tests : CPUTexture2DTests
{
    const float BC7Tol = 0.005f; // slightly more than 1/255
    const float BC7LossyTol = 0.05f; // for lossy compression comparisons

    // ---- Bit writer for constructing BC7 blocks ----

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

    // ---- Reference helpers ----

    static int Unquantize(int val, int bits)
    {
        if (bits >= 8)
            return val;
        val = val << (8 - bits);
        return val | (val >> bits);
    }

    static int Interpolate(int e0, int e1, int weight)
    {
        return (e0 * (64 - weight) + e1 * weight + 32) >> 6;
    }

    // csharpier-ignore-start
    static readonly int[] RefWeights2 = { 0, 21, 43, 64 };
    static readonly int[] RefWeights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
    static readonly int[] RefWeights4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };
    // csharpier-ignore-end

    // ---- Factory/comparison helpers ----

    static (CPUTexture2D.BC7 tex, NativeArray<byte> data) BC7_Make(
        byte[] blockData,
        int width = 4,
        int height = 4,
        int mipCount = 1
    )
    {
        var native = new NativeArray<byte>(blockData, Allocator.Temp);
        return (new CPUTexture2D.BC7(native, width, height, mipCount), native);
    }

    void BC7_AssertPixel(
        string label,
        CPUTexture2D.BC7 bc7,
        int x,
        int y,
        int er,
        int eg,
        int eb,
        int ea
    )
    {
        Color32 c = bc7.GetPixel32(x, y);
        assertColor32Equals(label, c, new Color32((byte)er, (byte)eg, (byte)eb, (byte)ea), 0);
    }

    void BC7_CompareWithUnity(string label, byte[] blockData, int w = 4, int h = 4)
    {
        var tex = new Texture2D(w, h, TextureFormat.BC7, false);
        tex.LoadRawTextureData(blockData);
        tex.Apply(false, false);

        var (bc7, data) = BC7_Make(blockData, w, h);
        try
        {
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color expected = tex.GetPixel(x, y);
                Color actual = bc7.GetPixel(x, y);
                assertColorEquals($"{label}.Unity({x},{y})", actual, expected, BC7Tol);
            }
        }
        finally
        {
            data.Dispose();
            UnityEngine.Object.Destroy(tex);
        }
    }

    /// <summary>
    /// Build a solid Mode 6 block where all pixels have the same RGBA value.
    /// Endpoints are 7-bit, p-bits are 0, so final 8-bit values = endpoint &lt;&lt; 1.
    /// </summary>
    byte[] BuildSolidMode6(int r, int g, int b, int a)
    {
        var blk = new byte[16];
        var bw = new BitWriter(blk);
        bw.Write(0b01000000, 7); // mode 6
        bw.Write(r, 7);
        bw.Write(r, 7);
        bw.Write(g, 7);
        bw.Write(g, 7);
        bw.Write(b, 7);
        bw.Write(b, 7);
        bw.Write(a, 7);
        bw.Write(a, 7);
        bw.Write(0, 1);
        bw.Write(0, 1);
        return blk;
    }

    /// <summary>
    /// Build a Mode 6 block with two distinct RGBA endpoints and ascending 4-bit
    /// indices (0..15), producing a smooth per-pixel gradient from e0 to e1.
    /// All endpoint values must be in [0, 127]. P-bits are 0.
    /// </summary>
    byte[] BuildGradientMode6(int r0, int r1, int g0, int g1, int b0, int b1, int a0, int a1)
    {
        var blk = new byte[16];
        var bw = new BitWriter(blk);
        bw.Write(0b01000000, 7); // mode 6
        bw.Write(r0, 7);
        bw.Write(r1, 7);
        bw.Write(g0, 7);
        bw.Write(g1, 7);
        bw.Write(b0, 7);
        bw.Write(b1, 7);
        bw.Write(a0, 7);
        bw.Write(a1, 7);
        bw.Write(0, 1);
        bw.Write(0, 1); // p-bits = 0
        bw.Write(0, 3); // pixel 0 (anchor): 3-bit index = 0
        for (int i = 1; i < 16; i++)
            bw.Write(i, 4); // pixels 1-15: 4-bit indices 1..15
        return blk;
    }

    // ================================================================
    // 1. Mode 0 Solid: 3 subsets, 4-bit RGB + unique pbit, 3-bit indices
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Mode0_Solid")]
    public void TestMode0Solid()
    {
        // partition=0, all R=10, G=5, B=2, all pbits=1
        // After pbit: (10<<1)|1=21, (5<<1)|1=11, (2<<1)|1=5 (5-bit values)
        // Unquantize(21,5)=173, Unquantize(11,5)=90, Unquantize(5,5)=41
        var block = new byte[16];
        var w = new BitWriter(block);
        w.Write(0b00000001, 1); // mode 0
        w.Write(0, 4); // partition 0
        w.WriteN(10, 4, 6); // R: 6 endpoints all = 10
        w.WriteN(5, 4, 6); // G: 6 endpoints all = 5
        w.WriteN(2, 4, 6); // B: 6 endpoints all = 2
        w.WriteN(1, 1, 6); // 6 p-bits all = 1

        var (bc7, data) = BC7_Make(block);
        try
        {
            BC7_AssertPixel("Mode0(0,0)", bc7, 0, 0, 173, 90, 41, 255);
            BC7_AssertPixel("Mode0(3,3)", bc7, 3, 3, 173, 90, 41, 255);
            BC7_CompareWithUnity("Mode0", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 2. Mode 1 Solid: 2 subsets, 6-bit RGB + shared pbit, 3-bit indices
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Mode1_Solid")]
    public void TestMode1Solid()
    {
        // partition=0, all R=40, G=20, B=10, both shared pbits=0
        // After pbit: (40<<1)|0=80, (20<<1)|0=40, (10<<1)|0=20 (7-bit)
        // Unquantize(80,7)=161, Unquantize(40,7)=80, Unquantize(20,7)=40
        var block = new byte[16];
        var w = new BitWriter(block);
        w.Write(0b00000010, 2); // mode 1
        w.Write(0, 6); // partition 0
        w.WriteN(40, 6, 4); // R
        w.WriteN(20, 6, 4); // G
        w.WriteN(10, 6, 4); // B
        w.WriteN(0, 1, 2); // shared pbits = 0

        var (bc7, data) = BC7_Make(block);
        try
        {
            BC7_AssertPixel("Mode1(0,0)", bc7, 0, 0, 161, 80, 40, 255);
            BC7_AssertPixel("Mode1(2,1)", bc7, 2, 1, 161, 80, 40, 255);
            BC7_CompareWithUnity("Mode1", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 3. Mode 2 Solid: 3 subsets, 5-bit RGB, no pbit, 2-bit indices
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Mode2_Solid")]
    public void TestMode2Solid()
    {
        // partition=0, all R=20, G=10, B=5
        // Unquantize(20,5)=165, Unquantize(10,5)=82, Unquantize(5,5)=41
        var block = new byte[16];
        var w = new BitWriter(block);
        w.Write(0b00000100, 3); // mode 2
        w.Write(0, 6); // partition 0
        w.WriteN(20, 5, 6); // R
        w.WriteN(10, 5, 6); // G
        w.WriteN(5, 5, 6); // B

        var (bc7, data) = BC7_Make(block);
        try
        {
            BC7_AssertPixel("Mode2(0,0)", bc7, 0, 0, 165, 82, 41, 255);
            BC7_AssertPixel("Mode2(1,2)", bc7, 1, 2, 165, 82, 41, 255);
            BC7_CompareWithUnity("Mode2", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 4. Mode 3 Solid: 2 subsets, 7-bit RGB + unique pbit, 2-bit indices
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Mode3_Solid")]
    public void TestMode3Solid()
    {
        // partition=0, all R=100, G=50, B=25, all pbits=0
        // After pbit: (100<<1)|0=200, (50<<1)|0=100, (25<<1)|0=50 (8-bit)
        // Unquantize(200,8)=200, Unquantize(100,8)=100, Unquantize(50,8)=50
        var block = new byte[16];
        var w = new BitWriter(block);
        w.Write(0b00001000, 4); // mode 3
        w.Write(0, 6); // partition 0
        w.WriteN(100, 7, 4); // R
        w.WriteN(50, 7, 4); // G
        w.WriteN(25, 7, 4); // B
        w.WriteN(0, 1, 4); // 4 pbits = 0

        var (bc7, data) = BC7_Make(block);
        try
        {
            BC7_AssertPixel("Mode3(0,0)", bc7, 0, 0, 200, 100, 50, 255);
            BC7_AssertPixel("Mode3(3,1)", bc7, 3, 1, 200, 100, 50, 255);
            BC7_CompareWithUnity("Mode3", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 5. Mode 4 Solid: 1 subset, 5-bit RGB + 6-bit A, rotation=0, idxMode=0
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Mode4_Solid")]
    public void TestMode4Solid()
    {
        // R0=R1=20, G0=G1=10, B0=B1=5, A0=A1=40
        // Unquantize(20,5)=165, Unquantize(10,5)=82, Unquantize(5,5)=41, Unquantize(40,6)=162
        var block = new byte[16];
        var w = new BitWriter(block);
        w.Write(0b00010000, 5); // mode 4
        w.Write(0, 2); // rotation 0
        w.Write(0, 1); // idxMode 0
        w.WriteN(20, 5, 2); // R0, R1
        w.WriteN(10, 5, 2); // G0, G1
        w.WriteN(5, 5, 2); // B0, B1
        w.WriteN(40, 6, 2); // A0, A1

        var (bc7, data) = BC7_Make(block);
        try
        {
            BC7_AssertPixel("Mode4(0,0)", bc7, 0, 0, 165, 82, 41, 162);
            BC7_AssertPixel("Mode4(2,3)", bc7, 2, 3, 165, 82, 41, 162);
            BC7_CompareWithUnity("Mode4", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 6. Mode 5 Solid: 1 subset, 7-bit RGB + 8-bit A, rotation=0
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Mode5_Solid")]
    public void TestMode5Solid()
    {
        // R0=R1=100, G0=G1=50, B0=B1=25, A0=A1=200
        // Unquantize(100,7)=201, Unquantize(50,7)=100, Unquantize(25,7)=50, Unquantize(200,8)=200
        var block = new byte[16];
        var w = new BitWriter(block);
        w.Write(0b00100000, 6); // mode 5
        w.Write(0, 2); // rotation 0
        w.WriteN(100, 7, 2); // R0, R1
        w.WriteN(50, 7, 2); // G0, G1
        w.WriteN(25, 7, 2); // B0, B1
        w.WriteN(200, 8, 2); // A0, A1

        var (bc7, data) = BC7_Make(block);
        try
        {
            BC7_AssertPixel("Mode5(0,0)", bc7, 0, 0, 201, 100, 50, 200);
            BC7_AssertPixel("Mode5(1,1)", bc7, 1, 1, 201, 100, 50, 200);
            BC7_CompareWithUnity("Mode5", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 7. Mode 6 Solid: 1 subset, 7-bit RGBA + unique pbit, 4-bit indices
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Mode6_Solid")]
    public void TestMode6Solid()
    {
        // R0=R1=50, G0=G1=30, B0=B1=10, A0=A1=60, pbit0=pbit1=0
        // After pbit: R=100, G=60, B=20, A=120 (8-bit, no unquantize needed)
        var block = new byte[16];
        var w = new BitWriter(block);
        w.Write(0b01000000, 7); // mode 6
        w.WriteN(50, 7, 2); // R0, R1
        w.WriteN(30, 7, 2); // G0, G1
        w.WriteN(10, 7, 2); // B0, B1
        w.WriteN(60, 7, 2); // A0, A1
        w.WriteN(0, 1, 2); // pbits = 0

        var (bc7, data) = BC7_Make(block);
        try
        {
            BC7_AssertPixel("Mode6(0,0)", bc7, 0, 0, 100, 60, 20, 120);
            BC7_AssertPixel("Mode6(3,3)", bc7, 3, 3, 100, 60, 20, 120);
            BC7_CompareWithUnity("Mode6", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 8. Mode 7 Solid: 2 subsets, 5-bit RGBA + unique pbit, 2-bit indices
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Mode7_Solid")]
    public void TestMode7Solid()
    {
        // partition=0, all R=20, G=10, B=5, A=25, all pbits=0
        // After pbit: R=40, G=20, B=10, A=50 (6-bit)
        // Unquantize(40,6)=162, Unquantize(20,6)=81, Unquantize(10,6)=40, Unquantize(50,6)=203
        var block = new byte[16];
        var w = new BitWriter(block);
        w.Write(0b10000000, 8); // mode 7
        w.Write(0, 6); // partition 0
        w.WriteN(20, 5, 4); // R
        w.WriteN(10, 5, 4); // G
        w.WriteN(5, 5, 4); // B
        w.WriteN(25, 5, 4); // A
        w.WriteN(0, 1, 4); // 4 pbits = 0

        var (bc7, data) = BC7_Make(block);
        try
        {
            BC7_AssertPixel("Mode7(0,0)", bc7, 0, 0, 162, 81, 40, 203);
            BC7_AssertPixel("Mode7(2,2)", bc7, 2, 2, 162, 81, 40, 203);
            BC7_CompareWithUnity("Mode7", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 9. Reserved mode (all zeros)
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Reserved")]
    public void TestReservedMode()
    {
        var block = new byte[16]; // all zeros -> mode >= 8
        var (bc7, data) = BC7_Make(block);
        try
        {
            BC7_AssertPixel("Reserved(0,0)", bc7, 0, 0, 0, 0, 0, 0);
            BC7_CompareWithUnity("Reserved", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 10. Mode 6 interpolation: different endpoints, non-zero index
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Mode6_Interp")]
    public void TestMode6Interpolation()
    {
        int r0v = 50,
            r1v = 100;
        int g0v = 0,
            g1v = 60;
        int b0v = 127,
            b1v = 0;
        int a0v = 30,
            a1v = 90;

        // After pbit=0: endpoints are doubled
        int re0 = (r0v << 1) | 0,
            re1 = (r1v << 1) | 0;
        int ge0 = (g0v << 1) | 0,
            ge1 = (g1v << 1) | 0;
        int be0 = (b0v << 1) | 0,
            be1 = (b1v << 1) | 0;
        int ae0 = (a0v << 1) | 0,
            ae1 = (a1v << 1) | 0;

        // Pixel 0 (anchor): 3-bit index, use value 4
        int w4 = RefWeights4[4]; // 17
        int expR = Interpolate(re0, re1, w4);
        int expG = Interpolate(ge0, ge1, w4);
        int expB = Interpolate(be0, be1, w4);
        int expA = Interpolate(ae0, ae1, w4);

        var block = new byte[16];
        var bw = new BitWriter(block);
        bw.Write(0b01000000, 7); // mode 6
        bw.Write(r0v, 7);
        bw.Write(r1v, 7);
        bw.Write(g0v, 7);
        bw.Write(g1v, 7);
        bw.Write(b0v, 7);
        bw.Write(b1v, 7);
        bw.Write(a0v, 7);
        bw.Write(a1v, 7);
        bw.Write(0, 1); // pbit0
        bw.Write(0, 1); // pbit1
        bw.Write(4, 3); // pixel 0 anchor index = 4
        // pixels 1-15 = 0

        var (bc7, data) = BC7_Make(block);
        try
        {
            BC7_AssertPixel("Mode6Interp(0,0)", bc7, 0, 0, expR, expG, expB, expA);
            // Pixel 1 has index 0, weight=0 -> ep0 exactly
            BC7_AssertPixel("Mode6Interp(1,0)", bc7, 1, 0, re0, ge0, be0, ae0);
            BC7_CompareWithUnity("Mode6Interp", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 11. Mode 6 non-anchor index: pixel 1 uses full 4-bit index (15)
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Mode6_NonAnchor")]
    public void TestMode6NonAnchorIndex()
    {
        int r0v = 50,
            r1v = 100;

        var block = new byte[16];
        var w = new BitWriter(block);
        w.Write(0b01000000, 7); // mode 6
        w.Write(r0v, 7);
        w.Write(r1v, 7);
        w.WriteN(0, 7, 2); // G0, G1 = 0
        w.WriteN(0, 7, 2); // B0, B1 = 0
        w.WriteN(127, 7, 2); // A0, A1 = 127
        w.Write(0, 1); // pbit0
        w.Write(0, 1); // pbit1
        w.Write(0, 3); // pixel 0 anchor: 3 bits = 0
        w.Write(15, 4); // pixel 1: 4 bits = 15 (max)

        int re0 = (r0v << 1) | 0; // 100
        int re1 = (r1v << 1) | 0; // 200
        int ae = (127 << 1) | 0; // 254

        var (bc7, data) = BC7_Make(block);
        try
        {
            BC7_AssertPixel("NonAnchor_p0", bc7, 0, 0, re0, 0, 0, ae);
            int expR1 = Interpolate(re0, re1, RefWeights4[15]); // weight=64 -> re1=200
            BC7_AssertPixel("NonAnchor_p1", bc7, 1, 0, expR1, 0, 0, ae);
            BC7_CompareWithUnity("NonAnchor", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 12. Mode 1 partition test: different colors per subset
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Mode1_Partition")]
    public void TestMode1Partition()
    {
        // Partition 13: pixels 0-7 = subset 0, pixels 8-15 = subset 1
        var block = new byte[16];
        var w = new BitWriter(block);
        w.Write(0b00000010, 2); // mode 1
        w.Write(13, 6); // partition 13

        // R endpoints: s0e0, s0e1, s1e0, s1e1
        w.Write(40, 6);
        w.Write(40, 6);
        w.Write(60, 6);
        w.Write(60, 6);
        // G
        w.Write(20, 6);
        w.Write(20, 6);
        w.Write(30, 6);
        w.Write(30, 6);
        // B
        w.Write(10, 6);
        w.Write(10, 6);
        w.Write(15, 6);
        w.Write(15, 6);
        // Shared pbits
        w.Write(0, 1);
        w.Write(0, 1);

        // Subset 0: after pbit 0: (40<<1)|0=80 -> Unquantize(80,7)=161
        // Subset 1: after pbit 0: (60<<1)|0=120 -> Unquantize(120,7)=241
        var (bc7, data) = BC7_Make(block);
        try
        {
            BC7_AssertPixel("Part13_sub0", bc7, 0, 0, 161, 80, 40, 255);
            BC7_AssertPixel("Part13_sub1", bc7, 0, 2, 241, 120, 60, 255);
            BC7_CompareWithUnity("Mode1Part", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 13. Mode 0 partition test: 3 subsets with different colors
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Mode0_Partition")]
    public void TestMode0Partition()
    {
        // Partition 0, 3-subset: 0,0,1,1,0,0,1,1,0,2,2,1,2,2,2,2
        // Pixel 0 -> subset 0, Pixel 2 -> subset 1, Pixel 9 -> subset 2
        int rS0 = 15,
            gS0 = 8,
            bS0 = 4;
        int rS1 = 10,
            gS1 = 5,
            bS1 = 2;
        int rS2 = 5,
            gS2 = 3,
            bS2 = 1;
        int pbit = 0;

        var block = new byte[16];
        var bw = new BitWriter(block);
        bw.Write(0b00000001, 1); // mode 0
        bw.Write(0, 4); // partition 0

        // R: s0e0, s0e1, s1e0, s1e1, s2e0, s2e1
        bw.Write(rS0, 4);
        bw.Write(rS0, 4);
        bw.Write(rS1, 4);
        bw.Write(rS1, 4);
        bw.Write(rS2, 4);
        bw.Write(rS2, 4);
        // G
        bw.Write(gS0, 4);
        bw.Write(gS0, 4);
        bw.Write(gS1, 4);
        bw.Write(gS1, 4);
        bw.Write(gS2, 4);
        bw.Write(gS2, 4);
        // B
        bw.Write(bS0, 4);
        bw.Write(bS0, 4);
        bw.Write(bS1, 4);
        bw.Write(bS1, 4);
        bw.Write(bS2, 4);
        bw.Write(bS2, 4);
        // pbits all 0
        bw.WriteN(0, 1, 6);

        int exR0 = Unquantize((rS0 << 1) | pbit, 5);
        int exG0 = Unquantize((gS0 << 1) | pbit, 5);
        int exB0 = Unquantize((bS0 << 1) | pbit, 5);
        int exR1 = Unquantize((rS1 << 1) | pbit, 5);
        int exG1 = Unquantize((gS1 << 1) | pbit, 5);
        int exB1 = Unquantize((bS1 << 1) | pbit, 5);
        int exR2 = Unquantize((rS2 << 1) | pbit, 5);
        int exG2 = Unquantize((gS2 << 1) | pbit, 5);
        int exB2 = Unquantize((bS2 << 1) | pbit, 5);

        var (bc7, data) = BC7_Make(block);
        try
        {
            BC7_AssertPixel("Part0_sub0", bc7, 0, 0, exR0, exG0, exB0, 255);
            BC7_AssertPixel("Part0_sub1", bc7, 2, 0, exR1, exG1, exB1, 255);
            BC7_AssertPixel("Part0_sub2", bc7, 1, 2, exR2, exG2, exB2, 255);
            BC7_CompareWithUnity("Mode0Part", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 14. Mode 5 rotation tests
    // ================================================================

    byte[] BuildMode5Block(int r, int g, int b, int a, int rotation)
    {
        var block = new byte[16];
        var w = new BitWriter(block);
        w.Write(0b00100000, 6); // mode 5
        w.Write(rotation, 2);
        w.WriteN(r, 7, 2);
        w.WriteN(g, 7, 2);
        w.WriteN(b, 7, 2);
        w.WriteN(a, 8, 2);
        return block;
    }

    [TestInfo("CPUTexture2D_BC7_Mode5_Rotation")]
    public void TestMode5Rotation()
    {
        int baseR = Unquantize(100, 7); // 201
        int baseG = Unquantize(50, 7); // 100
        int baseB = Unquantize(25, 7); // 50
        int baseA = 200; // Unquantize(200,8) = 200

        var blk0 = BuildMode5Block(100, 50, 25, 200, 0);
        var blk1 = BuildMode5Block(100, 50, 25, 200, 1);
        var blk2 = BuildMode5Block(100, 50, 25, 200, 2);
        var blk3 = BuildMode5Block(100, 50, 25, 200, 3);

        var (bc7_0, d0) = BC7_Make(blk0);
        var (bc7_1, d1) = BC7_Make(blk1);
        var (bc7_2, d2) = BC7_Make(blk2);
        var (bc7_3, d3) = BC7_Make(blk3);

        try
        {
            // rotation=0: no swap
            BC7_AssertPixel("Rot0", bc7_0, 0, 0, baseR, baseG, baseB, baseA);
            // rotation=1: swap R <-> A
            BC7_AssertPixel("Rot1", bc7_1, 0, 0, baseA, baseG, baseB, baseR);
            // rotation=2: swap G <-> A
            BC7_AssertPixel("Rot2", bc7_2, 0, 0, baseR, baseA, baseB, baseG);
            // rotation=3: swap B <-> A
            BC7_AssertPixel("Rot3", bc7_3, 0, 0, baseR, baseG, baseA, baseB);

            BC7_CompareWithUnity("M5Rot0", blk0);
            BC7_CompareWithUnity("M5Rot1", blk1);
            BC7_CompareWithUnity("M5Rot2", blk2);
            BC7_CompareWithUnity("M5Rot3", blk3);
        }
        finally
        {
            d0.Dispose();
            d1.Dispose();
            d2.Dispose();
            d3.Dispose();
        }
    }

    // ================================================================
    // 15. Mode 4 rotation tests
    // ================================================================

    byte[] BuildMode4Block(int r, int g, int b, int a, int rotation, int idxMode)
    {
        var block = new byte[16];
        var w = new BitWriter(block);
        w.Write(0b00010000, 5); // mode 4
        w.Write(rotation, 2);
        w.Write(idxMode, 1);
        w.WriteN(r, 5, 2);
        w.WriteN(g, 5, 2);
        w.WriteN(b, 5, 2);
        w.WriteN(a, 6, 2);
        return block;
    }

    [TestInfo("CPUTexture2D_BC7_Mode4_Rotation")]
    public void TestMode4Rotation()
    {
        int baseR = Unquantize(20, 5); // 165
        int baseG = Unquantize(10, 5); // 82
        int baseB = Unquantize(5, 5); // 41
        int baseA = Unquantize(40, 6); // 162

        var blk0 = BuildMode4Block(20, 10, 5, 40, 0, 0);
        var blk1 = BuildMode4Block(20, 10, 5, 40, 1, 0);
        var blk2 = BuildMode4Block(20, 10, 5, 40, 2, 0);
        var blk3 = BuildMode4Block(20, 10, 5, 40, 3, 0);

        var (bc7_0, d0) = BC7_Make(blk0);
        var (bc7_1, d1) = BC7_Make(blk1);
        var (bc7_2, d2) = BC7_Make(blk2);
        var (bc7_3, d3) = BC7_Make(blk3);

        try
        {
            BC7_AssertPixel("M4Rot0", bc7_0, 0, 0, baseR, baseG, baseB, baseA);
            BC7_AssertPixel("M4Rot1", bc7_1, 0, 0, baseA, baseG, baseB, baseR);
            BC7_AssertPixel("M4Rot2", bc7_2, 0, 0, baseR, baseA, baseB, baseG);
            BC7_AssertPixel("M4Rot3", bc7_3, 0, 0, baseR, baseG, baseA, baseB);

            BC7_CompareWithUnity("M4Rot0", blk0);
            BC7_CompareWithUnity("M4Rot1", blk1);
            BC7_CompareWithUnity("M4Rot2", blk2);
            BC7_CompareWithUnity("M4Rot3", blk3);
        }
        finally
        {
            d0.Dispose();
            d1.Dispose();
            d2.Dispose();
            d3.Dispose();
        }
    }

    // ================================================================
    // 16. Mode 4 index mode test
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Mode4_IdxMode")]
    public void TestMode4IdxMode()
    {
        int rVal0 = 10,
            rVal1 = 30;
        int gVal = 15;
        int bVal = 5;
        int aVal0 = 20,
            aVal1 = 50;

        int rU0 = Unquantize(rVal0, 5);
        int rU1 = Unquantize(rVal1, 5);
        int gU = Unquantize(gVal, 5);
        int bU = Unquantize(bVal, 5);
        int aU0 = Unquantize(aVal0, 6);
        int aU1 = Unquantize(aVal1, 6);

        // idxMode=0: color uses 2-bit weights, alpha uses 3-bit weights
        {
            var block = new byte[16];
            var w = new BitWriter(block);
            w.Write(0b00010000, 5); // mode 4
            w.Write(0, 2); // rotation 0
            w.Write(0, 1); // idxMode 0
            w.Write(rVal0, 5);
            w.Write(rVal1, 5);
            w.Write(gVal, 5);
            w.Write(gVal, 5);
            w.Write(bVal, 5);
            w.Write(bVal, 5);
            w.Write(aVal0, 6);
            w.Write(aVal1, 6);
            // 2-bit indices: pixel 0 anchor = 1 bit, write value 1
            w.Write(1, 1);
            w.WriteN(0, 2, 15);
            // 3-bit indices: pixel 0 anchor = 2 bits, write value 2
            w.Write(2, 2);

            int colorW = RefWeights2[1]; // 21
            int alphaW = RefWeights3[2]; // 18
            int expR = Interpolate(rU0, rU1, colorW);
            int expG = Interpolate(gU, gU, colorW);
            int expB = Interpolate(bU, bU, colorW);
            int expA = Interpolate(aU0, aU1, alphaW);

            var (bc7, data) = BC7_Make(block);
            try
            {
                BC7_AssertPixel("IdxMode0", bc7, 0, 0, expR, expG, expB, expA);
                BC7_CompareWithUnity("IdxMode0", block);
            }
            finally
            {
                data.Dispose();
            }
        }

        // idxMode=1: color uses 3-bit weights, alpha uses 2-bit weights
        {
            var block = new byte[16];
            var w = new BitWriter(block);
            w.Write(0b00010000, 5); // mode 4
            w.Write(0, 2); // rotation 0
            w.Write(1, 1); // idxMode 1
            w.Write(rVal0, 5);
            w.Write(rVal1, 5);
            w.Write(gVal, 5);
            w.Write(gVal, 5);
            w.Write(bVal, 5);
            w.Write(bVal, 5);
            w.Write(aVal0, 6);
            w.Write(aVal1, 6);
            w.Write(1, 1);
            w.WriteN(0, 2, 15);
            w.Write(2, 2);

            // idxMode=1 swaps: colorIdx=idx3=2, alphaIdx=idx2=1
            int colorW = RefWeights3[2]; // 18
            int alphaW = RefWeights2[1]; // 21
            int expR = Interpolate(rU0, rU1, colorW);
            int expG = Interpolate(gU, gU, colorW);
            int expB = Interpolate(bU, bU, colorW);
            int expA = Interpolate(aU0, aU1, alphaW);

            var (bc7, data) = BC7_Make(block);
            try
            {
                BC7_AssertPixel("IdxMode1", bc7, 0, 0, expR, expG, expB, expA);
                BC7_CompareWithUnity("IdxMode1", block);
            }
            finally
            {
                data.Dispose();
            }
        }
    }

    // ================================================================
    // 17. Mode 6 p-bits test
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Mode6_PBits")]
    public void TestMode6PBits()
    {
        // pbit0=0, pbit1=0: ep0=100, ep1=100
        {
            var block = new byte[16];
            var w = new BitWriter(block);
            w.Write(0b01000000, 7);
            w.WriteN(50, 7, 2);
            w.WriteN(50, 7, 2);
            w.WriteN(50, 7, 2);
            w.WriteN(50, 7, 2);
            w.Write(0, 1);
            w.Write(0, 1);

            var (bc7, data) = BC7_Make(block);
            try
            {
                BC7_AssertPixel("PBit00", bc7, 0, 0, 100, 100, 100, 100);
                BC7_CompareWithUnity("PBit00", block);
            }
            finally
            {
                data.Dispose();
            }
        }

        // pbit0=1, pbit1=1: ep0=101, ep1=101
        {
            var block = new byte[16];
            var w = new BitWriter(block);
            w.Write(0b01000000, 7);
            w.WriteN(50, 7, 2);
            w.WriteN(50, 7, 2);
            w.WriteN(50, 7, 2);
            w.WriteN(50, 7, 2);
            w.Write(1, 1);
            w.Write(1, 1);

            var (bc7, data) = BC7_Make(block);
            try
            {
                BC7_AssertPixel("PBit11", bc7, 0, 0, 101, 101, 101, 101);
                BC7_CompareWithUnity("PBit11", block);
            }
            finally
            {
                data.Dispose();
            }
        }

        // pbit0=0, pbit1=1: ep0=100, ep1=101
        // With anchor index=7 -> weight=Weights4[7]=30
        {
            var block = new byte[16];
            var w = new BitWriter(block);
            w.Write(0b01000000, 7);
            w.WriteN(50, 7, 2);
            w.WriteN(50, 7, 2);
            w.WriteN(50, 7, 2);
            w.WriteN(50, 7, 2);
            w.Write(0, 1);
            w.Write(1, 1);
            w.Write(7, 3); // pixel 0 anchor index = 7

            int exp = Interpolate(100, 101, RefWeights4[7]);
            var (bc7, data) = BC7_Make(block);
            try
            {
                BC7_AssertPixel("PBit01_interp", bc7, 0, 0, exp, exp, exp, exp);
                BC7_CompareWithUnity("PBit01", block);
            }
            finally
            {
                data.Dispose();
            }
        }
    }

    // ================================================================
    // 18. Mode 1 shared p-bit test
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Mode1_SharedPBit")]
    public void TestMode1SharedPBit()
    {
        // Partition 0, all same endpoints R=40, G=20, B=10
        // pbit0=1 (subset 0): (40<<1)|1=81, Unquantize(81,7)=163
        var block = new byte[16];
        var w = new BitWriter(block);
        w.Write(0b00000010, 2); // mode 1
        w.Write(0, 6); // partition 0
        w.WriteN(40, 6, 4); // R
        w.WriteN(20, 6, 4); // G
        w.WriteN(10, 6, 4); // B
        w.Write(1, 1); // pbit0 = 1 (shared for subset 0)
        w.Write(0, 1); // pbit1 = 0 (shared for subset 1)

        int rExp = Unquantize((40 << 1) | 1, 7);
        int gExp = Unquantize((20 << 1) | 1, 7);
        int bExp = Unquantize((10 << 1) | 1, 7);

        var (bc7, data) = BC7_Make(block);
        try
        {
            BC7_AssertPixel("SharedPBit1", bc7, 0, 0, rExp, gExp, bExp, 255);
            BC7_CompareWithUnity("SharedPBit", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 19. Mode 3 unique p-bits test
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Mode3_UniquePBits")]
    public void TestMode3UniquePBits()
    {
        // partition=0, all R=100, G=50, B=25
        // pbit0=0 -> s0e0=(100<<1)|0=200
        // pbit1=1 -> s0e1=(100<<1)|1=201
        var block = new byte[16];
        var w = new BitWriter(block);
        w.Write(0b00001000, 4); // mode 3
        w.Write(0, 6); // partition 0
        w.WriteN(100, 7, 4); // R
        w.WriteN(50, 7, 4); // G
        w.WriteN(25, 7, 4); // B
        w.Write(0, 1); // pbit0 for s0e0
        w.Write(1, 1); // pbit1 for s0e1
        w.Write(0, 1); // pbit2 for s1e0
        w.Write(1, 1); // pbit3 for s1e1

        // Pixel 0 -> subset 0, index 0 -> ep0, which uses pbit0=0
        // R=(100<<1)|0=200, G=(50<<1)|0=100, B=(25<<1)|0=50
        var (bc7, data) = BC7_Make(block);
        try
        {
            BC7_AssertPixel("UniqPBit_ep0", bc7, 0, 0, 200, 100, 50, 255);
            BC7_CompareWithUnity("UniqPBit", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 20. Mode 7 partition + alpha test
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Mode7_PartAlpha")]
    public void TestMode7PartitionAlpha()
    {
        int rS0 = 20,
            gS0 = 10,
            bS0 = 5,
            aS0 = 25;
        int rS1 = 30,
            gS1 = 15,
            bS1 = 8,
            aS1 = 20;

        var block = new byte[16];
        var w = new BitWriter(block);
        w.Write(0b10000000, 8); // mode 7
        w.Write(13, 6); // partition 13

        // R: s0e0, s0e1, s1e0, s1e1
        w.Write(rS0, 5);
        w.Write(rS0, 5);
        w.Write(rS1, 5);
        w.Write(rS1, 5);
        // G
        w.Write(gS0, 5);
        w.Write(gS0, 5);
        w.Write(gS1, 5);
        w.Write(gS1, 5);
        // B
        w.Write(bS0, 5);
        w.Write(bS0, 5);
        w.Write(bS1, 5);
        w.Write(bS1, 5);
        // A
        w.Write(aS0, 5);
        w.Write(aS0, 5);
        w.Write(aS1, 5);
        w.Write(aS1, 5);
        // pbits all 0
        w.WriteN(0, 1, 4);

        int exR0 = Unquantize(rS0 << 1, 6);
        int exG0 = Unquantize(gS0 << 1, 6);
        int exB0 = Unquantize(bS0 << 1, 6);
        int exA0 = Unquantize(aS0 << 1, 6);
        int exR1 = Unquantize(rS1 << 1, 6);
        int exG1 = Unquantize(gS1 << 1, 6);
        int exB1 = Unquantize(bS1 << 1, 6);
        int exA1 = Unquantize(aS1 << 1, 6);

        var (bc7, data) = BC7_Make(block);
        try
        {
            BC7_AssertPixel("M7Part_sub0", bc7, 0, 0, exR0, exG0, exB0, exA0);
            BC7_AssertPixel("M7Part_sub1", bc7, 0, 2, exR1, exG1, exB1, exA1);
            BC7_CompareWithUnity("M7Part", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 21. Coordinate clamping
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Clamp")]
    public void TestCoordClamping()
    {
        // Mode 6 solid block
        var block = new byte[16];
        var w = new BitWriter(block);
        w.Write(0b01000000, 7); // mode 6
        w.WriteN(50, 7, 2);
        w.WriteN(30, 7, 2);
        w.WriteN(10, 7, 2);
        w.WriteN(60, 7, 2);
        w.WriteN(0, 1, 2);

        var (bc7, data) = BC7_Make(block);
        try
        {
            Color corner = bc7.GetPixel(0, 0);
            // Negative coordinates clamp to 0
            assertColorEquals("ClampNeg", bc7.GetPixel(-1, -1), corner, BC7Tol);
            assertColorEquals("ClampNegX", bc7.GetPixel(-100, 0), corner, BC7Tol);
            assertColorEquals("ClampNegY", bc7.GetPixel(0, -50), corner, BC7Tol);
            // Over-max coordinates clamp to max
            Color far = bc7.GetPixel(3, 3);
            assertColorEquals("ClampOver", bc7.GetPixel(100, 100), far, BC7Tol);
            assertColorEquals("ClampOverX", bc7.GetPixel(10, 3), far, BC7Tol);
            assertColorEquals("ClampOverY", bc7.GetPixel(3, 10), far, BC7Tol);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 22. Multi-block: 8x8 with 4 distinct blocks
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_MultiBlock")]
    public void TestMultiBlock()
    {
        var block00 = BuildSolidMode6(63, 0, 0, 63); // red
        var block10 = BuildSolidMode6(0, 63, 0, 50); // green
        var block01 = BuildSolidMode6(0, 0, 63, 37); // blue
        var block11 = BuildSolidMode6(63, 63, 0, 25); // yellow

        var allBlocks = new byte[64];
        Array.Copy(block00, 0, allBlocks, 0, 16);
        Array.Copy(block10, 0, allBlocks, 16, 16);
        Array.Copy(block01, 0, allBlocks, 32, 16);
        Array.Copy(block11, 0, allBlocks, 48, 16);

        var (bc7, data) = BC7_Make(allBlocks, 8, 8);
        try
        {
            // Block (0,0): pixels (0-3, 0-3) -> R=(63<<1)=126
            BC7_AssertPixel("Block00", bc7, 1, 1, 126, 0, 0, 126);
            // Block (1,0): pixels (4-7, 0-3)
            BC7_AssertPixel("Block10", bc7, 5, 1, 0, 126, 0, 100);
            // Block (0,1): pixels (0-3, 4-7)
            BC7_AssertPixel("Block01", bc7, 1, 5, 0, 0, 126, 74);
            // Block (1,1): pixels (4-7, 4-7)
            BC7_AssertPixel("Block11", bc7, 5, 5, 126, 126, 0, 50);

            BC7_CompareWithUnity("MultiBlock", allBlocks, 8, 8);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 23. Non-power-of-two dimensions (12x8 = 3x2 blocks)
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_NPOT")]
    public void TestNonPowerOfTwo()
    {
        int w = 12,
            h = 8;
        int blocksX = (w + 3) / 4;
        int blocksY = (h + 3) / 4;
        var allBlocks = new byte[blocksX * blocksY * 16];

        for (int by = 0; by < blocksY; by++)
        for (int bx = 0; bx < blocksX; bx++)
        {
            int blockIdx = (by * blocksX + bx) * 16;
            int rv = 10 + bx * 20;
            int gv = 10 + by * 20;
            int bv = 30;
            int av = 50;

            var blk = new byte[16];
            var bw = new BitWriter(blk);
            bw.Write(0b01000000, 7); // mode 6
            bw.Write(rv, 7);
            bw.Write(rv, 7);
            bw.Write(gv, 7);
            bw.Write(gv, 7);
            bw.Write(bv, 7);
            bw.Write(bv, 7);
            bw.Write(av, 7);
            bw.Write(av, 7);
            bw.Write(0, 1);
            bw.Write(0, 1);
            Array.Copy(blk, 0, allBlocks, blockIdx, 16);
        }

        var (bc7, data) = BC7_Make(allBlocks, w, h);
        try
        {
            for (int by = 0; by < blocksY; by++)
            for (int bx = 0; bx < blocksX; bx++)
            {
                int px = bx * 4 + 1;
                int py = by * 4 + 1;
                int rv = 10 + bx * 20;
                int gv = 10 + by * 20;
                BC7_AssertPixel($"NPOT_block({bx},{by})", bc7, px, py, rv << 1, gv << 1, 60, 100);
            }

            BC7_CompareWithUnity("NPOT", allBlocks, w, h);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 24. Mip levels: 8x8 with 2 mips
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Mip")]
    public void TestMipLevels()
    {
        // Mip 0: 8x8 = 2x2 = 4 blocks x 16 = 64 bytes
        // Mip 1: 4x4 = 1x1 = 1 block  x 16 = 16 bytes
        var greenBlock = BuildSolidMode6(0, 63, 0, 63);
        var blueBlock = BuildSolidMode6(0, 0, 63, 32);

        var bytes = new byte[80];
        for (int i = 0; i < 4; i++)
            Array.Copy(greenBlock, 0, bytes, i * 16, 16);
        Array.Copy(blueBlock, 0, bytes, 64, 16);

        var (bc7, data) = BC7_Make(bytes, 8, 8, 2);
        try
        {
            // Mip 0: green
            BC7_AssertPixel("Mip0(0,0)", bc7, 0, 0, 0, 126, 0, 126);
            BC7_AssertPixel("Mip0(7,7)", bc7, 7, 7, 0, 126, 0, 126);

            // Mip 1: blue - check via GetPixel with mipLevel parameter
            Color mip1Pixel = bc7.GetPixel(0, 0, 1);
            assertFloatEquals("Mip1.r", mip1Pixel.r, 0f, BC7Tol);
            assertFloatEquals("Mip1.b", mip1Pixel.b, 126f / 255f, BC7Tol);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 25. Constructor validation
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Ctor")]
    public void TestConstructorValidation()
    {
        // Correct size: 4x4 x 1 mip = 16 bytes
        var goodData = new NativeArray<byte>(16, Allocator.Temp);
        try
        {
            new CPUTexture2D.BC7(goodData, 4, 4, 1);
        }
        finally
        {
            goodData.Dispose();
        }

        // Too small
        var smallData = new NativeArray<byte>(15, Allocator.Temp);
        try
        {
            bool threw = false;
            try
            {
                new CPUTexture2D.BC7(smallData, 4, 4, 1);
            }
            catch (Exception)
            {
                threw = true;
            }
            if (!threw)
                throw new Exception("BC7.Ctor: expected exception for undersized data");
        }
        finally
        {
            smallData.Dispose();
        }

        // Too large
        var largeData = new NativeArray<byte>(17, Allocator.Temp);
        try
        {
            bool threw = false;
            try
            {
                new CPUTexture2D.BC7(largeData, 4, 4, 1);
            }
            catch (Exception)
            {
                threw = true;
            }
            if (!threw)
                throw new Exception("BC7.Ctor: expected exception for oversized data");
        }
        finally
        {
            largeData.Dispose();
        }

        // Multi-mip: 8x8 with 2 mips = 64 + 16 = 80 bytes
        var mipData = new NativeArray<byte>(80, Allocator.Temp);
        try
        {
            new CPUTexture2D.BC7(mipData, 8, 8, 2);
        }
        finally
        {
            mipData.Dispose();
        }
    }

    // ================================================================
    // 26. GetPixel32 matches GetPixel
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Pixel32")]
    public void TestGetPixel32()
    {
        // Mode 6 with varied endpoints
        var block = new byte[16];
        var w = new BitWriter(block);
        w.Write(0b01000000, 7);
        w.Write(50, 7);
        w.Write(100, 7);
        w.Write(30, 7);
        w.Write(60, 7);
        w.Write(10, 7);
        w.Write(40, 7);
        w.Write(60, 7);
        w.Write(90, 7);
        w.Write(0, 1);
        w.Write(1, 1);
        // Pixel 0 anchor: 3 bits = 3
        w.Write(3, 3);
        // Pixels 1-15: varied indices
        w.Write(7, 4);
        w.Write(11, 4);
        w.Write(15, 4);
        w.Write(0, 4);
        w.Write(4, 4);
        w.Write(8, 4);
        w.Write(12, 4);
        w.Write(1, 4);
        w.Write(5, 4);
        w.Write(9, 4);
        w.Write(13, 4);
        w.Write(2, 4);
        w.Write(6, 4);
        w.Write(10, 4);
        w.Write(14, 4);

        var (bc7, data) = BC7_Make(block);
        try
        {
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                Color pixel = bc7.GetPixel(x, y);
                Color32 expected32 = pixel;
                Color32 actual32 = bc7.GetPixel32(x, y);
                assertColor32Equals($"Pixel32({x},{y})", actual32, expected32, 0);
            }
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 27. GetRawTextureData returns correct bytes
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_RawData")]
    public void TestRawTextureData()
    {
        // Build a block with known data
        var block = new byte[16];
        var w = new BitWriter(block);
        w.Write(0b01000000, 7); // mode 6
        w.Write(50, 7);
        w.Write(100, 7);
        w.Write(30, 7);
        w.Write(60, 7);
        w.Write(10, 7);
        w.Write(40, 7);
        w.Write(60, 7);
        w.Write(90, 7);
        w.Write(0, 1);
        w.Write(1, 1);
        w.Write(5, 3); // anchor
        for (int i = 1; i < 16; i++)
            w.Write(i, 4);

        var (bc7, data) = BC7_Make(block);
        try
        {
            var raw = bc7.GetRawTextureData<byte>();

            if (raw.Length != block.Length)
                throw new Exception($"BC7.RawData: length {raw.Length} != expected {block.Length}");

            for (int i = 0; i < block.Length; i++)
                if (raw[i] != block[i])
                    throw new Exception($"BC7.RawData[{i}]: {raw[i]} != expected {block[i]}");
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 28. Multi-block gradient: hand-crafted Mode 6 blocks with varying endpoints
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_Gradient")]
    public void TestCompressedGradient()
    {
        // 16x16 texture = 4x4 grid of blocks, each with per-pixel gradients
        // and block-to-block endpoint variation
        var blockData = new byte[16 * 16];
        for (int by = 0; by < 4; by++)
        for (int bx = 0; bx < 4; bx++)
        {
            var block = BuildGradientMode6(
                bx * 30, bx * 30 + 20,         // R: increases with column
                by * 25, by * 25 + 20,          // G: increases with row
                127 - bx * 30, 117 - bx * 30,  // B: decreases with column
                60 + by * 10, 80 + by * 10      // A: increases with row
            );
            Array.Copy(block, 0, blockData, (by * 4 + bx) * 16, 16);
        }

        var tex = new Texture2D(16, 16, TextureFormat.BC7, false);
        tex.LoadRawTextureData(blockData);
        tex.Apply(false, false);

        var nativeCopy = new NativeArray<byte>(blockData, Allocator.Temp);
        try
        {
            var bc7 = new CPUTexture2D.BC7(nativeCopy, 16, 16, 1);

            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                Color expected = tex.GetPixel(x, y);
                Color actual = bc7.GetPixel(x, y);
                assertColorEquals($"Gradient({x},{y})", actual, expected, BC7Tol);
            }
        }
        finally
        {
            nativeCopy.Dispose();
            UnityEngine.Object.Destroy(tex);
        }
    }

    // ================================================================
    // 29. Multi-block comparison against Texture2D.GetPixel
    // ================================================================

    [TestInfo("CPUTexture2D_BC7_VsUnity")]
    public void TestVsUnity()
    {
        // 8x8 texture = 2x2 grid of blocks with distinct color ranges
        var blockData = new byte[4 * 16];
        var b0 = BuildGradientMode6(10, 60, 5, 15, 0, 10, 100, 127);   // dark reds
        var b1 = BuildGradientMode6(5, 20, 30, 100, 10, 30, 90, 120);  // greens
        var b2 = BuildGradientMode6(0, 15, 10, 25, 50, 120, 80, 110);  // blues
        var b3 = BuildGradientMode6(80, 127, 60, 100, 40, 90, 110, 127); // bright mix
        Array.Copy(b0, 0, blockData, 0, 16);
        Array.Copy(b1, 0, blockData, 16, 16);
        Array.Copy(b2, 0, blockData, 32, 16);
        Array.Copy(b3, 0, blockData, 48, 16);

        var tex = new Texture2D(8, 8, TextureFormat.BC7, false);
        tex.LoadRawTextureData(blockData);
        tex.Apply(false, false);

        var nativeCopy = new NativeArray<byte>(blockData, Allocator.Temp);
        try
        {
            var bc7 = new CPUTexture2D.BC7(nativeCopy, 8, 8, 1);

            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                Color expected = tex.GetPixel(x, y);
                Color actual = bc7.GetPixel(x, y);
                assertColorEquals($"VsTex2D({x},{y})", actual, expected, BC7Tol);

                Color32 actual32 = bc7.GetPixel32(x, y);
                assertColor32Equals(
                    $"VsTex2D.C32({x},{y})",
                    actual32,
                    (Color32)expected,
                    1
                );
            }
        }
        finally
        {
            nativeCopy.Dispose();
            UnityEngine.Object.Destroy(tex);
        }
    }
}
