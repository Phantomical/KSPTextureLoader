using System;
using KSP.Testing;
using KSPTextureLoader;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoaderTests;

/// <summary>
/// Comprehensive tests for <see cref="CPUTexture2D.DXT5"/>.
///
/// DXT5/BC3 format: 16-byte blocks covering 4x4 pixels.
/// Each block = 8-byte BC4 alpha block + 8-byte DXT1 color block.
///
/// Alpha block: two 8-bit endpoints (a0, a1) + 16×3-bit indices.
///   If a0 > a1: 8-value palette interpolated between endpoints.
///   If a0 &lt;= a1: 6-value palette + literal 0.0 and 1.0.
///
/// Color block: two RGB565 endpoints + 16×2-bit indices.
///   Our implementation always uses 4-color mode regardless of c0 vs c1 ordering.
/// </summary>
public class DXT5Tests : CPUTexture2DTests
{
    const float DXT5Tol = 0.005f; // slightly more than 1/255
    const float DXT5LossyTol = 0.05f; // for lossy compression comparisons

    // ---- DXT5 block construction helpers (ported from BurstPQS) ----

    /// <summary>Pack R, G, B (0-255) into a 16-bit RGB565 value.</summary>
    static ushort DXT5_PackRGB565(int r8, int g8, int b8)
    {
        int r5 = (r8 * 31 + 127) / 255;
        int g6 = (g8 * 63 + 127) / 255;
        int b5 = (b8 * 31 + 127) / 255;
        return (ushort)((r5 << 11) | (g6 << 5) | b5);
    }

    /// <summary>Unpack RGB565 to float components.</summary>
    static (float r, float g, float b) DXT5_UnpackRGB565(ushort c)
    {
        float r = ((c >> 11) & 0x1F) * (1f / 31f);
        float g = ((c >> 5) & 0x3F) * (1f / 63f);
        float b = (c & 0x1F) * (1f / 31f);
        return (r, g, b);
    }

    /// <summary>
    /// Build a single DXT5 block (16 bytes) from human-readable parameters.
    /// </summary>
    static byte[] DXT5_BuildBlock(
        byte alpha0,
        byte alpha1,
        byte[] alphaIndices, // 16 values, each 0-7
        ushort color0,
        ushort color1,
        byte[] colorIndices // 16 values, each 0-3
    )
    {
        var block = new byte[16];

        // Alpha block (8 bytes): 2 endpoint bytes + 6 bytes of 3-bit indices
        block[0] = alpha0;
        block[1] = alpha1;

        // Pack 16 × 3-bit alpha indices into 48 bits (6 bytes), LSB-first
        ulong alphaBits = 0;
        for (int i = 0; i < 16; i++)
            alphaBits |= (ulong)(alphaIndices[i] & 7) << (i * 3);
        for (int i = 0; i < 6; i++)
            block[2 + i] = (byte)(alphaBits >> (i * 8));

        // Color block (8 bytes): 2 endpoint shorts (LE) + 4 bytes of 2-bit indices
        block[8] = (byte)(color0 & 0xFF);
        block[9] = (byte)(color0 >> 8);
        block[10] = (byte)(color1 & 0xFF);
        block[11] = (byte)(color1 >> 8);

        for (int row = 0; row < 4; row++)
        {
            byte rowByte = 0;
            for (int col = 0; col < 4; col++)
                rowByte |= (byte)((colorIndices[row * 4 + col] & 3) << (col * 2));
            block[12 + row] = rowByte;
        }

        return block;
    }

    /// <summary>Build a solid DXT5 block where all pixels have the same color and alpha.</summary>
    static byte[] DXT5_BuildSolidBlock(byte alpha, ushort color)
    {
        return DXT5_BuildBlock(alpha, alpha, new byte[16], color, color, new byte[16]);
    }

    /// <summary>
    /// Create a CPUTexture2D.DXT5 from raw block data and the NativeArray backing it.
    /// Caller must dispose the returned NativeArray.
    /// </summary>
    static (CPUTexture2D.DXT5 tex, NativeArray<byte> data) DXT5_Make(
        byte[] blockData,
        int width = 4,
        int height = 4,
        int mipCount = 1
    )
    {
        var native = new NativeArray<byte>(blockData, Allocator.Temp);
        return (new CPUTexture2D.DXT5(native, width, height, mipCount), native);
    }

