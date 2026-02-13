using System;
using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

/// <summary>
/// Integration tests that load DDS files from disk via
/// <see cref="TextureLoader.LoadCPUTexture(string, TextureLoadOptions)"/> and verify that the
/// resulting <see cref="CPUTexture2D"/> has the correct dimensions and pixel data.
///
/// These mirror <see cref="DDSLoaderTests"/> but exercise the CPU texture path
/// (memory-mapped DDS → CPUTexture2D format structs) instead of the GPU texture path.
/// </summary>
public class CPUTexture2DDDSLoaderTests : KSPTextureLoaderTestBase
{
    const string BasePath = "KSPTextureLoaderTests/PluginData/";
    const float Tol = 0.02f; // tolerant of block-compression and format-conversion loss

    static readonly TextureLoadOptions Options = new()
    {
        Unreadable = false,
        Hint = TextureLoadHint.Synchronous,
    };

    // Reference pixels from generate_test_dds.py (RGBA 0-255).
    // Row-major: [row0..row3], 4 pixels per row.
    static readonly Color32[] RefPixels =
    [
        // Row 0
        new(255, 0, 0, 255),
        new(0, 255, 0, 255),
        new(0, 0, 255, 255),
        new(255, 255, 0, 255),
        // Row 1
        new(255, 0, 255, 255),
        new(0, 255, 255, 255),
        new(128, 128, 128, 255),
        new(255, 255, 255, 255),
        // Row 2
        new(64, 0, 0, 255),
        new(0, 64, 0, 255),
        new(0, 0, 64, 255),
        new(64, 64, 0, 255),
        // Row 3
        new(0, 0, 0, 255),
        new(32, 32, 32, 255),
        new(192, 192, 192, 255),
        new(128, 0, 128, 255),
    ];

    static Color RefColor(int x, int y) => RefPixels[y * 4 + x];

    CPUTextureHandle LoadCPUDDS(string name)
    {
        var handle = TextureLoader.LoadCPUTexture(BasePath + name, Options);
        handle.WaitUntilComplete();
        return handle;
    }

    void AssertDimensions(CPUTexture2D tex, string name, int w = 4, int h = 4)
    {
        if (tex.Width != w || tex.Height != h)
            throw new Exception($"{name}: expected {w}x{h} but got {tex.Width}x{tex.Height}");
    }

    void AssertPixelRGBA(
        CPUTexture2D tex,
        string name,
        int x,
        int y,
        Color expected,
        float tol = Tol
    )
    {
        Color actual = tex.GetPixel(x, y);
        assertFloatEquals($"{name}.R({x},{y})", actual.r, expected.r, tol);
        assertFloatEquals($"{name}.G({x},{y})", actual.g, expected.g, tol);
        assertFloatEquals($"{name}.B({x},{y})", actual.b, expected.b, tol);
        assertFloatEquals($"{name}.A({x},{y})", actual.a, expected.a, tol);
    }

    void AssertAllPixelsRGBA(CPUTexture2D tex, string name, float tol = Tol)
    {
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
            AssertPixelRGBA(tex, name, x, y, RefColor(x, y), tol);
    }

    // ---- Uncompressed formats ----

