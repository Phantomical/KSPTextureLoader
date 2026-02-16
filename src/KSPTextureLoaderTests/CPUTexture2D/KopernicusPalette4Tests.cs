using System;
using KSP.Testing;
using KSPTextureLoader;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace KSPTextureLoaderTests;

/// <summary>
/// Comprehensive tests for <see cref="CPUTexture2D.KopernicusPalette4"/>.
///
/// Format: 16-entry RGBA32 color palette (64 bytes) followed by 4bpp pixel indices.
/// Two pixels per byte: even pixel = low nibble, odd pixel = high nibble.
/// This matches the Kopernicus DDS palette format decoded by DecodeKopernicusPalette4bitJob.
/// </summary>
public class KopernicusPalette4Tests : CPUTexture2DTests
{
    const int PaletteEntries = 16;
    const int PaletteBytes = PaletteEntries * 4; // 64

    // ---- Helpers ----

    /// <summary>
    /// Build raw palette data: 64 bytes of palette + (width*height/2) bytes of indices.
    /// </summary>
    static byte[] BuildPalette4Data(Color32[] palette, byte[] indices, int width, int height)
    {
        if (palette.Length != PaletteEntries)
            throw new ArgumentException($"palette must have {PaletteEntries} entries");
        if (indices.Length != width * height)
            throw new ArgumentException("indices must have width*height entries");

        int indexBytes = width * height / 2;
        var data = new byte[PaletteBytes + indexBytes];

        // Write palette
        for (int i = 0; i < PaletteEntries; i++)
        {
            data[i * 4 + 0] = palette[i].r;
            data[i * 4 + 1] = palette[i].g;
            data[i * 4 + 2] = palette[i].b;
            data[i * 4 + 3] = palette[i].a;
        }

        // Pack indices: even pixel in low nibble, odd pixel in high nibble
        for (int i = 0; i < width * height; i += 2)
        {
            data[PaletteBytes + i / 2] = (byte)(indices[i] | (indices[i + 1] << 4));
        }

        return data;
    }

    /// <summary>
    /// Create a default 16-color palette with distinct, recognizable colors.
    /// </summary>
    static Color32[] MakeDefaultPalette()
    {
        var palette = new Color32[PaletteEntries];
        for (int i = 0; i < PaletteEntries; i++)
        {
            palette[i] = new Color32(
                (byte)(i * 17), // 0, 17, 34, ..., 255
                (byte)(255 - i * 17), // 255, 238, 221, ..., 0
                (byte)(i * 37 % 256),
                (byte)(200 + i * 3)
            );
        }
        return palette;
    }

    /// <summary>
    /// Reference decoder that matches the DecodeKopernicusPalette4bitJob behavior.
    /// </summary>
    static Color32 ReferenceGetPixel(byte[] rawData, int width, int x, int y)
    {
        int pixel = y * width + x;
        byte packed = rawData[PaletteBytes + pixel / 2];
        int paletteIndex = (pixel % 2 == 0) ? (packed & 0xF) : (packed >> 4);
        int offset = paletteIndex * 4;
        return new Color32(
            rawData[offset],
            rawData[offset + 1],
            rawData[offset + 2],
            rawData[offset + 3]
        );
    }

    // ================================================================
    // 1. Constructor validation: correct size
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal4_Ctor")]
    public void TestCtor()
    {
        // Correct size: 4x4 = 64 palette + 8 index bytes = 72
        using var data = new NativeArray<byte>(72, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette4(data, 4, 4);
        if (tex.Width != 4)
            throw new Exception($"Width: expected 4, got {tex.Width}");
        if (tex.Height != 4)
            throw new Exception($"Height: expected 4, got {tex.Height}");
        if (tex.MipCount != 1)
            throw new Exception($"MipCount: expected 1, got {tex.MipCount}");
        if (tex.Format != default)
            throw new Exception($"Format: expected default, got {tex.Format}");
    }

    // ================================================================
    // 2. Constructor rejects undersized data
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal4_CtorSmall")]
    public void TestCtorTooSmall()
    {
        using var data = new NativeArray<byte>(71, Allocator.Temp);
        bool threw = false;
        try
        {
            new CPUTexture2D.KopernicusPalette4(data, 4, 4);
        }
        catch (Exception)
        {
            threw = true;
        }
        if (!threw)
            throw new Exception("Expected exception for undersized data");
    }

    // ================================================================
    // 3. Constructor rejects oversized data
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal4_CtorLarge")]
    public void TestCtorTooLarge()
    {
        using var data = new NativeArray<byte>(73, Allocator.Temp);
        bool threw = false;
        try
        {
            new CPUTexture2D.KopernicusPalette4(data, 4, 4);
        }
        catch (Exception)
        {
            threw = true;
        }
        if (!threw)
            throw new Exception("Expected exception for oversized data");
    }

    // ================================================================
    // 4. Solid color: all pixels use same palette index
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal4_Solid")]
    public void TestSolid()
    {
        var palette = MakeDefaultPalette();
        int solidIndex = 5;
        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)solidIndex;

        var raw = BuildPalette4Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette4(data, 4, 4);

        var expected = palette[solidIndex];
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
            assertColor32Equals($"Solid({x},{y})", tex.GetPixel32(x, y), expected, 0);
    }

