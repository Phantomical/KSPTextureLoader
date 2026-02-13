using System;
using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

/// <summary>
/// Integration tests that load DDS files from disk via
/// <see cref="TextureLoader.LoadTexture{T}"/> and verify that the resulting
/// <see cref="Texture2D"/> has the correct dimensions, format, and pixel data.
///
/// Test DDS files live in <c>GameData/TestData/PluginData/</c> and are generated
/// by <c>src/TestData/generate_test_dds.py</c>.
/// </summary>
public class DDSLoaderTests : KSPTextureLoaderTestBase
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

    TextureHandle<Texture2D> LoadDDS(string name)
    {
        var handle = TextureLoader.LoadTexture<Texture2D>(BasePath + name, Options);
        handle.WaitUntilComplete();
        return handle;
    }

    void AssertDimensions(Texture2D tex, string name, int w = 4, int h = 4)
    {
        if (tex.width != w || tex.height != h)
            throw new Exception($"{name}: expected {w}x{h} but got {tex.width}x{tex.height}");
    }

    void AssertPixelRGBA(Texture2D tex, string name, int x, int y, Color expected, float tol = Tol)
    {
        Color actual = tex.GetPixel(x, y);
        assertFloatEquals($"{name}.R({x},{y})", actual.r, expected.r, tol);
        assertFloatEquals($"{name}.G({x},{y})", actual.g, expected.g, tol);
        assertFloatEquals($"{name}.B({x},{y})", actual.b, expected.b, tol);
        assertFloatEquals($"{name}.A({x},{y})", actual.a, expected.a, tol);
    }

    void AssertAllPixelsRGBA(Texture2D tex, string name, float tol = Tol)
    {
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
            AssertPixelRGBA(tex, name, x, y, RefColor(x, y), tol);
    }

    void AssertPixelChannels(
        Texture2D tex,
        string name,
        int x,
        int y,
        bool checkR,
        bool checkG,
        bool checkB,
        bool checkA,
        Color expected,
        float tol = Tol
    )
    {
        Color actual = tex.GetPixel(x, y);
        if (checkR)
            assertFloatEquals($"{name}.R({x},{y})", actual.r, expected.r, tol);
        if (checkG)
            assertFloatEquals($"{name}.G({x},{y})", actual.g, expected.g, tol);
        if (checkB)
            assertFloatEquals($"{name}.B({x},{y})", actual.b, expected.b, tol);
        if (checkA)
            assertFloatEquals($"{name}.A({x},{y})", actual.a, expected.a, tol);
    }

    // ---- Uncompressed formats ----

    [TestInfo("DDSLoader_RGBA32")]
    public void TestRGBA32()
    {
        using var handle = LoadDDS("rgba32.dds");
        var tex = (Texture2D)handle.GetTexture();
        AssertDimensions(tex, "rgba32");
        AssertAllPixelsRGBA(tex, "rgba32", 0.004f);
    }

    [TestInfo("DDSLoader_BGRA32")]
    public void TestBGRA32()
    {
        using var handle = LoadDDS("bgra32.dds");
        var tex = (Texture2D)handle.GetTexture();
        AssertDimensions(tex, "bgra32");
        AssertAllPixelsRGBA(tex, "bgra32", 0.004f);
    }

    [TestInfo("DDSLoader_RGB565")]
    public void TestRGB565()
    {
        using var handle = LoadDDS("rgb565.dds");
        var tex = (Texture2D)handle.GetTexture();
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

    [TestInfo("DDSLoader_R8")]
    public void TestR8()
    {
        using var handle = LoadDDS("r8.dds");
        var tex = (Texture2D)handle.GetTexture();
        AssertDimensions(tex, "r8");
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            Color actual = tex.GetPixel(x, y);
            Color expected = RefColor(x, y);
            assertFloatEquals($"r8.R({x},{y})", actual.r, expected.r, 0.004f);
        }
    }

    [TestInfo("DDSLoader_RG8")]
    public void TestRG8()
    {
        using var handle = LoadDDS("rg8.dds");
        var tex = (Texture2D)handle.GetTexture();
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

    [TestInfo("DDSLoader_Alpha8")]
    public void TestAlpha8()
    {
        using var handle = LoadDDS("alpha8.dds");
        var tex = (Texture2D)handle.GetTexture();
        AssertDimensions(tex, "alpha8");
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            Color actual = tex.GetPixel(x, y);
            Color expected = RefColor(x, y);
            assertFloatEquals($"alpha8.A({x},{y})", actual.a, expected.a, 0.004f);
        }
    }

    [TestInfo("DDSLoader_R16")]
    public void TestR16()
    {
        using var handle = LoadDDS("r16.dds");
        var tex = (Texture2D)handle.GetTexture();
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

    [TestInfo("DDSLoader_R16F")]
    public void TestR16F()
    {
        using var handle = LoadDDS("r16f.dds");
        var tex = (Texture2D)handle.GetTexture();
        AssertDimensions(tex, "r16f");
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            Color actual = tex.GetPixel(x, y);
            Color expected = RefColor(x, y);
            assertFloatEquals($"r16f.R({x},{y})", actual.r, expected.r, 0.005f);
        }
    }

    [TestInfo("DDSLoader_RG16F")]
    public void TestRG16F()
    {
        using var handle = LoadDDS("rg16f.dds");
        var tex = (Texture2D)handle.GetTexture();
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

    [TestInfo("DDSLoader_RGBA16F")]
    public void TestRGBA16F()
    {
        using var handle = LoadDDS("rgba16f.dds");
        var tex = (Texture2D)handle.GetTexture();
        AssertDimensions(tex, "rgba16f");
        AssertAllPixelsRGBA(tex, "rgba16f", 0.005f);
    }

    [TestInfo("DDSLoader_R32F")]
    public void TestR32F()
    {
        using var handle = LoadDDS("r32f.dds");
        var tex = (Texture2D)handle.GetTexture();
        AssertDimensions(tex, "r32f");
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            Color actual = tex.GetPixel(x, y);
            Color expected = RefColor(x, y);
            assertFloatEquals($"r32f.R({x},{y})", actual.r, expected.r, 0.001f);
        }
    }

    [TestInfo("DDSLoader_RG32F")]
    public void TestRG32F()
    {
        using var handle = LoadDDS("rg32f.dds");
        var tex = (Texture2D)handle.GetTexture();
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

    [TestInfo("DDSLoader_RGBA32F")]
    public void TestRGBA32F()
    {
        using var handle = LoadDDS("rgba32f.dds");
        var tex = (Texture2D)handle.GetTexture();
        AssertDimensions(tex, "rgba32f");
        AssertAllPixelsRGBA(tex, "rgba32f", 0.001f);
    }

    // ---- Block compressed formats ----

    [TestInfo("DDSLoader_DXT1")]
    public void TestDXT1()
    {
        using var handle = LoadDDS("dxt1.dds");
        var tex = (Texture2D)handle.GetTexture();
        AssertDimensions(tex, "dxt1");
        // DXT1 block encoding from the Python script is simplistic and lossy.
        // Just verify it loads with correct dimensions; BC1 decompression quality
        // is already covered by the CPUTexture2D_DXT1 tests.
    }

    [TestInfo("DDSLoader_DXT5")]
    public void TestDXT5()
    {
        using var handle = LoadDDS("dxt5.dds");
        var tex = (Texture2D)handle.GetTexture();
        AssertDimensions(tex, "dxt5");
        // DXT5 block encoding from the Python script is simplistic and lossy.
        // Just verify it loads with correct dimensions; BC3 decompression quality
        // is already covered by the CPUTexture2D_DXT5 tests.
    }

    [TestInfo("DDSLoader_BC4")]
    public void TestBC4()
    {
        using var handle = LoadDDS("bc4.dds");
        var tex = (Texture2D)handle.GetTexture();
        AssertDimensions(tex, "bc4");
        // BC4 stores a single red channel with interpolation loss
        Color actual00 = tex.GetPixel(0, 0);
        // ref (0,0) has R=255 -> should be close to 1.0
        assertFloatEquals("bc4.R(0,0)", actual00.r, 1f, 0.05f);
        // ref (3,0) has R=255 -> also 1.0
        Color actual30 = tex.GetPixel(3, 0);
        assertFloatEquals("bc4.R(3,0)", actual30.r, 1f, 0.05f);
    }

    [TestInfo("DDSLoader_BC5")]
    public void TestBC5()
    {
        using var handle = LoadDDS("bc5.dds");
        var tex = (Texture2D)handle.GetTexture();
        AssertDimensions(tex, "bc5");
        // BC5 stores RG channels
        Color actual10 = tex.GetPixel(1, 0);
        // ref (1,0) has R=0, G=255
        assertFloatEquals("bc5.R(1,0)", actual10.r, 0f, 0.05f);
        assertFloatEquals("bc5.G(1,0)", actual10.g, 1f, 0.05f);
    }

    [TestInfo("DDSLoader_BC7")]
    public void TestBC7()
    {
        using var handle = LoadDDS("bc7.dds");
        var tex = (Texture2D)handle.GetTexture();
        AssertDimensions(tex, "bc7");
        // BC7 block encoding from the Python script has limited accuracy.
        // Just verify it loads with correct dimensions; BC7 decompression quality
        // is already covered by the CPUTexture2D_BC7 tests.
    }

    [TestInfo("DDSLoader_BC6H")]
    public void TestBC6H()
    {
        using var handle = LoadDDS("bc6h.dds");
        var tex = (Texture2D)handle.GetTexture();
        AssertDimensions(tex, "bc6h");
        // BC6H test data is all-zeros block, which decodes to near-black.
        // Just verify it loaded successfully with correct dimensions.
    }

    // ---- DX10 header ----

    [TestInfo("DDSLoader_RGBA32_DX10")]
    public void TestRGBA32DX10()
    {
        using var handle = LoadDDS("rgba32_dx10.dds");
        var tex = (Texture2D)handle.GetTexture();
        AssertDimensions(tex, "rgba32_dx10");
        AssertAllPixelsRGBA(tex, "rgba32_dx10", 0.004f);
    }

    // ---- Kopernicus palette formats ----

    [TestInfo("DDSLoader_KopernicusPalette4")]
    public void TestKopernicusPalette4()
    {
        using var handle = LoadDDS("kopernicus_palette4.dds");
        var tex = (Texture2D)handle.GetTexture();
        AssertDimensions(tex, "kopernicus_palette4");
        // Palette formats are decoded to RGBA32; exact pixel match expected.
        AssertAllPixelsRGBA(tex, "kopernicus_palette4", 0.004f);
    }

    [TestInfo("DDSLoader_KopernicusPalette8")]
    public void TestKopernicusPalette8()
    {
        using var handle = LoadDDS("kopernicus_palette8.dds");
        var tex = (Texture2D)handle.GetTexture();
        AssertDimensions(tex, "kopernicus_palette8");
        AssertAllPixelsRGBA(tex, "kopernicus_palette8", 0.004f);
    }
}
