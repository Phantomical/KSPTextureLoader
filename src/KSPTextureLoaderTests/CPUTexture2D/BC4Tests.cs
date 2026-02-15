using System;
using KSP.Testing;
using KSPTextureLoader;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoaderTests;

/// <summary>
/// Comprehensive tests for <see cref="CPUTexture2D.BC4"/>.
///
/// BC4 format: 8-byte blocks covering 4x4 pixels.
/// Each block = two 8-bit endpoints (r0, r1) + 16×3-bit indices (48 bits).
///
///   If r0 > r1: 8-value palette interpolated between endpoints.
///   If r0 &lt;= r1: 6-value palette + literal 0.0 and 1.0.
///
/// BC4 stores a single channel (red). Unlike uncompressed single-channel formats,
/// Unity returns G=0, B=0, A=0 for BC4's missing channels.
/// </summary>
public class BC4Tests : CPUTexture2DTests
{
    const float BC4Tol = 0.005f; // slightly more than 1/255

    // ---- BC4 block construction helpers ----

    /// <summary>
    /// Build a single BC4 block (8 bytes) from human-readable parameters.
    /// </summary>
    static byte[] BC4_BuildBlock(
        byte r0,
        byte r1,
        byte[] indices // 16 values, each 0-7
    )
    {
        var block = new byte[8];

        block[0] = r0;
        block[1] = r1;

        // Pack 16 × 3-bit indices into 48 bits (6 bytes), LSB-first
        ulong bits = 0;
        for (int i = 0; i < 16; i++)
            bits |= (ulong)(indices[i] & 7) << (i * 3);
        for (int i = 0; i < 6; i++)
            block[2 + i] = (byte)(bits >> (i * 8));

        return block;
    }

    /// <summary>Build a solid BC4 block where all pixels have the same value.</summary>
    static byte[] BC4_BuildSolidBlock(byte value)
    {
        return BC4_BuildBlock(value, value, new byte[16]);
    }

    /// <summary>
    /// Create a CPUTexture2D.BC4 from raw block data and the NativeArray backing it.
    /// Caller must dispose the returned NativeArray.
    /// </summary>
    static (CPUTexture2D.BC4 tex, NativeArray<byte> data) BC4_Make(
        byte[] blockData,
        int width = 4,
        int height = 4,
        int mipCount = 1
    )
    {
        var native = new NativeArray<byte>(blockData, Allocator.Temp);
        return (new CPUTexture2D.BC4(native, width, height, mipCount), native);
    }

