using System;
using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace KSPTextureLoaderTests;

/// <summary>
/// Block compressed textures whose dimensions are not a multiple of 4.
///
/// Unity 6.7 can't create these, so KSPTextureLoader decompresses them on the
/// CPU. Each <c>*_6x7.dds</c> file is a 6x7 texture with 3 mips where every
/// block is a copy of the single block in the matching 4x4 file, so we compare
/// against Unity's own decode of the 4x4 file.
/// </summary>
public class OddSizeBlockCompressedTests : KSPTextureLoaderTestBase
{
    const string BasePath = "KSPTextureLoaderTests/PluginData/";

    static readonly TextureLoadOptions Options = new()
    {
        Unreadable = false,
        Hint = TextureLoadHint.Synchronous,
    };

    static TextureHandle<Texture2D> Load(string name, TextureLoadOptions options)
    {
        var handle = TextureLoader.LoadTexture<Texture2D>(BasePath + name, options);
        handle.WaitUntilComplete();
        return handle;
    }

    /// <summary>
    /// Compare the 6x7 file against Unity's decode of the matching 4x4 file.
    /// Only the first <paramref name="channels"/> channels are compared, since
    /// Unity's GetPixel fills in the missing channels of BC4/BC5 differently
    /// from R8/RG16.
    /// </summary>
    void AssertMatchesReference(
        string name,
        GraphicsFormat expectedFormat,
        int channels = 4,
        float tol = 0.006f,
        bool relative = false
    )
    {
        using var refHandle = Load(name + ".dds", Options);
        var reference = refHandle.GetTexture();
        AssertMatches(
            name,
            expectedFormat,
            (x, y) => reference.GetPixel(x, y),
            channels,
            tol,
            relative
        );
    }

    /// <summary>
    /// Compare the 6x7 file against <paramref name="expected"/>, which gives
    /// the pixels of the single 4x4 block that the file is tiled from.
    /// </summary>
    void AssertMatches(
        string name,
        GraphicsFormat expectedFormat,
        Func<int, int, Color> expected,
        int channels = 4,
        float tol = 0.006f,
        bool relative = false
    )
    {
        using var oddHandle = Load(name + "_6x7.dds", Options);
        var odd = oddHandle.GetTexture();

        if (odd.width != 6 || odd.height != 7)
            throw new Exception($"{name}: expected 6x7 but got {odd.width}x{odd.height}");
        if (odd.mipmapCount != 3)
            throw new Exception($"{name}: expected 3 mips but got {odd.mipmapCount}");

#if UNITY_6_7
        if (odd.graphicsFormat != expectedFormat)
            throw new Exception(
                $"{name}: expected format {expectedFormat} but got {odd.graphicsFormat}"
            );
#endif

        void Check(int x, int y, int mip, int rx, int ry)
        {
            Color actual = odd.GetPixel(x, y, mip);
            Color exp = expected(rx, ry);

            for (int c = 0; c < channels; ++c)
            {
                float t = relative ? tol * Math.Max(1f, Math.Abs(exp[c])) : tol;
                if (Math.Abs(actual[c] - exp[c]) > t)
                    throw new Exception(
                        $"TEST {name}[mip {mip}]({x},{y}): FAIL! "
                            + $"Color({actual.r:F4},{actual.g:F4},{actual.b:F4},{actual.a:F4}) != "
                            + $"({exp.r:F4},{exp.g:F4},{exp.b:F4},{exp.a:F4}) (tol={tol})"
                    );
            }
        }

        // Mip 0 is 2x2 blocks, each a copy of the reference block.
        for (int y = 0; y < 7; ++y)
        for (int x = 0; x < 6; ++x)
            Check(x, y, 0, x % 4, y % 4);

        // Mip 1 is 3x3, a single block.
        for (int y = 0; y < 3; ++y)
        for (int x = 0; x < 3; ++x)
            Check(x, y, 1, x, y);

        // Mip 2 is 1x1.
        Check(0, 0, 2, 0, 0);
    }

    // One step of an 8-bit SNorm value, plus a little rounding slack.
    const float SNormTol = 1f / 127f + 0.001f;

    [TestInfo("OddSize_DXT1")]
    public void TestDXT1() => AssertMatchesReference("dxt1", GraphicsFormat.R8G8B8A8_UNorm);

    [TestInfo("OddSize_DXT3")]
    public void TestDXT3() => AssertMatchesReference("dxt3", GraphicsFormat.R8G8B8A8_UNorm);

    [TestInfo("OddSize_DXT5")]
    public void TestDXT5() => AssertMatchesReference("dxt5", GraphicsFormat.R8G8B8A8_UNorm);

    [TestInfo("OddSize_BC4")]
    public void TestBC4() => AssertMatchesReference("bc4", GraphicsFormat.R8_UNorm, channels: 1);

    [TestInfo("OddSize_BC4_SNorm")]
    public void TestBC4SNorm() =>
        AssertMatchesReference("bc4_snorm", GraphicsFormat.R8_SNorm, channels: 1, tol: SNormTol);

    [TestInfo("OddSize_BC5")]
    public void TestBC5() => AssertMatchesReference("bc5", GraphicsFormat.R8G8_UNorm, channels: 2);

    [TestInfo("OddSize_BC5_SNorm")]
    public void TestBC5SNorm() =>
        AssertMatchesReference("bc5_snorm", GraphicsFormat.R8G8_SNorm, channels: 2, tol: SNormTol);

    [TestInfo("OddSize_BC6H")]
    public void TestBC6H() =>
        AssertMatchesReference(
            "bc6h_mode11",
            GraphicsFormat.R16G16B16A16_SFloat,
            tol: 0.002f,
            relative: true
        );

    [TestInfo("OddSize_BC7")]
    public void TestBC7() => AssertMatchesReference("bc7", GraphicsFormat.R8G8B8A8_UNorm);

    /// <summary>
    /// The unreadable, asynchronous path goes through different texture
    /// creation code than the synchronous readable one.
    /// </summary>
    [TestInfo("OddSize_DXT5_UnreadableAsync")]
    public void TestDXT5UnreadableAsync()
    {
        using var handle = Load(
            "dxt5_6x7.dds",
            new TextureLoadOptions { Unreadable = true, Hint = TextureLoadHint.Asynchronous }
        );
        var tex = handle.GetTexture();

        if (tex.width != 6 || tex.height != 7 || tex.mipmapCount != 3)
            throw new Exception(
                $"expected 6x7 with 3 mips but got {tex.width}x{tex.height} with {tex.mipmapCount} mips"
            );
#if UNITY_6_7
        if (tex.graphicsFormat != GraphicsFormat.R8G8B8A8_UNorm)
            throw new Exception($"expected R8G8B8A8_UNorm but got {tex.graphicsFormat}");
#endif
    }
}
