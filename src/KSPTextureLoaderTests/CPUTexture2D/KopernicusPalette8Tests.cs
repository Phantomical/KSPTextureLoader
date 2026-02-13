using System;
using KSP.Testing;
using KSPTextureLoader;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoaderTests;

/// <summary>
/// Comprehensive tests for <see cref="CPUTexture2D.KopernicusPalette8"/>.
///
/// Format: 256-entry RGBA32 color palette (1024 bytes) followed by 8bpp pixel indices.
/// One pixel per byte. This matches the Kopernicus DDS palette format decoded by
/// DecodeKopernicusPalette8bitJob.
/// </summary>
public class KopernicusPalette8Tests : CPUTexture2DTests
{
    const int PaletteEntries = 256;
    const int PaletteBytes = PaletteEntries * 4; // 1024

    // ---- Helpers ----

    /// <summary>
    /// Build raw palette data: 1024 bytes of palette + (width*height) bytes of indices.
    /// </summary>
    static byte[] BuildPalette8Data(Color32[] palette, byte[] indices, int width, int height)
    {
        if (palette.Length != PaletteEntries)
            throw new ArgumentException($"palette must have {PaletteEntries} entries");
        if (indices.Length != width * height)
            throw new ArgumentException("indices must have width*height entries");

        var data = new byte[PaletteBytes + width * height];

        // Write palette
        for (int i = 0; i < PaletteEntries; i++)
        {
            data[i * 4 + 0] = palette[i].r;
            data[i * 4 + 1] = palette[i].g;
            data[i * 4 + 2] = palette[i].b;
            data[i * 4 + 3] = palette[i].a;
        }

        // Write indices (1 byte per pixel)
        Array.Copy(indices, 0, data, PaletteBytes, indices.Length);

        return data;
    }

    /// <summary>
    /// Create a 256-color palette with distinct values for each entry.
    /// </summary>
    static Color32[] MakeDefaultPalette()
    {
        var palette = new Color32[PaletteEntries];
        for (int i = 0; i < PaletteEntries; i++)
        {
            palette[i] = new Color32(
                (byte)i,
                (byte)(255 - i),
                (byte)((i * 37) % 256),
                (byte)((i * 59 + 100) % 256)
            );
        }
        return palette;
    }