    /// <summary>
    /// Load the same raw BC4 block data into a Unity Texture2D and compare
    /// every pixel against our decoder.
    /// </summary>
    void BC4_CompareWithUnity(string label, byte[] blockData, int w = 4, int h = 4)
    {
        var tex = new Texture2D(w, h, TextureFormat.BC4, false);
        tex.LoadRawTextureData(blockData);
        tex.Apply(false, false);

        var (bc4, data) = BC4_Make(blockData, w, h);
        try
        {
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color expected = tex.GetPixel(x, y);
                Color actual = bc4.GetPixel(x, y);
                assertColorEquals($"{label}.Unity({x},{y})", actual, expected, BC4Tol);
            }
        }
        finally
        {
            data.Dispose();
            UnityEngine.Object.Destroy(tex);
        }
    }

    // ================================================================
    // 1. Solid block: all pixels same value
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_Solid")]
    public void TestBC4_Solid()
    {
        byte value = 180;
        var block = BC4_BuildSolidBlock(value);

        var (bc4, data) = BC4_Make(block);
        try
        {
            float r = value / 255f;
            var expected = new Color(r, 0f, 0f, 0f);

            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                assertColorEquals($"Solid({x},{y})", bc4.GetPixel(x, y), expected, BC4Tol);

            BC4_CompareWithUnity("Solid", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 2. 8-value palette (r0 > r1): all 8 codes
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_8Value")]
    public void TestBC4_8ValuePalette()
    {
        byte r0 = 240;
        byte r1 = 30;
        float fr0 = r0 / 255f;
        float fr1 = r1 / 255f;

        var indices = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7 };
        var block = BC4_BuildBlock(r0, r1, indices);

        var (bc4, data) = BC4_Make(block);
        try
        {
            float[] expected =
            {
                fr0,
                fr1,
                (6f * fr0 + 1f * fr1) / 7f,
                (5f * fr0 + 2f * fr1) / 7f,
                (4f * fr0 + 3f * fr1) / 7f,
                (3f * fr0 + 4f * fr1) / 7f,
                (2f * fr0 + 5f * fr1) / 7f,
                (1f * fr0 + 6f * fr1) / 7f,
            };

            for (int x = 0; x < 4; x++)
                assertFloatEquals($"8Val_idx{x}", bc4.GetPixel(x, 0).r, expected[x], BC4Tol);
            for (int x = 0; x < 4; x++)
                assertFloatEquals(
                    $"8Val_idx{x + 4}",
                    bc4.GetPixel(x, 1).r,
                    expected[x + 4],
                    BC4Tol
                );

            BC4_CompareWithUnity("8Value", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 3. 6-value palette + special 0.0/1.0 (r0 <= r1)
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_6Value")]
    public void TestBC4_6ValuePalette()
    {
        byte r0 = 50;
        byte r1 = 200;
        float fr0 = r0 / 255f;
        float fr1 = r1 / 255f;

        var indices = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7 };
        var block = BC4_BuildBlock(r0, r1, indices);

        var (bc4, data) = BC4_Make(block);
        try
        {
            float[] expected =
            {
                fr0,
                fr1,
                (4f * fr0 + 1f * fr1) / 5f,
                (3f * fr0 + 2f * fr1) / 5f,
                (2f * fr0 + 3f * fr1) / 5f,
                (1f * fr0 + 4f * fr1) / 5f,
                0f, // code 6 = literal 0.0
                1f, // code 7 = literal 1.0
            };

            for (int x = 0; x < 4; x++)
                assertFloatEquals($"6Val_idx{x}", bc4.GetPixel(x, 0).r, expected[x], BC4Tol);
            for (int x = 0; x < 4; x++)
                assertFloatEquals(
                    $"6Val_idx{x + 4}",
                    bc4.GetPixel(x, 1).r,
                    expected[x + 4],
                    BC4Tol
                );

            BC4_CompareWithUnity("6Value", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 4. Equal endpoints: degenerate r0 == r1
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_EqualEndpoints")]
    public void TestBC4_EqualEndpoints()
    {
        // When r0 == r1, the 6-value path is taken (r0 <= r1).
        // Codes 0-5 all equal r0. Code 6 = 0. Code 7 = 1.
        byte r = 128;
        float fr = r / 255f;

        var indices = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 0, 0, 0, 0, 0, 0, 0 };
        var block = BC4_BuildBlock(r, r, indices);

        var (bc4, data) = BC4_Make(block);
        try
        {
            // Codes 0-5 all produce r0
            for (int x = 0; x < 4; x++)
                assertFloatEquals($"Eq_idx{x}", bc4.GetPixel(x, 0).r, fr, BC4Tol);
            for (int x = 0; x < 2; x++)
                assertFloatEquals($"Eq_idx{x + 4}", bc4.GetPixel(x, 1).r, fr, BC4Tol);
            // Code 6 = 0.0
            assertFloatEquals("Eq_idx6", bc4.GetPixel(2, 1).r, 0f, BC4Tol);
            // Code 7 = 1.0
            assertFloatEquals("Eq_idx7", bc4.GetPixel(3, 1).r, 1f, BC4Tol);

            BC4_CompareWithUnity("EqualEndpoints", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 5. Full range: r0=255, r1=0 endpoints
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_FullRange")]
    public void TestBC4_FullRange()
    {
        var block = BC4_BuildBlock(
            255,
            0,
            new byte[] { 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1 }
        );

        var (bc4, data) = BC4_Make(block);
        try
        {
            assertFloatEquals("FullRange_max", bc4.GetPixel(0, 0).r, 1f, BC4Tol);
            assertFloatEquals("FullRange_min", bc4.GetPixel(1, 0).r, 0f, BC4Tol);
            BC4_CompareWithUnity("FullRange", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 6. Zero endpoints: r0=0, r1=0
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_ZeroEndpoints")]
    public void TestBC4_ZeroEndpoints()
    {
        // r0 == r1 == 0 → 6-value path. Codes 0-5 = 0. Code 6 = 0. Code 7 = 1.
        var indices = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 0, 0, 0, 0, 0, 0, 0 };
        var block = BC4_BuildBlock(0, 0, indices);

        var (bc4, data) = BC4_Make(block);
        try
        {
            // Codes 0-6 all produce 0
            for (int x = 0; x < 4; x++)
                assertFloatEquals($"Zero_idx{x}", bc4.GetPixel(x, 0).r, 0f, BC4Tol);
            for (int x = 0; x < 3; x++)
                assertFloatEquals($"Zero_idx{x + 4}", bc4.GetPixel(x, 1).r, 0f, BC4Tol);
            // Code 7 = 1.0
            assertFloatEquals("Zero_idx7", bc4.GetPixel(3, 1).r, 1f, BC4Tol);

            BC4_CompareWithUnity("ZeroEndpoints", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 7. Max endpoints: r0=255, r1=255
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_MaxEndpoints")]
    public void TestBC4_MaxEndpoints()
    {
        // r0 == r1 == 255 → 6-value path. Codes 0-5 = 1. Code 6 = 0. Code 7 = 1.
        var indices = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 0, 0, 0, 0, 0, 0, 0 };
        var block = BC4_BuildBlock(255, 255, indices);

        var (bc4, data) = BC4_Make(block);
        try
        {
            // Codes 0-5 all produce 1.0
            for (int x = 0; x < 4; x++)
                assertFloatEquals($"Max_idx{x}", bc4.GetPixel(x, 0).r, 1f, BC4Tol);
            for (int x = 0; x < 2; x++)
                assertFloatEquals($"Max_idx{x + 4}", bc4.GetPixel(x, 1).r, 1f, BC4Tol);
            // Code 6 = 0.0
            assertFloatEquals("Max_idx6", bc4.GetPixel(2, 1).r, 0f, BC4Tol);
            // Code 7 = 1.0
            assertFloatEquals("Max_idx7", bc4.GetPixel(3, 1).r, 1f, BC4Tol);

            BC4_CompareWithUnity("MaxEndpoints", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 8. Bit packing across byte boundaries
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_BitPacking")]
    public void TestBC4_BitPacking()
    {
        // The 48-bit index bitfield spans 6 bytes (3 bits per pixel).
        // Bits straddle byte boundaries at pixel indices 2, 5, 10, 13.
        // Use a forward+reverse pattern to exercise all 16 positions.
        byte r0 = 200;
        byte r1 = 50;
        float fr0 = r0 / 255f;
        float fr1 = r1 / 255f;

        var indices = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0 };
        var block = BC4_BuildBlock(r0, r1, indices);

        var (bc4, data) = BC4_Make(block);
        try
        {
            float[] palette =
            {
                fr0,
                fr1,
                (6f * fr0 + 1f * fr1) / 7f,
                (5f * fr0 + 2f * fr1) / 7f,
                (4f * fr0 + 3f * fr1) / 7f,
                (3f * fr0 + 4f * fr1) / 7f,
                (2f * fr0 + 5f * fr1) / 7f,
                (1f * fr0 + 6f * fr1) / 7f,
            };

            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int idx = indices[y * 4 + x];
                assertFloatEquals(
                    $"BitPack({x},{y})_idx{idx}",
                    bc4.GetPixel(x, y).r,
                    palette[idx],
                    BC4Tol
                );
            }

            BC4_CompareWithUnity("BitPacking", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 9. Missing channels: G=0, B=0, A=0
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_MissingChannels")]
    public void TestBC4_MissingChannels()
    {
        // BC4 missing channels are 0, unlike uncompressed single-channel formats.
        var block = BC4_BuildBlock(
            100,
            200,
            new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7 }
        );

        var (bc4, data) = BC4_Make(block);
        try
        {
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                Color c = bc4.GetPixel(x, y);
                assertFloatEquals($"MissG({x},{y})", c.g, 0f, BC4Tol);
                assertFloatEquals($"MissB({x},{y})", c.b, 0f, BC4Tol);
                assertFloatEquals($"MissA({x},{y})", c.a, 0f, BC4Tol);
            }

            BC4_CompareWithUnity("MissingChannels", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 10. Coordinate clamping: out-of-bounds x, y
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_Clamp")]
    public void TestBC4_CoordClamping()
    {
        var block = BC4_BuildSolidBlock(180);
        var (bc4, data) = BC4_Make(block);
        try
        {
            Color corner = bc4.GetPixel(0, 0);
            // Negative coordinates clamp to 0
            assertColorEquals("ClampNeg", bc4.GetPixel(-1, -1), corner, BC4Tol);
            assertColorEquals("ClampNegX", bc4.GetPixel(-100, 0), corner, BC4Tol);
            assertColorEquals("ClampNegY", bc4.GetPixel(0, -50), corner, BC4Tol);
            // Over-max coordinates clamp to max
            Color far = bc4.GetPixel(3, 3);
            assertColorEquals("ClampOver", bc4.GetPixel(100, 100), far, BC4Tol);
            assertColorEquals("ClampOverX", bc4.GetPixel(10, 3), far, BC4Tol);
            assertColorEquals("ClampOverY", bc4.GetPixel(3, 10), far, BC4Tol);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 11. Multi-block: 8x8 with 4 distinct blocks (2x2 grid)
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_MultiBlock")]
    public void TestBC4_MultiBlock()
    {
        var block00 = BC4_BuildSolidBlock(255); // white
        var block10 = BC4_BuildSolidBlock(170); // light gray
        var block01 = BC4_BuildSolidBlock(85); // dark gray
        var block11 = BC4_BuildSolidBlock(0); // black

        var allBlocks = new byte[32];
        Array.Copy(block00, 0, allBlocks, 0, 8);
        Array.Copy(block10, 0, allBlocks, 8, 8);
        Array.Copy(block01, 0, allBlocks, 16, 8);
        Array.Copy(block11, 0, allBlocks, 24, 8);

        var (bc4, data) = BC4_Make(allBlocks, 8, 8);
        try
        {
            // Block (0,0): pixels (0-3, 0-3)
            assertFloatEquals("Block00", bc4.GetPixel(1, 1).r, 1f, BC4Tol);

            // Block (1,0): pixels (4-7, 0-3)
            assertFloatEquals("Block10", bc4.GetPixel(5, 1).r, 170f / 255f, BC4Tol);

            // Block (0,1): pixels (0-3, 4-7)
            assertFloatEquals("Block01", bc4.GetPixel(1, 5).r, 85f / 255f, BC4Tol);

            // Block (1,1): pixels (4-7, 4-7)
            assertFloatEquals("Block11", bc4.GetPixel(5, 5).r, 0f, BC4Tol);

            BC4_CompareWithUnity("MultiBlock", allBlocks, 8, 8);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 12. Non-power-of-two dimensions (12x8 = 3x2 blocks)
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_NPOT")]
    public void TestBC4_NonPowerOfTwo()
    {
        int w = 12,
            h = 8;
        int blocksX = (w + 3) / 4;
        int blocksY = (h + 3) / 4;
        var allBlocks = new byte[blocksX * blocksY * 8];

        for (int by = 0; by < blocksY; by++)
        for (int bx = 0; bx < blocksX; bx++)
        {
            int blockIdx = (by * blocksX + bx) * 8;
            byte value = (byte)(30 + (bx + by * blocksX) * 40);
            var block = BC4_BuildSolidBlock(value);
            Array.Copy(block, 0, allBlocks, blockIdx, 8);
        }

        var (bc4, data) = BC4_Make(allBlocks, w, h);
        try
        {
            for (int by = 0; by < blocksY; by++)
            for (int bx = 0; bx < blocksX; bx++)
            {
                int px = bx * 4 + 1;
                int py = by * 4 + 1;
                byte value = (byte)(30 + (bx + by * blocksX) * 40);
                assertFloatEquals(
                    $"NPOT_block({bx},{by})",
                    bc4.GetPixel(px, py).r,
                    value / 255f,
                    BC4Tol
                );
            }

            BC4_CompareWithUnity("NPOT", allBlocks, w, h);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 13. Mip levels: 8x8 with 2 mips
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_Mip")]
    public void TestBC4_MipLevels()
    {
        // Mip 0: 8x8 = 2x2 = 4 blocks × 8 = 32 bytes
        // Mip 1: 4x4 = 1x1 = 1 block  × 8 = 8 bytes
        var highBlock = BC4_BuildSolidBlock(200);
        var lowBlock = BC4_BuildSolidBlock(50);

        var bytes = new byte[40];
        for (int i = 0; i < 4; i++)
            Array.Copy(highBlock, 0, bytes, i * 8, 8);
        Array.Copy(lowBlock, 0, bytes, 32, 8);

        var (bc4, data) = BC4_Make(bytes, 8, 8, 2);
        try
        {
            // Mip 0: all pixels 200/255
            assertFloatEquals("Mip0(0,0)", bc4.GetPixel(0, 0, 0).r, 200f / 255f, BC4Tol);
            assertFloatEquals("Mip0(7,7)", bc4.GetPixel(7, 7, 0).r, 200f / 255f, BC4Tol);

            // Mip 1: all pixels 50/255
            assertFloatEquals("Mip1(0,0)", bc4.GetPixel(0, 0, 1).r, 50f / 255f, BC4Tol);
            assertFloatEquals("Mip1(3,3)", bc4.GetPixel(3, 3, 1).r, 50f / 255f, BC4Tol);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 14. Constructor validation: correct, too small, too large, multi-mip
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_Ctor")]
    public void TestBC4_ConstructorValidation()
    {
        // Correct size: 4x4 × 1 mip = 8 bytes
        var goodData = new NativeArray<byte>(8, Allocator.Temp);
        try
        {
            new CPUTexture2D.BC4(goodData, 4, 4, 1);
        }
        finally
        {
            goodData.Dispose();
        }

        // Too small
        var smallData = new NativeArray<byte>(7, Allocator.Temp);
        try
        {
            bool threw = false;
            try
            {
                new CPUTexture2D.BC4(smallData, 4, 4, 1);
            }
            catch (Exception)
            {
                threw = true;
            }
            if (!threw)
                throw new Exception("BC4.Ctor: expected exception for undersized data");
        }
        finally
        {
            smallData.Dispose();
        }

        // Too large
        var largeData = new NativeArray<byte>(9, Allocator.Temp);
        try
        {
            bool threw = false;
            try
            {
                new CPUTexture2D.BC4(largeData, 4, 4, 1);
            }
            catch (Exception)
            {
                threw = true;
            }
            if (!threw)
                throw new Exception("BC4.Ctor: expected exception for oversized data");
        }
        finally
        {
            largeData.Dispose();
        }

        // Multi-mip: 8x8 with 2 mips = 32 + 8 = 40 bytes
        var mipData = new NativeArray<byte>(40, Allocator.Temp);
        try
        {
            new CPUTexture2D.BC4(mipData, 8, 8, 2);
        }
        finally
        {
            mipData.Dispose();
        }
    }

    // ================================================================
    // 15. GetPixel32 matches GetPixel
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_Pixel32")]
    public void TestBC4_GetPixel32()
    {
        var indices = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0 };
        var block = BC4_BuildBlock(240, 30, indices);

        var (bc4, data) = BC4_Make(block);
        try
        {
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                Color pixel = bc4.GetPixel(x, y);
                Color32 expected32 = pixel;
                Color32 actual32 = bc4.GetPixel32(x, y);
                assertColor32Equals($"Pixel32({x},{y})", actual32, expected32, 0);
            }
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 16. GetRawTextureData returns correct bytes
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_RawData")]
    public void TestBC4_RawTextureData()
    {
        var block = BC4_BuildBlock(
            200,
            100,
            new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0 }
        );

        var (bc4, data) = BC4_Make(block);
        try
        {
            var raw = bc4.GetRawTextureData<byte>();

            if (raw.Length != block.Length)
                throw new Exception($"BC4.RawData: length {raw.Length} != expected {block.Length}");

            for (int i = 0; i < block.Length; i++)
                if (raw[i] != block[i])
                    throw new Exception($"BC4.RawData[{i}]: {raw[i]} != expected {block[i]}");
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 17. 8-value palette monotonicity: interpolated values are ordered
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_Monotone8")]
    public void TestBC4_8ValueMonotonicity()
    {
        // In 8-value mode, codes 0,2,3,4,5,6,7,1 should be monotonically
        // decreasing from r0 toward r1 when r0 > r1.
        byte r0 = 250;
        byte r1 = 10;

        // Row 0: codes 0,2,3,4 — Row 1: codes 5,6,7,1
        var indices = new byte[] { 0, 2, 3, 4, 5, 6, 7, 1, 0, 0, 0, 0, 0, 0, 0, 0 };
        var block = BC4_BuildBlock(r0, r1, indices);

        var (bc4, data) = BC4_Make(block);
        try
        {
            float prev = bc4.GetPixel(0, 0).r; // code 0 = r0
            // Codes 2-7 should be monotonically decreasing
            float v2 = bc4.GetPixel(1, 0).r;
            float v3 = bc4.GetPixel(2, 0).r;
            float v4 = bc4.GetPixel(3, 0).r;
            float v5 = bc4.GetPixel(0, 1).r;
            float v6 = bc4.GetPixel(1, 1).r;
            float v7 = bc4.GetPixel(2, 1).r;
            float end = bc4.GetPixel(3, 1).r; // code 1 = r1

            if (
                !(
                    prev >= v2
                    && v2 >= v3
                    && v3 >= v4
                    && v4 >= v5
                    && v5 >= v6
                    && v6 >= v7
                    && v7 >= end
                )
            )
                throw new Exception(
                    $"BC4.Monotone8: values not monotonically decreasing: "
                        + $"{prev:F4}, {v2:F4}, {v3:F4}, {v4:F4}, {v5:F4}, {v6:F4}, {v7:F4}, {end:F4}"
                );

            BC4_CompareWithUnity("Monotone8", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 18. 6-value palette monotonicity
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_Monotone6")]
    public void TestBC4_6ValueMonotonicity()
    {
        // In 6-value mode (r0 < r1), codes 0,2,3,4,5,1 should be
        // monotonically increasing from r0 toward r1.
        byte r0 = 10;
        byte r1 = 250;

        // Row 0: codes 0,2,3,4 — Row 1: codes 5,1,6,7
        var indices = new byte[] { 0, 2, 3, 4, 5, 1, 6, 7, 0, 0, 0, 0, 0, 0, 0, 0 };
        var block = BC4_BuildBlock(r0, r1, indices);

        var (bc4, data) = BC4_Make(block);
        try
        {
            float v0 = bc4.GetPixel(0, 0).r; // code 0 = r0
            float v2 = bc4.GetPixel(1, 0).r;
            float v3 = bc4.GetPixel(2, 0).r;
            float v4 = bc4.GetPixel(3, 0).r;
            float v5 = bc4.GetPixel(0, 1).r;
            float v1 = bc4.GetPixel(1, 1).r; // code 1 = r1

            if (!(v0 <= v2 && v2 <= v3 && v3 <= v4 && v4 <= v5 && v5 <= v1))
                throw new Exception(
                    $"BC4.Monotone6: values not monotonically increasing: "
                        + $"{v0:F4}, {v2:F4}, {v3:F4}, {v4:F4}, {v5:F4}, {v1:F4}"
                );

            // Code 6 = 0, Code 7 = 1 (special values)
            assertFloatEquals("Monotone6_code6", bc4.GetPixel(2, 1).r, 0f, BC4Tol);
            assertFloatEquals("Monotone6_code7", bc4.GetPixel(3, 1).r, 1f, BC4Tol);

            BC4_CompareWithUnity("Monotone6", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 19. Mode boundary: r0=1, r1=0 (8-value mode by 1)
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_ModeBoundary")]
    public void TestBC4_ModeBoundary()
    {
        // r0=1 > r1=0 → 8-value mode. All interpolated values should be
        // between 0 and 1/255.
        var indices = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 0, 0, 0, 0, 0, 0, 0 };
        var block = BC4_BuildBlock(1, 0, indices);

        var (bc4, data) = BC4_Make(block);
        try
        {
            float fr0 = 1f / 255f;
            assertFloatEquals("Boundary_code0", bc4.GetPixel(0, 0).r, fr0, BC4Tol);
            assertFloatEquals("Boundary_code1", bc4.GetPixel(1, 0).r, 0f, BC4Tol);

            // All interpolated values should be between 0 and fr0
            for (int x = 2; x < 4; x++)
            {
                float v = bc4.GetPixel(x, 0).r;
                if (v < -BC4Tol || v > fr0 + BC4Tol)
                    throw new Exception($"BC4.ModeBoundary: code {x} out of range: {v:F6}");
            }
            for (int x = 0; x < 4; x++)
            {
                float v = bc4.GetPixel(x, 1).r;
                if (v < -BC4Tol || v > fr0 + BC4Tol)
                    throw new Exception($"BC4.ModeBoundary: code {x + 4} out of range: {v:F6}");
            }

            BC4_CompareWithUnity("ModeBoundary", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 20. Adjacent endpoints: r0=100, r1=101 (6-value mode)
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_Adjacent")]
    public void TestBC4_AdjacentEndpoints()
    {
        // r0=100 < r1=101 → 6-value mode. Interpolated values are very close.
        byte r0 = 100;
        byte r1 = 101;
        float fr0 = r0 / 255f;
        float fr1 = r1 / 255f;

        var indices = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 0, 0, 0, 0, 0, 0, 0 };
        var block = BC4_BuildBlock(r0, r1, indices);

        var (bc4, data) = BC4_Make(block);
        try
        {
            assertFloatEquals("Adj_code0", bc4.GetPixel(0, 0).r, fr0, BC4Tol);
            assertFloatEquals("Adj_code1", bc4.GetPixel(1, 0).r, fr1, BC4Tol);
            assertFloatEquals("Adj_code2", bc4.GetPixel(2, 0).r, (4f * fr0 + fr1) / 5f, BC4Tol);
            assertFloatEquals(
                "Adj_code3",
                bc4.GetPixel(3, 0).r,
                (3f * fr0 + 2f * fr1) / 5f,
                BC4Tol
            );
            assertFloatEquals(
                "Adj_code4",
                bc4.GetPixel(0, 1).r,
                (2f * fr0 + 3f * fr1) / 5f,
                BC4Tol
            );
            assertFloatEquals("Adj_code5", bc4.GetPixel(1, 1).r, (fr0 + 4f * fr1) / 5f, BC4Tol);
            assertFloatEquals("Adj_code6", bc4.GetPixel(2, 1).r, 0f, BC4Tol);
            assertFloatEquals("Adj_code7", bc4.GetPixel(3, 1).r, 1f, BC4Tol);

            BC4_CompareWithUnity("Adjacent", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 21. All-same index: every pixel uses code 3
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_SameIndex")]
    public void TestBC4_AllSameIndex()
    {
        byte r0 = 200;
        byte r1 = 50;
        float fr0 = r0 / 255f;
        float fr1 = r1 / 255f;

        // All 16 pixels use code 3 = (5*r0 + 2*r1)/7
        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = 3;
        var block = BC4_BuildBlock(r0, r1, indices);

        float expected = (5f * fr0 + 2f * fr1) / 7f;

        var (bc4, data) = BC4_Make(block);
        try
        {
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                assertFloatEquals($"SameIdx({x},{y})", bc4.GetPixel(x, y).r, expected, BC4Tol);

            BC4_CompareWithUnity("SameIndex", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 22. GetPixels matches GetPixel for multi-block texture
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_GetPixels")]
    public void TestBC4_GetPixels()
    {
        // Build an 8x8 texture (2x2 blocks) with varied data
        var block00 = BC4_BuildBlock(
            240,
            30,
            new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0 }
        );
        var block10 = BC4_BuildSolidBlock(128);
        var block01 = BC4_BuildBlock(
            50,
            200,
            new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 0, 0, 0, 0, 0, 0, 0 }
        );
        var block11 = BC4_BuildBlock(
            0,
            255,
            new byte[] { 6, 7, 0, 1, 2, 3, 4, 5, 5, 4, 3, 2, 1, 0, 7, 6 }
        );

        var allBlocks = new byte[32];
        Array.Copy(block00, 0, allBlocks, 0, 8);
        Array.Copy(block10, 0, allBlocks, 8, 8);
        Array.Copy(block01, 0, allBlocks, 16, 8);
        Array.Copy(block11, 0, allBlocks, 24, 8);

        int w = 8,
            h = 8;
        var (bc4, data) = BC4_Make(allBlocks, w, h);
        try
        {
            var pixels = bc4.GetPixels();

            if (pixels.Length != w * h)
                throw new Exception($"BC4.GetPixels: expected {w * h} pixels, got {pixels.Length}");

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color expected = bc4.GetPixel(x, y);
                Color actual = pixels[y * w + x];
                assertColorEquals($"BC4.GetPixels({x},{y})", actual, expected, 1e-6f);
            }
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 23. GetPixels32 matches GetPixel32 for multi-block texture
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_GetPixels32")]
    public void TestBC4_GetPixels32()
    {
        // Build an 8x8 texture (2x2 blocks) with varied data
        var block00 = BC4_BuildBlock(240, 30, [0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0]);
        var block10 = BC4_BuildSolidBlock(128);
        var block01 = BC4_BuildBlock(50, 200, [0, 1, 2, 3, 4, 5, 6, 7, 0, 0, 0, 0, 0, 0, 0, 0]);
        var block11 = BC4_BuildBlock(0, 255, [6, 7, 0, 1, 2, 3, 4, 5, 5, 4, 3, 2, 1, 0, 7, 6]);

        var allBlocks = new byte[32];
        Array.Copy(block00, 0, allBlocks, 0, 8);
        Array.Copy(block10, 0, allBlocks, 8, 8);
        Array.Copy(block01, 0, allBlocks, 16, 8);
        Array.Copy(block11, 0, allBlocks, 24, 8);

        int w = 8,
            h = 8;
        var (bc4, data) = BC4_Make(allBlocks, w, h);
        try
        {
            var pixels = bc4.GetPixels32();

            if (pixels.Length != w * h)
                throw new Exception(
                    $"BC4.GetPixels32: expected {w * h} pixels, got {pixels.Length}"
                );

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color32 expected = bc4.GetPixel32(x, y);
                Color32 actual = pixels[y * w + x];
                assertColor32Equals($"BC4.GetPixels32({x},{y})", actual, expected, 0);
            }
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 24. Reversed endpoints 8-value: r0=30, r1=240 swapped to check
    //     that the 6-value path is correctly selected
    // ================================================================

    [TestInfo("CPUTexture2D_BC4_Reversed")]
    public void TestBC4_ReversedEndpoints()
    {
        // r0=30 < r1=240 → 6-value mode.
        // Verify that code 6 = 0 and code 7 = 1 are present.
        byte r0 = 30;
        byte r1 = 240;

        var indices = new byte[] { 6, 7, 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5 };
        var block = BC4_BuildBlock(r0, r1, indices);

        var (bc4, data) = BC4_Make(block);
        try
        {
            // Code 6 = 0.0
            assertFloatEquals("Rev_code6", bc4.GetPixel(0, 0).r, 0f, BC4Tol);
            // Code 7 = 1.0
            assertFloatEquals("Rev_code7", bc4.GetPixel(1, 0).r, 1f, BC4Tol);
            // Code 0 = r0
            assertFloatEquals("Rev_code0", bc4.GetPixel(2, 0).r, r0 / 255f, BC4Tol);
            // Code 1 = r1
            assertFloatEquals("Rev_code1", bc4.GetPixel(3, 0).r, r1 / 255f, BC4Tol);

            BC4_CompareWithUnity("Reversed", block);
        }
        finally
        {
            data.Dispose();
        }
    }
}
