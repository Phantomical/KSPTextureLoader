using System;
using KSP.Testing;
using KSPTextureLoader;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoaderTests;

/// <summary>
/// Comprehensive tests for <see cref="CPUTexture2D.BC5"/>.
///
/// BC5/RGTC2 format: 16-byte blocks covering 4x4 pixels.
/// Each block = 8-byte BC4 block for red + 8-byte BC4 block for green.
/// Blue = 0.0, Alpha = 0.0 for all pixels.
///
/// BC4 block: two 8-bit endpoints (e0, e1) + 16×3-bit indices.
///   If e0 > e1: 8-value palette interpolated between endpoints.
///   If e0 &lt;= e1: 6-value palette + literal 0.0 and 1.0.
/// </summary>
public class BC5Tests : CPUTexture2DTests
{
    const float BC5Tol = 0.005f; // slightly more than 1/255
    const float BC5LossyTol = 0.05f; // for lossy compression comparisons

    // ---- BC5 block construction helpers ----

    /// <summary>
    /// Build a single BC4 block (8 bytes) from endpoints and 16 3-bit indices.
    /// </summary>
    static byte[] BC5_BuildBC4Block(byte e0, byte e1, byte[] indices)
    {
        var block = new byte[8];
        block[0] = e0;
        block[1] = e1;

        // Pack 16 × 3-bit indices into 48 bits (6 bytes), LSB-first
        ulong bits = 0;
        for (int i = 0; i < 16; i++)
            bits |= (ulong)(indices[i] & 7) << (i * 3);
        for (int i = 0; i < 6; i++)
            block[2 + i] = (byte)(bits >> (i * 8));

        return block;
    }

    /// <summary>
    /// Build a single BC5 block (16 bytes) from human-readable parameters.
    /// </summary>
    static byte[] BC5_BuildBlock(
        byte red0,
        byte red1,
        byte[] redIndices,
        byte green0,
        byte green1,
        byte[] greenIndices
    )
    {
        var block = new byte[16];
        var redBlock = BC5_BuildBC4Block(red0, red1, redIndices);
        var greenBlock = BC5_BuildBC4Block(green0, green1, greenIndices);
        Array.Copy(redBlock, 0, block, 0, 8);
        Array.Copy(greenBlock, 0, block, 8, 8);
        return block;
    }

    /// <summary>Build a solid BC5 block where all pixels have the same red and green.</summary>
    static byte[] BC5_BuildSolidBlock(byte red, byte green)
    {
        return BC5_BuildBlock(red, red, new byte[16], green, green, new byte[16]);
    }

    /// <summary>
    /// Create a CPUTexture2D.BC5 from raw block data and the NativeArray backing it.
    /// Caller must dispose the returned NativeArray.
    /// </summary>
    static (CPUTexture2D.BC5 tex, NativeArray<byte> data) BC5_Make(
        byte[] blockData,
        int width = 4,
        int height = 4,
        int mipCount = 1
    )
    {
        var native = new NativeArray<byte>(blockData, Allocator.Temp);
        return (new CPUTexture2D.BC5(native, width, height, mipCount), native);
    }

    /// <summary>
    /// Compute the expected float value from a BC4 palette given endpoints and index.
    /// </summary>
    static float BC5_PaletteValue(byte e0, byte e1, int code)
    {
        float f0 = e0 / 255f;
        float f1 = e1 / 255f;

        if (e0 > e1)
        {
            return code switch
            {
                0 => f0,
                1 => f1,
                2 => (6f * f0 + 1f * f1) / 7f,
                3 => (5f * f0 + 2f * f1) / 7f,
                4 => (4f * f0 + 3f * f1) / 7f,
                5 => (3f * f0 + 4f * f1) / 7f,
                6 => (2f * f0 + 5f * f1) / 7f,
                _ => (1f * f0 + 6f * f1) / 7f,
            };
        }
        else
        {
            return code switch
            {
                0 => f0,
                1 => f1,
                2 => (4f * f0 + 1f * f1) / 5f,
                3 => (3f * f0 + 2f * f1) / 5f,
                4 => (2f * f0 + 3f * f1) / 5f,
                5 => (1f * f0 + 4f * f1) / 5f,
                6 => 0f,
                _ => 1f,
            };
        }
    }