    // ================================================================
    // 1. Constructor validation: correct size
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal8_Ctor")]
    public void TestCtor()
    {
        // 4x4 = 1024 palette + 16 index bytes = 1040
        using var data = new NativeArray<byte>(1040, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette8(data, 4, 4);
        if (tex.Width != 4)
            throw new Exception($"Width: expected 4, got {tex.Width}");
        if (tex.Height != 4)
            throw new Exception($"Height: expected 4, got {tex.Height}");
        if (tex.MipCount != 1)
            throw new Exception($"MipCount: expected 1, got {tex.MipCount}");
        if (tex.Format != TextureFormat.RGBA32)
            throw new Exception($"Format: expected RGBA32, got {tex.Format}");
    }

    // ================================================================
    // 2. Constructor rejects undersized data
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal8_CtorSmall")]
    public void TestCtorTooSmall()
    {
        using var data = new NativeArray<byte>(1039, Allocator.Temp);
        bool threw = false;
        try
        {
            new CPUTexture2D.KopernicusPalette8(data, 4, 4);
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

    [TestInfo("CPUTexture2D_KopPal8_CtorLarge")]
    public void TestCtorTooLarge()
    {
        using var data = new NativeArray<byte>(1041, Allocator.Temp);
        bool threw = false;
        try
        {
            new CPUTexture2D.KopernicusPalette8(data, 4, 4);
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

    [TestInfo("CPUTexture2D_KopPal8_Solid")]
    public void TestSolid()
    {
        var palette = MakeDefaultPalette();
        int solidIndex = 42;
        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)solidIndex;

        var raw = BuildPalette8Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette8(data, 4, 4);

        var expected = palette[solidIndex];
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
            assertColor32Equals($"Solid({x},{y})", tex.GetPixel32(x, y), expected, 0);
    }

    // ================================================================
    // 5. All 256 palette entries (16x16 texture)
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal8_AllEntries")]
    public void TestAllPaletteEntries()
    {
        int w = 16,
            h = 16;
        var palette = MakeDefaultPalette();
        var indices = new byte[w * h];
        for (int i = 0; i < 256; i++)
            indices[i] = (byte)i;

        var raw = BuildPalette8Data(palette, indices, w, h);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette8(data, w, h);

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int idx = y * w + x;
            assertColor32Equals($"Entry{idx}({x},{y})", tex.GetPixel32(x, y), palette[idx], 0);
        }
    }

    // ================================================================
    // 6. Pixel addressing: verify x,y maps correctly
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal8_Address")]
    public void TestPixelAddressing()
    {
        var palette = MakeDefaultPalette();
        var indices = new byte[16];
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
            indices[y * 4 + x] = (byte)(y * 4 + x + 100);

        var raw = BuildPalette8Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette8(data, 4, 4);

        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            int expected = y * 4 + x + 100;
            assertColor32Equals($"Addr({x},{y})", tex.GetPixel32(x, y), palette[expected], 0);
        }
    }

    // ================================================================
    // 7. Coordinate clamping
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal8_Clamp")]
    public void TestCoordClamping()
    {
        var palette = MakeDefaultPalette();
        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)(i * 10);

        var raw = BuildPalette8Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette8(data, 4, 4);

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
    // 8. GetPixel32 matches GetPixel via Color->Color32 conversion
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal8_Pixel32")]
    public void TestGetPixel32MatchesGetPixel()
    {
        var palette = MakeDefaultPalette();
        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)(i * 15);

        var raw = BuildPalette8Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette8(data, 4, 4);

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
    // 9. GetRawTextureData returns the original bytes
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal8_RawData")]
    public void TestRawTextureData()
    {
        var palette = MakeDefaultPalette();
        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)(i * 15);

        var raw = BuildPalette8Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette8(data, 4, 4);

        var result = tex.GetRawTextureData<byte>();
        if (result.Length != raw.Length)
            throw new Exception($"RawData length: expected {raw.Length}, got {result.Length}");
        for (int i = 0; i < raw.Length; i++)
            if (result[i] != raw[i])
                throw new Exception($"RawData[{i}]: expected {raw[i]}, got {result[i]}");
    }

    // ================================================================
    // 10. Palette with transparent colors (alpha variations)
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal8_Alpha")]
    public void TestAlphaChannel()
    {
        var palette = new Color32[256];
        for (int i = 0; i < 256; i++)
            palette[i] = new Color32(255, 0, 0, (byte)i);

        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)(i * 17); // 0, 17, 34, ..., 255

        var raw = BuildPalette8Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette8(data, 4, 4);

        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            int idx = y * 4 + x;
            int paletteIdx = idx * 17;
            Color32 actual = tex.GetPixel32(x, y);
            assertColor32Equals($"Alpha({x},{y})", actual, palette[paletteIdx], 0);
        }
    }

    // ================================================================
    // 11. First and last palette entries (index 0 and 255)
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal8_Extremes")]
    public void TestExtremePaletteEntries()
    {
        var palette = new Color32[256];
        palette[0] = new Color32(0, 0, 0, 0);
        palette[255] = new Color32(255, 255, 255, 255);
        for (int i = 1; i < 255; i++)
            palette[i] = new Color32(128, 128, 128, 128);

        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)(i % 2 == 0 ? 0 : 255);

        var raw = BuildPalette8Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette8(data, 4, 4);

        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            int pixel = y * 4 + x;
            var expected = pixel % 2 == 0 ? palette[0] : palette[255];
            assertColor32Equals($"Extreme({x},{y})", tex.GetPixel32(x, y), expected, 0);
        }
    }

    // ================================================================
    // 12. Non-square texture (8x4)
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal8_NonSquare")]
    public void TestNonSquare()
    {
        int w = 8,
            h = 4;
        var palette = MakeDefaultPalette();
        var indices = new byte[w * h];
        for (int i = 0; i < w * h; i++)
            indices[i] = (byte)(i * 7 % 256);

        var raw = BuildPalette8Data(palette, indices, w, h);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette8(data, w, h);

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int idx = indices[y * w + x];
            assertColor32Equals($"NonSq({x},{y})", tex.GetPixel32(x, y), palette[idx], 0);
        }
    }

    // ================================================================
    // 13. Large texture (32x32)
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal8_Large")]
    public void TestLargeTexture()
    {
        int w = 32,
            h = 32;
        var palette = MakeDefaultPalette();
        var indices = new byte[w * h];
        for (int i = 0; i < w * h; i++)
            indices[i] = (byte)((i * 3 + 7) % 256);

        var raw = BuildPalette8Data(palette, indices, w, h);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette8(data, w, h);

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int idx = indices[y * w + x];
            assertColor32Equals($"Large({x},{y})", tex.GetPixel32(x, y), palette[idx], 0);
        }
    }

    // ================================================================
    // 14. Duplicate palette entries: different indices, same color
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal8_DupPalette")]
    public void TestDuplicatePaletteEntries()
    {
        var palette = new Color32[256];
        var color = new Color32(42, 87, 123, 200);
        for (int i = 0; i < 256; i++)
            palette[i] = color;

        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)(i * 17);

        var raw = BuildPalette8Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette8(data, 4, 4);

        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
            assertColor32Equals($"Dup({x},{y})", tex.GetPixel32(x, y), color, 0);
    }

    // ================================================================
    // 15. Checkerboard pattern: verify spatial addressing
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal8_Checker")]
    public void TestCheckerboard()
    {
        var palette = new Color32[256];
        palette[0] = new Color32(0, 0, 0, 255);
        palette[1] = new Color32(255, 255, 255, 255);
        for (int i = 2; i < 256; i++)
            palette[i] = new Color32(128, 128, 128, 255);

        var indices = new byte[16];
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
            indices[y * 4 + x] = (byte)((x + y) % 2);

        var raw = BuildPalette8Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette8(data, 4, 4);

        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            var expected = palette[(x + y) % 2];
            assertColor32Equals($"Check({x},{y})", tex.GetPixel32(x, y), expected, 0);
        }
    }

    // ================================================================
    // 16. GetPixelBilinear at pixel centers returns exact values
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal8_Bilinear")]
    public void TestBilinearAtCenters()
    {
        var palette = MakeDefaultPalette();
        var indices = new byte[16];
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)(i * 15);

        var raw = BuildPalette8Data(palette, indices, 4, 4);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette8(data, 4, 4);

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
    // 17. Constructor validation for various sizes
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal8_CtorSizes")]
    public void TestCtorVariousSizes()
    {
        // 8x4 = 1024 + 32 = 1056
        using var data1 = new NativeArray<byte>(1056, Allocator.Temp);
        new CPUTexture2D.KopernicusPalette8(data1, 8, 4);

        // 1x1 = 1024 + 1 = 1025
        using var data2 = new NativeArray<byte>(1025, Allocator.Temp);
        new CPUTexture2D.KopernicusPalette8(data2, 1, 1);

        // 16x16 = 1024 + 256 = 1280
        using var data3 = new NativeArray<byte>(1280, Allocator.Temp);
        new CPUTexture2D.KopernicusPalette8(data3, 16, 16);
    }

    // ================================================================
    // 18. Sequential index values across rows
    // ================================================================

    [TestInfo("CPUTexture2D_KopPal8_Sequential")]
    public void TestSequentialIndices()
    {
        int w = 8,
            h = 8;
        var palette = MakeDefaultPalette();
        var indices = new byte[w * h];
        for (int i = 0; i < w * h; i++)
            indices[i] = (byte)i;

        var raw = BuildPalette8Data(palette, indices, w, h);
        using var data = new NativeArray<byte>(raw, Allocator.Temp);
        var tex = new CPUTexture2D.KopernicusPalette8(data, w, h);

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int idx = y * w + x;
            assertColor32Equals($"Seq({x},{y})", tex.GetPixel32(x, y), palette[idx], 0);
        }
    }
}