    // ================================================================
    // 5. All 16 palette entries
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal4_AllEntries")]
    public void TestAllPaletteEntries()
    {
        var palette = MakeDefaultPalette();
        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)i;

        var raw = BuildPalette4Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette4(data, 4, 4);

        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            int idx = y * 4 + x;
            assertColor32Equals($"Entry{idx}({x},{y})", tex.GetPixel32(x, y), palette[idx], 0);
        }
    }

    // ================================================================
    // 6. Nibble ordering: even pixel = low nibble, odd pixel = high nibble
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal4_NibbleOrder")]
    public void TestNibbleOrdering()
    {
        var palette = MakeDefaultPalette();
        // Pairs: (0,15), (1,14), (2,13), ...
        var indices = new byte[16];
        for (int i = 0; i < 16; i += 2)
        {
            indices[i] = (byte)(i / 2);
            indices[i + 1] = (byte)(15 - i / 2);
        }

        var raw = BuildPalette4Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette4(data, 4, 4);

        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            var expected = ReferenceGetPixel(raw, 4, x, y);
            assertColor32Equals($"Nibble({x},{y})", tex.GetPixel32(x, y), expected, 0);
        }
    }

    // ================================================================
    // 7. Reference decoder comparison: every pixel on a larger texture
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal4_Reference")]
    public void TestReferenceDecoder()
    {
        int w = 8,
            h = 8;
        var palette = MakeDefaultPalette();
        var indices = new byte[w * h];
        for (int i = 0; i < w * h; i++)
            indices[i] = (byte)((i * 7 + 3) % 16);

        var raw = BuildPalette4Data(palette, indices, w, h);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette4(data, w, h);

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var expected = ReferenceGetPixel(raw, w, x, y);
            assertColor32Equals($"Ref({x},{y})", tex.GetPixel32(x, y), expected, 0);
        }
    }

    // ================================================================
    // 8. Coordinate clamping
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal4_Clamp")]
    public void TestCoordClamping()
    {
        var palette = MakeDefaultPalette();
        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)i;

        var raw = BuildPalette4Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette4(data, 4, 4);

        // Negative coords clamp to 0
        assertColor32Equals("ClampNeg", tex.GetPixel32(-1, -1), tex.GetPixel32(0, 0), 0);
        assertColor32Equals("ClampNegX", tex.GetPixel32(-100, 2), tex.GetPixel32(0, 2), 0);
        assertColor32Equals("ClampNegY", tex.GetPixel32(2, -50), tex.GetPixel32(2, 0), 0);
        // Over-max clamp
        assertColor32Equals("ClampOver", tex.GetPixel32(100, 100), tex.GetPixel32(3, 3), 0);
        assertColor32Equals("ClampOverX", tex.GetPixel32(10, 2), tex.GetPixel32(3, 2), 0);
        assertColor32Equals("ClampOverY", tex.GetPixel32(2, 10), tex.GetPixel32(2, 3), 0);
    }

    // ================================================================
    // 9. GetPixel32 matches GetPixel via Color->Color32 conversion
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal4_Pixel32")]
    public void TestGetPixel32MatchesGetPixel()
    {
        var palette = MakeDefaultPalette();
        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)(i % 16);

        var raw = BuildPalette4Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette4(data, 4, 4);

        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            Color pixel = tex.GetPixel(x, y);
            Color32 expected32 = pixel;
            Color32 actual32 = tex.GetPixel32(x, y);
            assertColor32Equals($"Pixel32({x},{y})", actual32, expected32, 1);
        }
    }

    // ================================================================
    // 10. GetRawTextureData returns the original bytes
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal4_RawData")]
    public void TestRawTextureData()
    {
        var palette = MakeDefaultPalette();
        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)(i % 16);

        var raw = BuildPalette4Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette4(data, 4, 4);

        var result = tex.GetRawTextureData<byte>();
        if (result.Length != raw.Length)
            throw new Exception($"RawData length: expected {raw.Length}, got {result.Length}");
        for (int i = 0; i < raw.Length; i++)
            if (result[i] != raw[i])
                throw new Exception($"RawData[{i}]: expected {raw[i]}, got {result[i]}");
    }

    // ================================================================
    // 11. Palette with transparent colors (alpha < 255)
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal4_Alpha")]
    public void TestAlphaChannel()
    {
        var palette = new Color32[16];
        for (int i = 0; i < 16; i++)
            palette[i] = new Color32(255, 0, 0, (byte)(i * 17));

        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)i;

        var raw = BuildPalette4Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette4(data, 4, 4);

        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            int idx = y * 4 + x;
            Color32 actual = tex.GetPixel32(x, y);
            assertColor32Equals($"Alpha{idx}", actual, palette[idx], 0);
        }
    }

    // ================================================================
    // 12. First and last palette entries
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal4_Extremes")]
    public void TestExtremePaletteEntries()
    {
        var palette = new Color32[16];
        palette[0] = new Color32(0, 0, 0, 0);
        palette[15] = new Color32(255, 255, 255, 255);
        for (int i = 1; i < 15; i++)
            palette[i] = new Color32(128, 128, 128, 128);

        // Alternate between index 0 and 15
        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)(i % 2 == 0 ? 0 : 15);

        var raw = BuildPalette4Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette4(data, 4, 4);

        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            int pixel = y * 4 + x;
            var expected = pixel % 2 == 0 ? palette[0] : palette[15];
            assertColor32Equals($"Extreme({x},{y})", tex.GetPixel32(x, y), expected, 0);
        }
    }

    // ================================================================
    // 13. Non-square texture (8x4)
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal4_NonSquare")]
    public void TestNonSquare()
    {
        int w = 8,
            h = 4;
        var palette = MakeDefaultPalette();
        var indices = new byte[w * h];
        for (int i = 0; i < w * h; i++)
            indices[i] = (byte)(i % 16);

        var raw = BuildPalette4Data(palette, indices, w, h);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette4(data, w, h);

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var expected = ReferenceGetPixel(raw, w, x, y);
            assertColor32Equals($"NonSq({x},{y})", tex.GetPixel32(x, y), expected, 0);
        }
    }

    // ================================================================
    // 14. Large texture (16x16)
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal4_Large")]
    public void TestLargeTexture()
    {
        int w = 16,
            h = 16;
        var palette = MakeDefaultPalette();
        var indices = new byte[w * h];
        for (int i = 0; i < w * h; i++)
            indices[i] = (byte)((i * 3 + 7) % 16);

        var raw = BuildPalette4Data(palette, indices, w, h);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette4(data, w, h);

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var expected = ReferenceGetPixel(raw, w, x, y);
            assertColor32Equals($"Large({x},{y})", tex.GetPixel32(x, y), expected, 0);
        }
    }

    // ================================================================
    // 15. Duplicate palette entries: different indices, same color
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal4_DupPalette")]
    public void TestDuplicatePaletteEntries()
    {
        var palette = new Color32[16];
        var color = new Color32(42, 87, 123, 200);
        for (int i = 0; i < 16; i++)
            palette[i] = color;

        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)i;

        var raw = BuildPalette4Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette4(data, 4, 4);

        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
            assertColor32Equals($"Dup({x},{y})", tex.GetPixel32(x, y), color, 0);
    }

    // ================================================================
    // 16. Checkerboard pattern: verify spatial addressing
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal4_Checker")]
    public void TestCheckerboard()
    {
        var palette = new Color32[16];
        palette[0] = new Color32(0, 0, 0, 255);
        palette[1] = new Color32(255, 255, 255, 255);
        for (int i = 2; i < 16; i++)
            palette[i] = new Color32(128, 128, 128, 255);

        var indices = new byte[16];
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
            indices[y * 4 + x] = (byte)((x + y) % 2);

        var raw = BuildPalette4Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette4(data, 4, 4);

        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            var expected = palette[(x + y) % 2];
            assertColor32Equals($"Check({x},{y})", tex.GetPixel32(x, y), expected, 0);
        }
    }

    // ================================================================
    // 17. GetPixelBilinear at pixel centers returns exact values
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal4_Bilinear")]
    public void TestBilinearAtCenters()
    {
        var palette = MakeDefaultPalette();
        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)i;

        var raw = BuildPalette4Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette4(data, 4, 4);

        // At pixel centers, bilinear should return the exact pixel value
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            float u = (x + 0.5f) / 4f;
            float v = (y + 0.5f) / 4f;
            Color bilinear = tex.GetPixelBilinear(u, v);
            Color point = tex.GetPixel(x, y);
            assertColorEquals($"Bilinear({x},{y})", bilinear, point, 0.005f);
        }
    }

    // ================================================================
    // 18. Constructor validation for non-square sizes
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal4_CtorSizes")]
    public void TestCtorVariousSizes()
    {
        // 8x4 = 64 palette + 16 index bytes = 80
        using var data1 = new NativeArray<byte>(80, Allocator.Temp);
        new CPUTexture2D.KopernicusPalette4(data1, 8, 4);

        // 2x2 = 64 palette + 2 index bytes = 66
        using var data2 = new NativeArray<byte>(66, Allocator.Temp);
        new CPUTexture2D.KopernicusPalette4(data2, 2, 2);

        // 16x16 = 64 palette + 128 index bytes = 192
        using var data3 = new NativeArray<byte>(192, Allocator.Temp);
        new CPUTexture2D.KopernicusPalette4(data3, 16, 16);
    }
}