    /// <summary>
    /// Load the same raw BC5 block data into a Unity Texture2D and compare
    /// every pixel against our decoder.
    /// </summary>
    void BC5_CompareWithUnity(string label, byte[] blockData, int w = 4, int h = 4)
    {
        var tex = new Texture2D(w, h, TextureFormat.BC5, false);
        tex.LoadRawTextureData(blockData);
        tex.Apply(false, false);

        var (bc5, data) = BC5_Make(blockData, w, h);
        try
        {
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color expected = tex.GetPixel(x, y);
                Color actual = bc5.GetPixel(x, y);
                assertColorEquals($"{label}.Unity({x},{y})", actual, expected, BC5Tol);
            }
        }
        finally
        {
            data.Dispose();
            UnityEngine.Object.Destroy(tex);
        }
    }

    // ================================================================
    // 1. Solid block: all pixels same red and green
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_Solid")]
    public void TestBC5_Solid()
    {
        byte red = 200;
        byte green = 100;
        var block = BC5_BuildSolidBlock(red, green);

        var (bc5, data) = BC5_Make(block);
        try
        {
            var expected = new Color(red / 255f, green / 255f, 0f, 0f);

            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                assertColorEquals($"Solid({x},{y})", bc5.GetPixel(x, y), expected, BC5Tol);

            BC5_CompareWithUnity("Solid", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 2. Red channel 8-value palette (e0 > e1)
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_Red8Palette")]
    public void TestBC5_Red8ValuePalette()
    {
        byte r0 = 240;
        byte r1 = 30;

        var redIdx = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7 };
        var block = BC5_BuildBlock(r0, r1, redIdx, 128, 128, new byte[16]);

        var (bc5, data) = BC5_Make(block);
        try
        {
            for (int i = 0; i < 8; i++)
            {
                int x = i % 4;
                int y = i / 4;
                float expected = BC5_PaletteValue(r0, r1, i);
                assertFloatEquals($"R8_idx{i}", bc5.GetPixel(x, y).r, expected, BC5Tol);
            }

            // Green should be constant 128/255
            float expG = 128f / 255f;
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                assertFloatEquals($"R8_green({x},{y})", bc5.GetPixel(x, y).g, expG, BC5Tol);

            BC5_CompareWithUnity("Red8Palette", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 3. Green channel 8-value palette (e0 > e1)
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_Green8Palette")]
    public void TestBC5_Green8ValuePalette()
    {
        byte g0 = 220;
        byte g1 = 40;

        var greenIdx = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7 };
        var block = BC5_BuildBlock(128, 128, new byte[16], g0, g1, greenIdx);

        var (bc5, data) = BC5_Make(block);
        try
        {
            for (int i = 0; i < 8; i++)
            {
                int x = i % 4;
                int y = i / 4;
                float expected = BC5_PaletteValue(g0, g1, i);
                assertFloatEquals($"G8_idx{i}", bc5.GetPixel(x, y).g, expected, BC5Tol);
            }

            // Red should be constant 128/255
            float expR = 128f / 255f;
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                assertFloatEquals($"G8_red({x},{y})", bc5.GetPixel(x, y).r, expR, BC5Tol);

            BC5_CompareWithUnity("Green8Palette", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 4. Red channel 6-value palette + special 0.0/1.0 (e0 <= e1)
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_Red6Palette")]
    public void TestBC5_Red6ValuePalette()
    {
        byte r0 = 50;
        byte r1 = 200;

        var redIdx = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7 };
        var block = BC5_BuildBlock(r0, r1, redIdx, 128, 128, new byte[16]);

        var (bc5, data) = BC5_Make(block);
        try
        {
            for (int i = 0; i < 8; i++)
            {
                int x = i % 4;
                int y = i / 4;
                float expected = BC5_PaletteValue(r0, r1, i);
                assertFloatEquals($"R6_idx{i}", bc5.GetPixel(x, y).r, expected, BC5Tol);
            }

            // Specifically verify code 6 = 0.0 and code 7 = 1.0
            assertFloatEquals("R6_code6", bc5.GetPixel(2, 1).r, 0f, BC5Tol);
            assertFloatEquals("R6_code7", bc5.GetPixel(3, 1).r, 1f, BC5Tol);

            BC5_CompareWithUnity("Red6Palette", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 5. Green channel 6-value palette + special 0.0/1.0 (e0 <= e1)
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_Green6Palette")]
    public void TestBC5_Green6ValuePalette()
    {
        byte g0 = 60;
        byte g1 = 180;

        var greenIdx = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7 };
        var block = BC5_BuildBlock(128, 128, new byte[16], g0, g1, greenIdx);

        var (bc5, data) = BC5_Make(block);
        try
        {
            for (int i = 0; i < 8; i++)
            {
                int x = i % 4;
                int y = i / 4;
                float expected = BC5_PaletteValue(g0, g1, i);
                assertFloatEquals($"G6_idx{i}", bc5.GetPixel(x, y).g, expected, BC5Tol);
            }

            // Specifically verify code 6 = 0.0 and code 7 = 1.0
            assertFloatEquals("G6_code6", bc5.GetPixel(2, 1).g, 0f, BC5Tol);
            assertFloatEquals("G6_code7", bc5.GetPixel(3, 1).g, 1f, BC5Tol);

            BC5_CompareWithUnity("Green6Palette", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 6. Equal endpoints: degenerate e0 == e1
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_EqualEndpoints")]
    public void TestBC5_EqualEndpoints()
    {
        // When e0 == e1, the 6-value path is taken (e0 <= e1).
        // Codes 0-5 all equal e0. Code 6 = 0. Code 7 = 1.
        byte r = 128;
        byte g = 100;
        float fr = r / 255f;
        float fg = g / 255f;

        var indices = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 0, 0, 0, 0, 0, 0, 0 };
        var block = BC5_BuildBlock(r, r, indices, g, g, indices);

        var (bc5, data) = BC5_Make(block);
        try
        {
            // Codes 0-5 all produce the endpoint value
            for (int x = 0; x < 4; x++)
            {
                assertFloatEquals($"REq_idx{x}", bc5.GetPixel(x, 0).r, fr, BC5Tol);
                assertFloatEquals($"GEq_idx{x}", bc5.GetPixel(x, 0).g, fg, BC5Tol);
            }
            for (int x = 0; x < 2; x++)
            {
                assertFloatEquals($"REq_idx{x + 4}", bc5.GetPixel(x, 1).r, fr, BC5Tol);
                assertFloatEquals($"GEq_idx{x + 4}", bc5.GetPixel(x, 1).g, fg, BC5Tol);
            }
            // Code 6 = 0.0
            assertFloatEquals("REq_idx6", bc5.GetPixel(2, 1).r, 0f, BC5Tol);
            assertFloatEquals("GEq_idx6", bc5.GetPixel(2, 1).g, 0f, BC5Tol);
            // Code 7 = 1.0
            assertFloatEquals("REq_idx7", bc5.GetPixel(3, 1).r, 1f, BC5Tol);
            assertFloatEquals("GEq_idx7", bc5.GetPixel(3, 1).g, 1f, BC5Tol);

            BC5_CompareWithUnity("EqualEndpoints", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 7. Full range: endpoints at 255 and 0
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_FullRange")]
    public void TestBC5_FullRange()
    {
        var block = BC5_BuildBlock(
            255,
            0,
            new byte[] { 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1 },
            0,
            255,
            new byte[] { 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0 }
        );

        var (bc5, data) = BC5_Make(block);
        try
        {
            // Red: e0=255, e1=0, code 0=1.0, code 1=0.0
            assertFloatEquals("FullR_max", bc5.GetPixel(0, 0).r, 1f, BC5Tol);
            assertFloatEquals("FullR_min", bc5.GetPixel(1, 0).r, 0f, BC5Tol);
            // Green: e0=0, e1=255, code 1=1.0, code 0=0.0
            assertFloatEquals("FullG_max", bc5.GetPixel(0, 0).g, 1f, BC5Tol);
            assertFloatEquals("FullG_min", bc5.GetPixel(1, 0).g, 0f, BC5Tol);

            BC5_CompareWithUnity("FullRange", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 8. Blue and alpha channels are always 0.0
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_BlueAlpha")]
    public void TestBC5_BlueAndAlphaAreZero()
    {
        // Use varied indices to make sure blue/alpha are always 0.0 regardless of R/G
        var redIdx = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0 };
        var greenIdx = new byte[] { 7, 6, 5, 4, 3, 2, 1, 0, 0, 1, 2, 3, 4, 5, 6, 7 };
        var block = BC5_BuildBlock(200, 50, redIdx, 180, 30, greenIdx);

        var (bc5, data) = BC5_Make(block);
        try
        {
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                Color pixel = bc5.GetPixel(x, y);
                assertFloatEquals($"Blue({x},{y})", pixel.b, 0f, BC5Tol);
                assertFloatEquals($"Alpha({x},{y})", pixel.a, 0f, BC5Tol);
            }

            BC5_CompareWithUnity("BlueAlpha", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 9. Bit packing across byte boundaries
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_BitPacking")]
    public void TestBC5_BitPacking()
    {
        // The 48-bit index bitfield spans 6 bytes (3 bits per pixel).
        // Bits straddle byte boundaries at pixel indices 2, 5, 10, 13.
        // Use a forward+reverse pattern to exercise all 16 positions.
        byte r0 = 200;
        byte r1 = 50;
        byte g0 = 180;
        byte g1 = 30;

        var redIdx = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0 };
        var greenIdx = new byte[] { 7, 6, 5, 4, 3, 2, 1, 0, 0, 1, 2, 3, 4, 5, 6, 7 };
        var block = BC5_BuildBlock(r0, r1, redIdx, g0, g1, greenIdx);

        var (bc5, data) = BC5_Make(block);
        try
        {
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int pi = y * 4 + x;
                float expR = BC5_PaletteValue(r0, r1, redIdx[pi]);
                float expG = BC5_PaletteValue(g0, g1, greenIdx[pi]);
                assertFloatEquals(
                    $"BitR({x},{y})_idx{redIdx[pi]}",
                    bc5.GetPixel(x, y).r,
                    expR,
                    BC5Tol
                );
                assertFloatEquals(
                    $"BitG({x},{y})_idx{greenIdx[pi]}",
                    bc5.GetPixel(x, y).g,
                    expG,
                    BC5Tol
                );
            }

            BC5_CompareWithUnity("BitPacking", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 10. Independent channels: red and green vary independently
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_IndependentChannels")]
    public void TestBC5_IndependentChannels()
    {
        // Red uses 8-value palette (r0 > r1), green uses 6-value palette (g0 <= g1)
        byte r0 = 240;
        byte r1 = 30;
        byte g0 = 50;
        byte g1 = 200;

        // Opposite index patterns to verify channels are decoded independently
        var redIdx = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7 };
        var greenIdx = new byte[] { 7, 6, 5, 4, 3, 2, 1, 0, 7, 6, 5, 4, 3, 2, 1, 0 };
        var block = BC5_BuildBlock(r0, r1, redIdx, g0, g1, greenIdx);

        var (bc5, data) = BC5_Make(block);
        try
        {
            // Pixel (0,0): red code 0 (=r0), green code 7 (=1.0 in 6-value mode)
            Color p00 = bc5.GetPixel(0, 0);
            assertFloatEquals("Ind(0,0).r", p00.r, r0 / 255f, BC5Tol);
            assertFloatEquals("Ind(0,0).g", p00.g, 1f, BC5Tol);

            // Pixel (1,0): red code 1 (=r1), green code 6 (=0.0 in 6-value mode)
            Color p10 = bc5.GetPixel(1, 0);
            assertFloatEquals("Ind(1,0).r", p10.r, r1 / 255f, BC5Tol);
            assertFloatEquals("Ind(1,0).g", p10.g, 0f, BC5Tol);

            // Pixel (2,0): red code 2 (interpolated), green code 5 (interpolated)
            Color p20 = bc5.GetPixel(2, 0);
            assertFloatEquals("Ind(2,0).r", p20.r, BC5_PaletteValue(r0, r1, 2), BC5Tol);
            assertFloatEquals("Ind(2,0).g", p20.g, BC5_PaletteValue(g0, g1, 5), BC5Tol);

            BC5_CompareWithUnity("IndependentChannels", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 11. Coordinate clamping: out-of-bounds x, y
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_Clamp")]
    public void TestBC5_CoordClamping()
    {
        var block = BC5_BuildSolidBlock(200, 100);
        var (bc5, data) = BC5_Make(block);
        try
        {
            Color corner = bc5.GetPixel(0, 0);
            // Negative coordinates clamp to 0
            assertColorEquals("ClampNeg", bc5.GetPixel(-1, -1), corner, BC5Tol);
            assertColorEquals("ClampNegX", bc5.GetPixel(-100, 0), corner, BC5Tol);
            assertColorEquals("ClampNegY", bc5.GetPixel(0, -50), corner, BC5Tol);
            // Over-max coordinates clamp to max
            Color far = bc5.GetPixel(3, 3);
            assertColorEquals("ClampOver", bc5.GetPixel(100, 100), far, BC5Tol);
            assertColorEquals("ClampOverX", bc5.GetPixel(10, 3), far, BC5Tol);
            assertColorEquals("ClampOverY", bc5.GetPixel(3, 10), far, BC5Tol);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 12. Multi-block: 8x8 with 4 distinct blocks (2x2 grid)
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_MultiBlock")]
    public void TestBC5_MultiBlock()
    {
        var block00 = BC5_BuildSolidBlock(255, 0); // red=1, green=0
        var block10 = BC5_BuildSolidBlock(0, 255); // red=0, green=1
        var block01 = BC5_BuildSolidBlock(128, 64); // red=0.5, green=0.25
        var block11 = BC5_BuildSolidBlock(64, 128); // red=0.25, green=0.5

        var allBlocks = new byte[64];
        Array.Copy(block00, 0, allBlocks, 0, 16);
        Array.Copy(block10, 0, allBlocks, 16, 16);
        Array.Copy(block01, 0, allBlocks, 32, 16);
        Array.Copy(block11, 0, allBlocks, 48, 16);

        var (bc5, data) = BC5_Make(allBlocks, 8, 8);
        try
        {
            // Block (0,0): pixels (0-3, 0-3)
            assertColorEquals(
                "Block00",
                bc5.GetPixel(1, 1),
                new Color(255f / 255f, 0f / 255f, 0f, 0f),
                BC5Tol
            );

            // Block (1,0): pixels (4-7, 0-3)
            assertColorEquals(
                "Block10",
                bc5.GetPixel(5, 1),
                new Color(0f / 255f, 255f / 255f, 0f, 0f),
                BC5Tol
            );

            // Block (0,1): pixels (0-3, 4-7)
            assertColorEquals(
                "Block01",
                bc5.GetPixel(1, 5),
                new Color(128f / 255f, 64f / 255f, 0f, 0f),
                BC5Tol
            );

            // Block (1,1): pixels (4-7, 4-7)
            assertColorEquals(
                "Block11",
                bc5.GetPixel(5, 5),
                new Color(64f / 255f, 128f / 255f, 0f, 0f),
                BC5Tol
            );

            BC5_CompareWithUnity("MultiBlock", allBlocks, 8, 8);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 13. Non-power-of-two dimensions (12x8 = 3x2 blocks)
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_NPOT")]
    public void TestBC5_NonPowerOfTwo()
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
            byte red = (byte)(50 + bx * 80);
            byte green = (byte)(50 + by * 80);
            var block = BC5_BuildSolidBlock(red, green);
            Array.Copy(block, 0, allBlocks, blockIdx, 16);
        }

        var (bc5, data) = BC5_Make(allBlocks, w, h);
        try
        {
            for (int by = 0; by < blocksY; by++)
            for (int bx = 0; bx < blocksX; bx++)
            {
                int px = bx * 4 + 1;
                int py = by * 4 + 1;
                byte red = (byte)(50 + bx * 80);
                byte green = (byte)(50 + by * 80);
                assertColorEquals(
                    $"NPOT_block({bx},{by})",
                    bc5.GetPixel(px, py),
                    new Color(red / 255f, green / 255f, 0f, 0f),
                    BC5Tol
                );
            }

            BC5_CompareWithUnity("NPOT", allBlocks, w, h);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 14. Mip levels: 8x8 with 2 mips
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_Mip")]
    public void TestBC5_MipLevels()
    {
        // Mip 0: 8x8 = 2x2 = 4 blocks × 16 = 64 bytes
        // Mip 1: 4x4 = 1x1 = 1 block  × 16 = 16 bytes
        var mip0Block = BC5_BuildSolidBlock(200, 100);
        var mip1Block = BC5_BuildSolidBlock(50, 220);

        var bytes = new byte[80];
        for (int i = 0; i < 4; i++)
            Array.Copy(mip0Block, 0, bytes, i * 16, 16);
        Array.Copy(mip1Block, 0, bytes, 64, 16);

        var (bc5, data) = BC5_Make(bytes, 8, 8, 2);
        try
        {
            var mip0Expected = new Color(200f / 255f, 100f / 255f, 0f, 0f);
            var mip1Expected = new Color(50f / 255f, 220f / 255f, 0f, 0f);

            // Mip 0: all corners
            assertColorEquals("Mip0(0,0)", bc5.GetPixel(0, 0, 0), mip0Expected, BC5Tol);
            assertColorEquals("Mip0(7,7)", bc5.GetPixel(7, 7, 0), mip0Expected, BC5Tol);

            // Mip 1: all corners
            assertColorEquals("Mip1(0,0)", bc5.GetPixel(0, 0, 1), mip1Expected, BC5Tol);
            assertColorEquals("Mip1(3,3)", bc5.GetPixel(3, 3, 1), mip1Expected, BC5Tol);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 15. Constructor validation: correct, too small, too large, multi-mip
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_Ctor")]
    public void TestBC5_ConstructorValidation()
    {
        // Correct size: 4x4 × 1 mip = 16 bytes
        var goodData = new NativeArray<byte>(16, Allocator.Temp);
        try
        {
            new CPUTexture2D.BC5(goodData, 4, 4, 1);
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
                new CPUTexture2D.BC5(smallData, 4, 4, 1);
            }
            catch (Exception)
            {
                threw = true;
            }
            if (!threw)
                throw new Exception("BC5.Ctor: expected exception for undersized data");
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
                new CPUTexture2D.BC5(largeData, 4, 4, 1);
            }
            catch (Exception)
            {
                threw = true;
            }
            if (!threw)
                throw new Exception("BC5.Ctor: expected exception for oversized data");
        }
        finally
        {
            largeData.Dispose();
        }

        // Multi-mip: 8x8 with 2 mips = 64 + 16 = 80 bytes
        var mipData = new NativeArray<byte>(80, Allocator.Temp);
        try
        {
            new CPUTexture2D.BC5(mipData, 8, 8, 2);
        }
        finally
        {
            mipData.Dispose();
        }
    }

    // ================================================================
    // 16. GetPixel32 matches GetPixel
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_Pixel32")]
    public void TestBC5_GetPixel32()
    {
        var redIdx = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0 };
        var greenIdx = new byte[] { 7, 6, 5, 4, 3, 2, 1, 0, 0, 1, 2, 3, 4, 5, 6, 7 };
        var block = BC5_BuildBlock(240, 30, redIdx, 200, 50, greenIdx);

        var (bc5, data) = BC5_Make(block);
        try
        {
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                Color pixel = bc5.GetPixel(x, y);
                Color32 expected32 = pixel;
                Color32 actual32 = bc5.GetPixel32(x, y);
                assertColor32Equals($"Pixel32({x},{y})", actual32, expected32, 0);
            }
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 17. GetRawTextureData returns correct bytes
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_RawData")]
    public void TestBC5_RawTextureData()
    {
        var redIdx = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0 };
        var greenIdx = new byte[] { 7, 6, 5, 4, 3, 2, 1, 0, 0, 1, 2, 3, 4, 5, 6, 7 };
        var block = BC5_BuildBlock(200, 100, redIdx, 180, 60, greenIdx);

        var (bc5, data) = BC5_Make(block);
        try
        {
            var raw = bc5.GetRawTextureData<byte>();

            if (raw.Length != block.Length)
                throw new Exception($"BC5.RawData: length {raw.Length} != expected {block.Length}");

            for (int i = 0; i < block.Length; i++)
                if (raw[i] != block[i])
                    throw new Exception($"BC5.RawData[{i}]: {raw[i]} != expected {block[i]}");
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 18. Mixed palette modes: red 8-value, green 6-value
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_MixedModes")]
    public void TestBC5_MixedPaletteModes()
    {
        // Red: 8-value palette (r0 > r1)
        byte r0 = 200;
        byte r1 = 50;
        // Green: 6-value palette (g0 <= g1)
        byte g0 = 60;
        byte g1 = 180;

        // All 8 indices for both channels
        var indices = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7 };
        var block = BC5_BuildBlock(r0, r1, indices, g0, g1, indices);

        var (bc5, data) = BC5_Make(block);
        try
        {
            for (int i = 0; i < 8; i++)
            {
                int x = i % 4;
                int y = i / 4;
                Color pixel = bc5.GetPixel(x, y);

                float expR = BC5_PaletteValue(r0, r1, i);
                float expG = BC5_PaletteValue(g0, g1, i);

                assertFloatEquals($"MixedR_idx{i}", pixel.r, expR, BC5Tol);
                assertFloatEquals($"MixedG_idx{i}", pixel.g, expG, BC5Tol);
            }

            // Red code 6: interpolated value (8-value palette)
            // Green code 6: literal 0.0 (6-value palette)
            assertFloatEquals(
                "MixedR_code6_interp",
                bc5.GetPixel(2, 1).r,
                BC5_PaletteValue(r0, r1, 6),
                BC5Tol
            );
            assertFloatEquals("MixedG_code6_zero", bc5.GetPixel(2, 1).g, 0f, BC5Tol);

            // Red code 7: interpolated value (8-value palette)
            // Green code 7: literal 1.0 (6-value palette)
            assertFloatEquals(
                "MixedR_code7_interp",
                bc5.GetPixel(3, 1).r,
                BC5_PaletteValue(r0, r1, 7),
                BC5Tol
            );
            assertFloatEquals("MixedG_code7_one", bc5.GetPixel(3, 1).g, 1f, BC5Tol);

            BC5_CompareWithUnity("MixedModes", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 19. Smallest possible texture: 1x1 (still a full 4x4 block)
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_1x1")]
    public void TestBC5_1x1()
    {
        byte red = 180;
        byte green = 70;
        var block = BC5_BuildSolidBlock(red, green);

        var (bc5, data) = BC5_Make(block, 1, 1);
        try
        {
            var expected = new Color(red / 255f, green / 255f, 0f, 0f);
            assertColorEquals("1x1(0,0)", bc5.GetPixel(0, 0), expected, BC5Tol);
            // Clamped out-of-bounds still returns (0,0)
            assertColorEquals("1x1_clamped", bc5.GetPixel(3, 3), expected, BC5Tol);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 21. Non-multiple-of-4 dimensions (5x3 = 2x1 blocks)
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_NonMul4")]
    public void TestBC5_NonMultipleOf4()
    {
        int w = 5,
            h = 3;
        int blocksX = (w + 3) / 4; // 2
        int blocksY = (h + 3) / 4; // 1

        var block0 = BC5_BuildSolidBlock(100, 200);
        var block1 = BC5_BuildSolidBlock(200, 100);

        var allBlocks = new byte[blocksX * blocksY * 16];
        Array.Copy(block0, 0, allBlocks, 0, 16);
        Array.Copy(block1, 0, allBlocks, 16, 16);

        var (bc5, data) = BC5_Make(allBlocks, w, h);
        try
        {
            // Block 0 covers pixels (0-3, 0-2)
            assertColorEquals(
                "NonMul4(0,0)",
                bc5.GetPixel(0, 0),
                new Color(100f / 255f, 200f / 255f, 0f, 0f),
                BC5Tol
            );
            assertColorEquals(
                "NonMul4(3,2)",
                bc5.GetPixel(3, 2),
                new Color(100f / 255f, 200f / 255f, 0f, 0f),
                BC5Tol
            );

            // Block 1 covers pixel (4, 0-2)
            assertColorEquals(
                "NonMul4(4,0)",
                bc5.GetPixel(4, 0),
                new Color(200f / 255f, 100f / 255f, 0f, 0f),
                BC5Tol
            );

            // Clamped beyond width/height
            assertColorEquals("NonMul4_clampX", bc5.GetPixel(4, 0), bc5.GetPixel(10, 0), BC5Tol);
            assertColorEquals("NonMul4_clampY", bc5.GetPixel(0, 2), bc5.GetPixel(0, 10), BC5Tol);

            BC5_CompareWithUnity("NonMul4", allBlocks, w, h);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 22. GetPixels matches GetPixel for multi-block texture
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_GetPixels")]
    public void TestBC5_GetPixels()
    {
        // Build an 8x8 texture (2x2 blocks) with varied data
        var block00 = BC5_BuildBlock(
            240,
            30,
            [0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0],
            100,
            200,
            [7, 6, 5, 4, 3, 2, 1, 0, 0, 1, 2, 3, 4, 5, 6, 7]
        );
        var block10 = BC5_BuildSolidBlock(128, 64);
        var block01 = BC5_BuildBlock(
            50,
            200,
            [0, 1, 2, 3, 4, 5, 6, 7, 0, 0, 0, 0, 0, 0, 0, 0],
            180,
            20,
            [3, 3, 3, 3, 5, 5, 5, 5, 7, 7, 7, 7, 1, 1, 1, 1]
        );
        var block11 = BC5_BuildBlock(
            0,
            255,
            [6, 7, 0, 1, 2, 3, 4, 5, 5, 4, 3, 2, 1, 0, 7, 6],
            255,
            0,
            [0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0]
        );

        var allBlocks = new byte[64];
        Array.Copy(block00, 0, allBlocks, 0, 16);
        Array.Copy(block10, 0, allBlocks, 16, 16);
        Array.Copy(block01, 0, allBlocks, 32, 16);
        Array.Copy(block11, 0, allBlocks, 48, 16);

        int w = 8,
            h = 8;
        var (bc5, data) = BC5_Make(allBlocks, w, h);
        try
        {
            var pixels = bc5.GetPixels();

            if (pixels.Length != w * h)
                throw new Exception($"BC5.GetPixels: expected {w * h} pixels, got {pixels.Length}");

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color expected = bc5.GetPixel(x, y);
                Color actual = pixels[y * w + x];
                assertColorEquals($"BC5.GetPixels({x},{y})", actual, expected, 1e-6f);
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

    [TestInfo("CPUTexture2D_BC5_GetPixels32")]
    public void TestBC5_GetPixels32()
    {
        // Build an 8x8 texture (2x2 blocks) with varied data
        var block00 = BC5_BuildBlock(
            240,
            30,
            [0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0],
            100,
            200,
            [7, 6, 5, 4, 3, 2, 1, 0, 0, 1, 2, 3, 4, 5, 6, 7]
        );
        var block10 = BC5_BuildSolidBlock(128, 64);
        var block01 = BC5_BuildBlock(
            50,
            200,
            [0, 1, 2, 3, 4, 5, 6, 7, 0, 0, 0, 0, 0, 0, 0, 0],
            180,
            20,
            [3, 3, 3, 3, 5, 5, 5, 5, 7, 7, 7, 7, 1, 1, 1, 1]
        );
        var block11 = BC5_BuildBlock(
            0,
            255,
            [6, 7, 0, 1, 2, 3, 4, 5, 5, 4, 3, 2, 1, 0, 7, 6],
            255,
            0,
            [0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0]
        );

        var allBlocks = new byte[64];
        Array.Copy(block00, 0, allBlocks, 0, 16);
        Array.Copy(block10, 0, allBlocks, 16, 16);
        Array.Copy(block01, 0, allBlocks, 32, 16);
        Array.Copy(block11, 0, allBlocks, 48, 16);

        int w = 8,
            h = 8;
        var (bc5, data) = BC5_Make(allBlocks, w, h);
        try
        {
            var pixels = bc5.GetPixels32();

            if (pixels.Length != w * h)
                throw new Exception(
                    $"BC5.GetPixels32: expected {w * h} pixels, got {pixels.Length}"
                );

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color32 expected = bc5.GetPixel32(x, y);
                Color32 actual = pixels[y * w + x];
                assertColor32Equals($"BC5.GetPixels32({x},{y})", actual, expected, 0);
            }
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 24. Mip chain byte size calculation
    // ================================================================

    [TestInfo("CPUTexture2D_BC5_MipSizes")]
    public void TestBC5_MipChainSizes()
    {
        // 16x16 with 3 mips:
        //   Mip 0: 4x4 blocks = 16 blocks × 16 = 256 bytes
        //   Mip 1: 2x2 blocks = 4 blocks  × 16 = 64 bytes
        //   Mip 2: 1x1 blocks = 1 block   × 16 = 16 bytes
        //   Total: 336 bytes
        int totalSize = 256 + 64 + 16;
        var data = new NativeArray<byte>(totalSize, Allocator.Temp);
        try
        {
            new CPUTexture2D.BC5(data, 16, 16, 3);
        }
        finally
        {
            data.Dispose();
        }

        // Wrong size should throw
        var wrongData = new NativeArray<byte>(totalSize - 1, Allocator.Temp);
        try
        {
            bool threw = false;
            try
            {
                new CPUTexture2D.BC5(wrongData, 16, 16, 3);
            }
            catch (Exception)
            {
                threw = true;
            }
            if (!threw)
                throw new Exception("BC5.MipSizes: expected exception for wrong mip chain size");
        }
        finally
        {
            wrongData.Dispose();
        }
    }
}