    /// <summary>
    /// Load the same raw DXT5 block data into a Unity Texture2D and compare
    /// every pixel against our decoder. Validates correctness against Unity's
    /// ground-truth decoder.
    /// </summary>
    void DXT5_CompareWithUnity(string label, byte[] blockData, int w = 4, int h = 4)
    {
        var tex = new Texture2D(w, h, TextureFormat.DXT5, false);
        tex.LoadRawTextureData(blockData);
        tex.Apply(false, false);

        var (dxt5, data) = DXT5_Make(blockData, w, h);
        try
        {
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color expected = tex.GetPixel(x, y);
                Color actual = dxt5.GetPixel(x, y);
                assertColorEquals($"{label}.Unity({x},{y})", actual, expected, DXT5Tol);
            }
        }
        finally
        {
            data.Dispose();
            UnityEngine.Object.Destroy(tex);
        }
    }

    // ================================================================
    // 1. End-to-end: Unity compress → compare all pixels
    // ================================================================

    // ================================================================
    // 2. Solid block: all pixels same color and alpha
    // ================================================================

    [TestInfo("CPUTexture2D_DXT5_Solid")]
    public void TestDXT5_Solid()
    {
        ushort red565 = DXT5_PackRGB565(255, 0, 0);
        byte alpha = 200;
        var block = DXT5_BuildSolidBlock(alpha, red565);

        var (dxt5, data) = DXT5_Make(block);
        try
        {
            var (r, g, b) = DXT5_UnpackRGB565(red565);
            float a = alpha / 255f;
            var expected = new Color(r, g, b, a);

            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                assertColorEquals($"Solid({x},{y})", dxt5.GetPixel(x, y), expected, DXT5Tol);

            DXT5_CompareWithUnity("Solid", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 3. Color interpolation: all 4 indices, c0 > c1 (4-color mode)
    // ================================================================

    [TestInfo("CPUTexture2D_DXT5_Color4")]
    public void TestDXT5_ColorInterpolation4Color()
    {
        ushort c0 = DXT5_PackRGB565(255, 0, 0);
        ushort c1 = DXT5_PackRGB565(0, 0, 255);
        if (c0 < c1)
            (c0, c1) = (c1, c0);

        var (r0, g0, b0) = DXT5_UnpackRGB565(c0);
        var (r1, g1, b1) = DXT5_UnpackRGB565(c1);

        // Each row: indices 0, 1, 2, 3
        var colorIdx = new byte[] { 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3 };
        var block = DXT5_BuildBlock(255, 255, new byte[16], c0, c1, colorIdx);

        var (dxt5, data) = DXT5_Make(block);
        try
        {
            assertColorEquals(
                "4Color_idx0",
                dxt5.GetPixel(0, 0),
                new Color(r0, g0, b0, 1f),
                DXT5Tol
            );
            assertColorEquals(
                "4Color_idx1",
                dxt5.GetPixel(1, 0),
                new Color(r1, g1, b1, 1f),
                DXT5Tol
            );
            assertColorEquals(
                "4Color_idx2",
                dxt5.GetPixel(2, 0),
                new Color((2f * r0 + r1) / 3f, (2f * g0 + g1) / 3f, (2f * b0 + b1) / 3f, 1f),
                DXT5Tol
            );
            assertColorEquals(
                "4Color_idx3",
                dxt5.GetPixel(3, 0),
                new Color((r0 + 2f * r1) / 3f, (g0 + 2f * g1) / 3f, (b0 + 2f * b1) / 3f, 1f),
                DXT5Tol
            );

            DXT5_CompareWithUnity("4Color", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 4. Alpha 8-value palette (a0 > a1)
    // ================================================================

    [TestInfo("CPUTexture2D_DXT5_Alpha8")]
    public void TestDXT5_Alpha8ValuePalette()
    {
        byte a0 = 240;
        byte a1 = 30;
        float fa0 = a0 / 255f;
        float fa1 = a1 / 255f;

        var alphaIdx = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7 };
        var block = DXT5_BuildBlock(
            a0,
            a1,
            alphaIdx,
            DXT5_PackRGB565(128, 128, 128),
            DXT5_PackRGB565(128, 128, 128),
            new byte[16]
        );

        var (dxt5, data) = DXT5_Make(block);
        try
        {
            float[] expected =
            {
                fa0,
                fa1,
                (6f * fa0 + 1f * fa1) / 7f,
                (5f * fa0 + 2f * fa1) / 7f,
                (4f * fa0 + 3f * fa1) / 7f,
                (3f * fa0 + 4f * fa1) / 7f,
                (2f * fa0 + 5f * fa1) / 7f,
                (1f * fa0 + 6f * fa1) / 7f,
            };

            for (int x = 0; x < 4; x++)
                assertFloatEquals($"A8_idx{x}", dxt5.GetPixel(x, 0).a, expected[x], DXT5Tol);
            for (int x = 0; x < 4; x++)
                assertFloatEquals(
                    $"A8_idx{x + 4}",
                    dxt5.GetPixel(x, 1).a,
                    expected[x + 4],
                    DXT5Tol
                );

            DXT5_CompareWithUnity("Alpha8", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 5. Alpha 6-value palette + special 0.0/1.0 (a0 <= a1)
    // ================================================================

    [TestInfo("CPUTexture2D_DXT5_Alpha6")]
    public void TestDXT5_Alpha6ValuePalette()
    {
        byte a0 = 50;
        byte a1 = 200;
        float fa0 = a0 / 255f;
        float fa1 = a1 / 255f;

        var alphaIdx = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7 };
        var block = DXT5_BuildBlock(
            a0,
            a1,
            alphaIdx,
            DXT5_PackRGB565(128, 128, 128),
            DXT5_PackRGB565(128, 128, 128),
            new byte[16]
        );

        var (dxt5, data) = DXT5_Make(block);
        try
        {
            float[] expected =
            {
                fa0,
                fa1,
                (4f * fa0 + 1f * fa1) / 5f,
                (3f * fa0 + 2f * fa1) / 5f,
                (2f * fa0 + 3f * fa1) / 5f,
                (1f * fa0 + 4f * fa1) / 5f,
                0f, // code 6 = literal 0.0
                1f, // code 7 = literal 1.0
            };

            for (int x = 0; x < 4; x++)
                assertFloatEquals($"A6_idx{x}", dxt5.GetPixel(x, 0).a, expected[x], DXT5Tol);
            for (int x = 0; x < 4; x++)
                assertFloatEquals(
                    $"A6_idx{x + 4}",
                    dxt5.GetPixel(x, 1).a,
                    expected[x + 4],
                    DXT5Tol
                );

            DXT5_CompareWithUnity("Alpha6", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 6. Alpha equal endpoints: degenerate a0 == a1
    // ================================================================

    [TestInfo("CPUTexture2D_DXT5_AlphaEq")]
    public void TestDXT5_AlphaEqualEndpoints()
    {
        // When a0 == a1, the 6-value path is taken (a0 <= a1).
        // Codes 0-5 all equal a0. Code 6 = 0. Code 7 = 1.
        byte a = 128;
        float fa = a / 255f;

        var alphaIdx = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 0, 0, 0, 0, 0, 0, 0 };
        var block = DXT5_BuildBlock(
            a,
            a,
            alphaIdx,
            DXT5_PackRGB565(128, 128, 128),
            DXT5_PackRGB565(128, 128, 128),
            new byte[16]
        );

        var (dxt5, data) = DXT5_Make(block);
        try
        {
            // Codes 0-5 all produce a0
            for (int x = 0; x < 4; x++)
                assertFloatEquals($"AEq_idx{x}", dxt5.GetPixel(x, 0).a, fa, DXT5Tol);
            for (int x = 0; x < 2; x++)
                assertFloatEquals($"AEq_idx{x + 4}", dxt5.GetPixel(x, 1).a, fa, DXT5Tol);
            // Code 6 = 0.0
            assertFloatEquals("AEq_idx6", dxt5.GetPixel(2, 1).a, 0f, DXT5Tol);
            // Code 7 = 1.0
            assertFloatEquals("AEq_idx7", dxt5.GetPixel(3, 1).a, 1f, DXT5Tol);

            DXT5_CompareWithUnity("AlphaEq", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 7. Alpha full range: a0=255, a1=0 endpoints
    // ================================================================

    [TestInfo("CPUTexture2D_DXT5_AlphaRange")]
    public void TestDXT5_AlphaFullRange()
    {
        var block = DXT5_BuildBlock(
            255,
            0,
            new byte[] { 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1 },
            DXT5_PackRGB565(128, 128, 128),
            DXT5_PackRGB565(128, 128, 128),
            new byte[16]
        );

        var (dxt5, data) = DXT5_Make(block);
        try
        {
            assertFloatEquals("AlphaMax", dxt5.GetPixel(0, 0).a, 1f, DXT5Tol);
            assertFloatEquals("AlphaMin", dxt5.GetPixel(1, 0).a, 0f, DXT5Tol);
            DXT5_CompareWithUnity("AlphaRange", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 8. Alpha bit packing across byte boundaries
    // ================================================================

    [TestInfo("CPUTexture2D_DXT5_AlphaBits")]
    public void TestDXT5_AlphaBitPacking()
    {
        // The 48-bit alpha index bitfield spans 6 bytes (3 bits per pixel).
        // Bits straddle byte boundaries at pixel indices 2, 5, 10, 13.
        // Use a forward+reverse pattern to exercise all 16 positions.
        byte a0 = 200;
        byte a1 = 50;
        float fa0 = a0 / 255f;
        float fa1 = a1 / 255f;

        var alphaIdx = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0 };
        var block = DXT5_BuildBlock(
            a0,
            a1,
            alphaIdx,
            DXT5_PackRGB565(128, 128, 128),
            DXT5_PackRGB565(128, 128, 128),
            new byte[16]
        );

        var (dxt5, data) = DXT5_Make(block);
        try
        {
            float[] palette =
            {
                fa0,
                fa1,
                (6f * fa0 + 1f * fa1) / 7f,
                (5f * fa0 + 2f * fa1) / 7f,
                (4f * fa0 + 3f * fa1) / 7f,
                (3f * fa0 + 4f * fa1) / 7f,
                (2f * fa0 + 5f * fa1) / 7f,
                (1f * fa0 + 6f * fa1) / 7f,
            };

            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int idx = alphaIdx[y * 4 + x];
                assertFloatEquals(
                    $"BitPack({x},{y})_idx{idx}",
                    dxt5.GetPixel(x, y).a,
                    palette[idx],
                    DXT5Tol
                );
            }

            DXT5_CompareWithUnity("BitPack", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 9. Mixed color and alpha variation within a single block
    // ================================================================

    [TestInfo("CPUTexture2D_DXT5_Mixed")]
    public void TestDXT5_MixedColorAndAlpha()
    {
        // Different color AND alpha indices per pixel to verify independent decoding.
        ushort c0 = DXT5_PackRGB565(255, 0, 0);
        ushort c1 = DXT5_PackRGB565(0, 0, 255);
        if (c0 < c1)
            (c0, c1) = (c1, c0);

        byte a0 = 240;
        byte a1 = 30;
        float fa0 = a0 / 255f;
        float fa1 = a1 / 255f;

        var (r0, g0, b0) = DXT5_UnpackRGB565(c0);
        var (r1, g1, b1) = DXT5_UnpackRGB565(c1);

        var colorIdx = new byte[] { 0, 1, 2, 3, 3, 2, 1, 0, 0, 0, 1, 1, 2, 2, 3, 3 };
        var alphaIdx = new byte[] { 0, 7, 1, 6, 2, 5, 3, 4, 4, 3, 5, 2, 6, 1, 7, 0 };

        var block = DXT5_BuildBlock(a0, a1, alphaIdx, c0, c1, colorIdx);
        var (dxt5, data) = DXT5_Make(block);
        try
        {
            // Pixel (0,0): color=c0, alpha=a0
            Color p00 = dxt5.GetPixel(0, 0);
            assertFloatEquals("Mixed(0,0).r", p00.r, r0, DXT5Tol);
            assertFloatEquals("Mixed(0,0).a", p00.a, fa0, DXT5Tol);

            // Pixel (1,0): color=c1, alpha=code7 = (a0+6*a1)/7
            Color p10 = dxt5.GetPixel(1, 0);
            assertFloatEquals("Mixed(1,0).r", p10.r, r1, DXT5Tol);
            assertFloatEquals("Mixed(1,0).a", p10.a, (fa0 + 6f * fa1) / 7f, DXT5Tol);

            // Pixel (2,0): color=(2*c0+c1)/3, alpha=a1
            Color p20 = dxt5.GetPixel(2, 0);
            assertFloatEquals("Mixed(2,0).r", p20.r, (2f * r0 + r1) / 3f, DXT5Tol);
            assertFloatEquals("Mixed(2,0).a", p20.a, fa1, DXT5Tol);

            DXT5_CompareWithUnity("Mixed", block);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 10. Coordinate clamping: out-of-bounds x, y
    // ================================================================

    [TestInfo("CPUTexture2D_DXT5_Clamp")]
    public void TestDXT5_CoordClamping()
    {
        var block = DXT5_BuildSolidBlock(255, DXT5_PackRGB565(255, 0, 0));
        var (dxt5, data) = DXT5_Make(block);
        try
        {
            Color corner = dxt5.GetPixel(0, 0);
            // Negative coordinates clamp to 0
            assertColorEquals("ClampNeg", dxt5.GetPixel(-1, -1), corner, DXT5Tol);
            assertColorEquals("ClampNegX", dxt5.GetPixel(-100, 0), corner, DXT5Tol);
            assertColorEquals("ClampNegY", dxt5.GetPixel(0, -50), corner, DXT5Tol);
            // Over-max coordinates clamp to max
            Color far = dxt5.GetPixel(3, 3);
            assertColorEquals("ClampOver", dxt5.GetPixel(100, 100), far, DXT5Tol);
            assertColorEquals("ClampOverX", dxt5.GetPixel(10, 3), far, DXT5Tol);
            assertColorEquals("ClampOverY", dxt5.GetPixel(3, 10), far, DXT5Tol);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 11. Multi-block: 8x8 with 4 distinct blocks (2x2 grid)
    // ================================================================

    [TestInfo("CPUTexture2D_DXT5_MultiBlock")]
    public void TestDXT5_MultiBlock()
    {
        var block00 = DXT5_BuildSolidBlock(255, DXT5_PackRGB565(255, 0, 0)); // red
        var block10 = DXT5_BuildSolidBlock(200, DXT5_PackRGB565(0, 255, 0)); // green
        var block01 = DXT5_BuildSolidBlock(150, DXT5_PackRGB565(0, 0, 255)); // blue
        var block11 = DXT5_BuildSolidBlock(100, DXT5_PackRGB565(255, 255, 0)); // yellow

        var allBlocks = new byte[64];
        Array.Copy(block00, 0, allBlocks, 0, 16);
        Array.Copy(block10, 0, allBlocks, 16, 16);
        Array.Copy(block01, 0, allBlocks, 32, 16);
        Array.Copy(block11, 0, allBlocks, 48, 16);

        var (dxt5, data) = DXT5_Make(allBlocks, 8, 8);
        try
        {
            // Block (0,0): pixels (0-3, 0-3)
            var (r0, g0, b0) = DXT5_UnpackRGB565(DXT5_PackRGB565(255, 0, 0));
            assertColorEquals("Block00", dxt5.GetPixel(1, 1), new Color(r0, g0, b0, 1f), DXT5Tol);

            // Block (1,0): pixels (4-7, 0-3)
            var (r1, g1, b1) = DXT5_UnpackRGB565(DXT5_PackRGB565(0, 255, 0));
            assertColorEquals(
                "Block10",
                dxt5.GetPixel(5, 1),
                new Color(r1, g1, b1, 200f / 255f),
                DXT5Tol
            );

            // Block (0,1): pixels (0-3, 4-7)
            var (r2, g2, b2) = DXT5_UnpackRGB565(DXT5_PackRGB565(0, 0, 255));
            assertColorEquals(
                "Block01",
                dxt5.GetPixel(1, 5),
                new Color(r2, g2, b2, 150f / 255f),
                DXT5Tol
            );

            // Block (1,1): pixels (4-7, 4-7)
            var (r3, g3, b3) = DXT5_UnpackRGB565(DXT5_PackRGB565(255, 255, 0));
            assertColorEquals(
                "Block11",
                dxt5.GetPixel(5, 5),
                new Color(r3, g3, b3, 100f / 255f),
                DXT5Tol
            );

            DXT5_CompareWithUnity("MultiBlock", allBlocks, 8, 8);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 12. Non-power-of-two dimensions (12x8 = 3x2 blocks)
    // ================================================================

    [TestInfo("CPUTexture2D_DXT5_NPOT")]
    public void TestDXT5_NonPowerOfTwo()
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
            byte alpha = (byte)(50 + (bx + by) * 40);
            int r = 50 + bx * 80;
            int g = 50 + by * 80;
            var block = DXT5_BuildSolidBlock(alpha, DXT5_PackRGB565(r, g, 128));
            Array.Copy(block, 0, allBlocks, blockIdx, 16);
        }

        var (dxt5, data) = DXT5_Make(allBlocks, w, h);
        try
        {
            for (int by = 0; by < blocksY; by++)
            for (int bx = 0; bx < blocksX; bx++)
            {
                int px = bx * 4 + 1;
                int py = by * 4 + 1;
                byte alpha = (byte)(50 + (bx + by) * 40);
                int r = 50 + bx * 80;
                int g = 50 + by * 80;
                var (expR, expG, expB) = DXT5_UnpackRGB565(DXT5_PackRGB565(r, g, 128));
                assertColorEquals(
                    $"NPOT_block({bx},{by})",
                    dxt5.GetPixel(px, py),
                    new Color(expR, expG, expB, alpha / 255f),
                    DXT5Tol
                );
            }

            DXT5_CompareWithUnity("NPOT", allBlocks, w, h);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 13. Mip levels: 8x8 with 2 mips
    // ================================================================

    [TestInfo("CPUTexture2D_DXT5_Mip")]
    public void TestDXT5_MipLevels()
    {
        // Mip 0: 8x8 = 2x2 = 4 blocks × 16 = 64 bytes
        // Mip 1: 4x4 = 1x1 = 1 block  × 16 = 16 bytes
        var greenBlock = DXT5_BuildSolidBlock(255, DXT5_PackRGB565(0, 255, 0));
        var blueBlock = DXT5_BuildSolidBlock(128, DXT5_PackRGB565(0, 0, 255));

        var bytes = new byte[80];
        for (int i = 0; i < 4; i++)
            Array.Copy(greenBlock, 0, bytes, i * 16, 16);
        Array.Copy(blueBlock, 0, bytes, 64, 16);

        var (dxt5, data) = DXT5_Make(bytes, 8, 8, 2);
        try
        {
            var (gr, gg, gb) = DXT5_UnpackRGB565(DXT5_PackRGB565(0, 255, 0));
            var (br, bg, bb) = DXT5_UnpackRGB565(DXT5_PackRGB565(0, 0, 255));
            var greenExpected = new Color(gr, gg, gb, 1f);
            var blueExpected = new Color(br, bg, bb, 128f / 255f);

            // Mip 0: all corners green
            assertColorEquals("Mip0(0,0)", dxt5.GetPixel(0, 0, 0), greenExpected, DXT5Tol);
            assertColorEquals("Mip0(7,7)", dxt5.GetPixel(7, 7, 0), greenExpected, DXT5Tol);

            // Mip 1: all corners blue
            assertColorEquals("Mip1(0,0)", dxt5.GetPixel(0, 0, 1), blueExpected, DXT5Tol);
            assertColorEquals("Mip1(3,3)", dxt5.GetPixel(3, 3, 1), blueExpected, DXT5Tol);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 14. Constructor validation: correct, too small, too large, multi-mip
    // ================================================================

    [TestInfo("CPUTexture2D_DXT5_Ctor")]
    public void TestDXT5_ConstructorValidation()
    {
        // Correct size: 4x4 × 1 mip = 16 bytes
        var goodData = new NativeArray<byte>(16, Allocator.Temp);
        try
        {
            new CPUTexture2D.DXT5(goodData, 4, 4, 1);
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
                new CPUTexture2D.DXT5(smallData, 4, 4, 1);
            }
            catch (Exception)
            {
                threw = true;
            }
            if (!threw)
                throw new Exception("DXT5.Ctor: expected exception for undersized data");
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
                new CPUTexture2D.DXT5(largeData, 4, 4, 1);
            }
            catch (Exception)
            {
                threw = true;
            }
            if (!threw)
                throw new Exception("DXT5.Ctor: expected exception for oversized data");
        }
        finally
        {
            largeData.Dispose();
        }

        // Multi-mip: 8x8 with 2 mips = 64 + 16 = 80 bytes
        var mipData = new NativeArray<byte>(80, Allocator.Temp);
        try
        {
            new CPUTexture2D.DXT5(mipData, 8, 8, 2);
        }
        finally
        {
            mipData.Dispose();
        }
    }

    // ================================================================
    // 15. GetPixel32 matches GetPixel
    // ================================================================

    [TestInfo("CPUTexture2D_DXT5_Pixel32")]
    public void TestDXT5_GetPixel32()
    {
        // Use varied alpha codes for non-trivial fractional values
        var alphaIdx = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0 };
        var block = DXT5_BuildBlock(
            240,
            30,
            alphaIdx,
            DXT5_PackRGB565(200, 100, 50),
            DXT5_PackRGB565(50, 200, 100),
            new byte[] { 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3 }
        );

        var (dxt5, data) = DXT5_Make(block);
        try
        {
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                Color pixel = dxt5.GetPixel(x, y);
                Color32 expected32 = pixel;
                Color32 actual32 = dxt5.GetPixel32(x, y);
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

    [TestInfo("CPUTexture2D_DXT5_RawData")]
    public void TestDXT5_RawTextureData()
    {
        var block = DXT5_BuildBlock(
            200,
            100,
            new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0 },
            DXT5_PackRGB565(255, 0, 0),
            DXT5_PackRGB565(0, 0, 255),
            new byte[] { 0, 1, 2, 3, 3, 2, 1, 0, 0, 0, 1, 1, 2, 2, 3, 3 }
        );

        var (dxt5, data) = DXT5_Make(block);
        try
        {
            var raw = dxt5.GetRawTextureData<byte>();

            if (raw.Length != block.Length)
                throw new Exception(
                    $"DXT5.RawData: length {raw.Length} != expected {block.Length}"
                );

            for (int i = 0; i < block.Length; i++)
                if (raw[i] != block[i])
                    throw new Exception($"DXT5.RawData[{i}]: {raw[i]} != expected {block[i]}");
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 17. Compressed gradient: full pipeline through Unity compressor
    // ================================================================

    [TestInfo("CPUTexture2D_DXT5_Gradient")]
    public void TestDXT5_CompressedGradient()
    {
        var tex = new Texture2D(16, 16, TextureFormat.RGBA32, false);
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
        {
            float u = x / 15f;
            float v = y / 15f;
            tex.SetPixel(x, y, new Color(u, v, 1f - u, 0.2f + 0.6f * v));
        }
        tex.Apply(false, false);
        tex.Compress(false);

        var rawData = tex.GetRawTextureData<byte>();
        var nativeCopy = new NativeArray<byte>(rawData.Length, Allocator.Temp);
        NativeArray<byte>.Copy(rawData, nativeCopy);

        try
        {
            var dxt5 = new CPUTexture2D.DXT5(nativeCopy, 16, 16, 1);

            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                Color expected = tex.GetPixel(x, y);
                Color actual = dxt5.GetPixel(x, y);
                assertColorEquals($"Gradient({x},{y})", actual, expected, DXT5LossyTol);
            }
        }
        finally
        {
            nativeCopy.Dispose();
            UnityEngine.Object.Destroy(tex);
        }
    }

    // ================================================================
    // 18. Full comparison against Texture2D.GetPixel for varied pixels
    // ================================================================

    [TestInfo("CPUTexture2D_DXT5_VsUnity")]
    public void TestDXT5_VsTexture2D()
    {
        var tex = new Texture2D(8, 8, TextureFormat.RGBA32, false);
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
            tex.SetPixel(x, y, new Color(x / 7f, y / 7f, (x + y) / 14f, 0.3f + 0.7f * x / 7f));
        tex.Apply(false, false);
        tex.Compress(false);

        var rawData = tex.GetRawTextureData<byte>();
        var nativeCopy = new NativeArray<byte>(rawData.Length, Allocator.Temp);
        NativeArray<byte>.Copy(rawData, nativeCopy);

        try
        {
            var dxt5 = new CPUTexture2D.DXT5(nativeCopy, 8, 8, 1);

            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                Color expected = tex.GetPixel(x, y);
                Color actual = dxt5.GetPixel(x, y);
                assertColorEquals($"VsTex2D({x},{y})", actual, expected, DXT5LossyTol);

                Color32 actual32 = dxt5.GetPixel32(x, y);
                assertColor32Equals(
                    $"VsTex2D.C32({x},{y})",
                    actual32,
                    (Color32)expected,
                    2 // allow +/-2 for lossy compression
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
