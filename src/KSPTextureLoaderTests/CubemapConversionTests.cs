using System;
using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace KSPTextureLoaderTests;

/// <summary>
/// Tests for <see cref="TextureUtils.ConvertTexture2dToCubemap"/>, which turns a
/// 4x3 cross-layout <see cref="Texture2D"/> into a <see cref="Cubemap"/>.
///
/// The source texture is filled with a distinct solid color per (face, mip) so
/// that a face copied from the wrong cell, or a mip level that was never
/// written (and so contains garbage), is caught. Solid colors also keep the
/// checks independent of row order and block-compression artifacts.
/// </summary>
public class CubemapConversionTests : KSPTextureLoaderTestBase
{
    const int CubeDim = 16;
    const float Tol = 0.004f;
    const float CompressedTol = 0.04f; // DXT endpoints are RGB565

    // Cross-layout cell (in face units) for each cubemap face.
    static readonly (CubemapFace face, int x, int y)[] Layout =
    [
        (CubemapFace.NegativeY, 2, 2),
        (CubemapFace.PositiveX, 3, 1),
        (CubemapFace.PositiveZ, 2, 1),
        (CubemapFace.NegativeX, 1, 1),
        (CubemapFace.NegativeZ, 0, 1),
        (CubemapFace.PositiveY, 2, 0),
    ];

    static Color32 FaceColor(CubemapFace face, int mip) =>
        new((byte)(20 + (int)face * 40), (byte)(32 + mip * 64), (byte)(224 - mip * 64), 255);

    // Used for the unused cells of the cross so a mis-offset copy shows up.
    static readonly Color32 Filler = new(255, 0, 255, 128);

    static Texture2D CreateCrossTexture(bool mipChain)
    {
        var src = new Texture2D(CubeDim * 4, CubeDim * 3, TextureFormat.RGBA32, mipChain, true);

        for (int mip = 0; mip < src.mipmapCount; ++mip)
        {
            int dim = Math.Max(CubeDim >> mip, 1);
            int w = Math.Max(src.width >> mip, 1);
            int h = Math.Max(src.height >> mip, 1);

            var pixels = new Color32[w * h];
            for (int i = 0; i < pixels.Length; ++i)
                pixels[i] = Filler;

            foreach (var (face, cx, cy) in Layout)
            {
                var color = FaceColor(face, mip);
                for (int y = cy * dim; y < Math.Min((cy + 1) * dim, h); ++y)
                for (int x = cx * dim; x < Math.Min((cx + 1) * dim, w); ++x)
                    pixels[y * w + x] = color;
            }

            src.SetPixels32(pixels, mip);
        }

        // Don't regenerate mips, we want the exact per-mip colors above.
        src.Apply(false, false);
        return src;
    }

    /// <summary>
    /// Reads a cubemap face back through the GPU, so this works for unreadable
    /// and compressed cubemaps.
    /// </summary>
    internal static Color[] ReadBackFace(Cubemap cube, CubemapFace face, int mip)
    {
        int dim = Math.Max(cube.width >> mip, 1);

        var faceTex = new Texture2D(dim, dim, cube.graphicsFormat, TextureCreationFlags.None)
        {
            filterMode = FilterMode.Point,
        };
        var readback = new Texture2D(dim, dim, TextureFormat.RGBA32, false, true);
        var rt = RenderTexture.GetTemporary(
            dim,
            dim,
            0,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.Linear
        );

        try
        {
            Graphics.CopyTexture(cube, (int)face, mip, faceTex, 0, 0);
            Graphics.Blit(faceTex, rt);

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            readback.ReadPixels(new Rect(0, 0, dim, dim), 0, 0);
            RenderTexture.active = prev;

            return readback.GetPixels();
        }
        finally
        {
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.Destroy(faceTex);
            UnityEngine.Object.Destroy(readback);
        }
    }

    static void AssertMipCount(Cubemap cube, int expected)
    {
        if (cube.width != CubeDim || cube.height != CubeDim)
            throw new Exception(
                $"expected {CubeDim}x{CubeDim} cubemap but got {cube.width}x{cube.height}"
            );
        if (cube.mipmapCount != expected)
            throw new Exception($"expected {expected} mip levels but got {cube.mipmapCount}");
    }

    void AssertFaces(Cubemap cube, bool gpuReadback, float tol)
    {
        for (int mip = 0; mip < cube.mipmapCount; ++mip)
        {
            int dim = CubeDim >> mip;
            foreach (var (face, _, _) in Layout)
            {
                var pixels = gpuReadback
                    ? ReadBackFace(cube, face, mip)
                    : cube.GetPixels(face, mip);
                if (pixels.Length != dim * dim)
                    throw new Exception(
                        $"{face} mip {mip}: expected {dim * dim} pixels but got {pixels.Length}"
                    );

                Color expected = FaceColor(face, mip);
                for (int i = 0; i < pixels.Length; ++i)
                    assertColorEquals($"{face}.mip{mip}[{i}]", pixels[i], expected, tol);
            }
        }
    }

    void RunConversion(Texture2D src, bool readable, Action<Cubemap> check)
    {
        Cubemap cube = null;
        try
        {
            cube = TextureUtils.ConvertTexture2dToCubemap(src, readable);
            if (cube.isReadable != readable)
                throw new Exception($"expected isReadable == {readable} but got {cube.isReadable}");
            check(cube);
        }
        finally
        {
            UnityEngine.Object.Destroy(src);
            if (cube != null)
                UnityEngine.Object.Destroy(cube);
        }
    }

    [TestInfo("CubemapConversion_Readable_CopiesAllMips")]
    public void TestReadableCopiesAllMips()
    {
        RunConversion(
            CreateCrossTexture(true),
            true,
            cube =>
            {
                // RGBA32 with a 16px face supports log2(16) = 4 mip levels.
                AssertMipCount(cube, 4);

                // Both the CPU and the GPU copies need to have been written.
                AssertFaces(cube, false, Tol);
                AssertFaces(cube, true, Tol);
            }
        );
    }

    [TestInfo("CubemapConversion_Unreadable_CopiesAllMips")]
    public void TestUnreadableCopiesAllMips()
    {
        RunConversion(
            CreateCrossTexture(true),
            false,
            cube =>
            {
                AssertMipCount(cube, 4);
                AssertFaces(cube, true, Tol);
            }
        );
    }

    [TestInfo("CubemapConversion_Unreadable_DXT5")]
    public void TestUnreadableDXT5()
    {
        // Converting an unreadable compressed source only works via GPU copies.
        if (!TextureUtils.CanGpuCopyTexture2dToCubemap)
            return;

        var src = CreateCrossTexture(true);
        src.Compress(false);
        src.Apply(false, true);

        if (src.format != TextureFormat.DXT5)
            throw new Exception($"expected source to compress to DXT5 but got {src.format}");

        RunConversion(
            src,
            false,
            cube =>
            {
                if (cube.format != TextureFormat.DXT5)
                    throw new Exception($"expected a DXT5 cubemap but got {cube.format}");

                // GetSupportedMipMapLevels gives log2(16) - log2(4) = 2 levels for DXT5.
                AssertMipCount(cube, 2);
                AssertFaces(cube, true, CompressedTol);
            }
        );
    }

    [TestInfo("CubemapConversion_NoMipSource")]
    public void TestNoMipSource()
    {
        RunConversion(
            CreateCrossTexture(false),
            true,
            cube =>
            {
                // The cubemap must not have more mips than the source provides,
                // since any extra levels would never be written.
                AssertMipCount(cube, 1);
                AssertFaces(cube, false, Tol);
            }
        );
    }
}