    [TestInfo("CPUTexture2D_DDSLoader_RGBA32")]
    public void TestRGBA32()
    {
        using var handle = LoadCPUDDS("rgba32.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "rgba32");
        AssertAllPixelsRGBA(tex, "rgba32", 0.004f);
    }

    [TestInfo("CPUTexture2D_DDSLoader_BGRA32")]
    public void TestBGRA32()
    {
        using var handle = LoadCPUDDS("bgra32.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "bgra32");
        AssertAllPixelsRGBA(tex, "bgra32", 0.004f);
    }

    [TestInfo("CPUTexture2D_DDSLoader_RGB565")]
    public void TestRGB565()
    {
        using var handle = LoadCPUDDS("rgb565.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "rgb565");
        // RGB565 has quantization loss; alpha is always 1
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            Color expected = RefColor(x, y);
            expected.a = 1f;
            AssertPixelRGBA(tex, "rgb565", x, y, expected, 0.04f);
        }
    }

    [TestInfo("CPUTexture2D_DDSLoader_R8")]
    public void TestR8()
    {
        using var handle = LoadCPUDDS("r8.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "r8");
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            Color actual = tex.GetPixel(x, y);
            Color expected = RefColor(x, y);
            assertFloatEquals($"r8.R({x},{y})", actual.r, expected.r, 0.004f);
        }
    }

    [TestInfo("CPUTexture2D_DDSLoader_RG8")]
    public void TestRG8()
    {
        using var handle = LoadCPUDDS("rg8.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "rg8");
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            Color actual = tex.GetPixel(x, y);
            Color expected = RefColor(x, y);
            assertFloatEquals($"rg8.R({x},{y})", actual.r, expected.r, 0.004f);
            assertFloatEquals($"rg8.G({x},{y})", actual.g, expected.g, 0.004f);
        }
    }

    [TestInfo("CPUTexture2D_DDSLoader_Alpha8")]
    public void TestAlpha8()
    {
        using var handle = LoadCPUDDS("alpha8.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "alpha8");
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            Color actual = tex.GetPixel(x, y);
            Color expected = RefColor(x, y);
            assertFloatEquals($"alpha8.A({x},{y})", actual.a, expected.a, 0.004f);
        }
    }

    [TestInfo("CPUTexture2D_DDSLoader_R16")]
    public void TestR16()
    {
        using var handle = LoadCPUDDS("r16.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "r16");
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            Color actual = tex.GetPixel(x, y);
            Color expected = RefColor(x, y);
            assertFloatEquals($"r16.R({x},{y})", actual.r, expected.r, 0.004f);
        }
    }

    // ---- Float formats ----

    [TestInfo("CPUTexture2D_DDSLoader_R16F")]
    public void TestR16F()
    {
        using var handle = LoadCPUDDS("r16f.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "r16f");
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            Color actual = tex.GetPixel(x, y);
            Color expected = RefColor(x, y);
            assertFloatEquals($"r16f.R({x},{y})", actual.r, expected.r, 0.005f);
        }
    }

    [TestInfo("CPUTexture2D_DDSLoader_RG16F")]
    public void TestRG16F()
    {
        using var handle = LoadCPUDDS("rg16f.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "rg16f");
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            Color actual = tex.GetPixel(x, y);
            Color expected = RefColor(x, y);
            assertFloatEquals($"rg16f.R({x},{y})", actual.r, expected.r, 0.005f);
            assertFloatEquals($"rg16f.G({x},{y})", actual.g, expected.g, 0.005f);
        }
    }

    [TestInfo("CPUTexture2D_DDSLoader_RGBA16F")]
    public void TestRGBA16F()
    {
        using var handle = LoadCPUDDS("rgba16f.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "rgba16f");
        AssertAllPixelsRGBA(tex, "rgba16f", 0.005f);
    }

    [TestInfo("CPUTexture2D_DDSLoader_R32F")]
    public void TestR32F()
    {
        using var handle = LoadCPUDDS("r32f.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "r32f");
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            Color actual = tex.GetPixel(x, y);
            Color expected = RefColor(x, y);
            assertFloatEquals($"r32f.R({x},{y})", actual.r, expected.r, 0.001f);
        }
    }

    [TestInfo("CPUTexture2D_DDSLoader_RG32F")]
    public void TestRG32F()
    {
        using var handle = LoadCPUDDS("rg32f.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "rg32f");
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            Color actual = tex.GetPixel(x, y);
            Color expected = RefColor(x, y);
            assertFloatEquals($"rg32f.R({x},{y})", actual.r, expected.r, 0.001f);
            assertFloatEquals($"rg32f.G({x},{y})", actual.g, expected.g, 0.001f);
        }
    }

    [TestInfo("CPUTexture2D_DDSLoader_RGBA32F")]
    public void TestRGBA32F()
    {
        using var handle = LoadCPUDDS("rgba32f.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "rgba32f");
        AssertAllPixelsRGBA(tex, "rgba32f", 0.001f);
    }

    // ---- Block compressed formats ----

    [TestInfo("CPUTexture2D_DDSLoader_DXT1")]
    public void TestDXT1()
    {
        using var handle = LoadCPUDDS("dxt1.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "dxt1");
    }

    [TestInfo("CPUTexture2D_DDSLoader_DXT5")]
    public void TestDXT5()
    {
        using var handle = LoadCPUDDS("dxt5.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "dxt5");
    }

    [TestInfo("CPUTexture2D_DDSLoader_BC4")]
    public void TestBC4()
    {
        using var handle = LoadCPUDDS("bc4.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "bc4");
        // BC4 stores a single red channel with interpolation loss
        Color actual00 = tex.GetPixel(0, 0);
        // ref (0,0) has R=255 -> should be close to 1.0
        assertFloatEquals("bc4.R(0,0)", actual00.r, 1f, 0.05f);
        // ref (3,0) has R=255 -> also 1.0
        Color actual30 = tex.GetPixel(3, 0);
        assertFloatEquals("bc4.R(3,0)", actual30.r, 1f, 0.05f);
    }

    [TestInfo("CPUTexture2D_DDSLoader_BC5")]
    public void TestBC5()
    {
        using var handle = LoadCPUDDS("bc5.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "bc5");
        // BC5 stores RG channels
        Color actual10 = tex.GetPixel(1, 0);
        // ref (1,0) has R=0, G=255
        assertFloatEquals("bc5.R(1,0)", actual10.r, 0f, 0.05f);
        assertFloatEquals("bc5.G(1,0)", actual10.g, 1f, 0.05f);
    }

    [TestInfo("CPUTexture2D_DDSLoader_BC7")]
    public void TestBC7()
    {
        using var handle = LoadCPUDDS("bc7.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "bc7");
    }

    [TestInfo("CPUTexture2D_DDSLoader_BC6H")]
    public void TestBC6H()
    {
        using var handle = LoadCPUDDS("bc6h.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "bc6h");
    }

    // ---- DX10 header ----

    [TestInfo("CPUTexture2D_DDSLoader_RGBA32_DX10")]
    public void TestRGBA32DX10()
    {
        using var handle = LoadCPUDDS("rgba32_dx10.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "rgba32_dx10");
        AssertAllPixelsRGBA(tex, "rgba32_dx10", 0.004f);
    }

    // ---- Kopernicus palette formats ----

    [TestInfo("CPUTexture2D_DDSLoader_KopernicusPalette4")]
    public void TestKopernicusPalette4()
    {
        using var handle = LoadCPUDDS("kopernicus_palette4.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "kopernicus_palette4");
        AssertAllPixelsRGBA(tex, "kopernicus_palette4", 0.004f);
    }

    [TestInfo("CPUTexture2D_DDSLoader_KopernicusPalette8")]
    public void TestKopernicusPalette8()
    {
        using var handle = LoadCPUDDS("kopernicus_palette8.dds");
        var tex = handle.GetTexture();
        AssertDimensions(tex, "kopernicus_palette8");
        AssertAllPixelsRGBA(tex, "kopernicus_palette8", 0.004f);
    }
}
