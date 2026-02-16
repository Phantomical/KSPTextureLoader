using System;
using KSP.Testing;
using KSPTextureLoader;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoaderTests;

/// <summary>
/// Tests each <see cref="CPUTexture2D"/> format struct against
/// <see cref="Texture2D.GetPixel(int,int)"/> to verify that pixel decoding matches Unity.
/// </summary>
public class CPUTexture2DTests : KSPTextureLoaderTestBase
{
    const int W = 4;
    const int H = 4;
    const float Tol = 0.005f; // slightly more than 1/255

    /// <summary>
    /// Creates a Texture2D with known pixel values in the given format,
    /// using RGBA32 as the source and converting via SetPixel/GetPixel.
    /// Returns the texture and a grid of ground-truth pixel colors.
    /// </summary>
    protected static (Texture2D tex, Color[,] pixels) MakeTestTexture(TextureFormat fmt)
    {
        var src = new Texture2D(W, H, TextureFormat.RGBA32, false);
        var colors = new Color32[W * H];

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                byte r = (byte)(x * 80 + 15);
                byte g = (byte)(y * 60 + 30);
                byte b = (byte)((x + y) * 40 + 50);
                byte a = (byte)(200 - x * 20 - y * 10);
                colors[y * W + x] = new Color32(r, g, b, a);
            }
        }
        src.SetPixels32(colors);
        src.Apply(false, false);

        Texture2D tex;
        if (fmt == TextureFormat.RGBA32)
        {
            tex = src;
        }
        else
        {
            tex = new Texture2D(W, H, fmt, false);
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                tex.SetPixel(x, y, src.GetPixel(x, y));
            tex.Apply(false, false);
            UnityEngine.Object.Destroy(src);
        }

        var pixelGrid = new Color[W, H];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
            pixelGrid[x, y] = tex.GetPixel(x, y);

        return (tex, pixelGrid);
    }

    /// <summary>
    /// Tests a CPUTexture2D format struct's GetPixels against its own GetPixel,
    /// verifying that the bulk method produces the same results as per-pixel access.
    /// </summary>
    protected void TestFormatGetPixels<T>(
        TextureFormat fmt,
        Func<NativeArray<byte>, int, int, int, T> factory,
        string name
    )
        where T : ICPUTexture2D, IGetPixels
    {
        var (tex, _) = MakeTestTexture(fmt);
        try
        {
            var rawData = tex.GetRawTextureData<byte>();
            var cpuTex = factory(rawData, tex.width, tex.height, tex.mipmapCount);
            var pixels = cpuTex.GetPixels();

            if (pixels.Length != W * H)
                throw new Exception(
                    $"{name}.GetPixels: expected {W * H} pixels, got {pixels.Length}"
                );

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    Color expected = cpuTex.GetPixel(x, y);
                    Color actual = pixels[y * W + x];
                    assertColorEquals($"{name}.GetPixels({x},{y})", actual, expected, 1e-6f);
                }
            }
        }
        finally
        {
            UnityEngine.Object.Destroy(tex);
        }
    }

    /// <summary>
    /// Tests a CPUTexture2D format struct's GetPixels32 against its own GetPixel32,
    /// verifying that the bulk method produces the same results as per-pixel access.
    /// </summary>
    protected void TestFormatGetPixels32<T>(
        TextureFormat fmt,
        Func<NativeArray<byte>, int, int, int, T> factory,
        string name
    )
        where T : ICPUTexture2D, IGetPixels
    {
        var (tex, _) = MakeTestTexture(fmt);
        try
        {
            var rawData = tex.GetRawTextureData<byte>();
            var cpuTex = factory(rawData, tex.width, tex.height, tex.mipmapCount);
            var pixels = cpuTex.GetPixels32();

            if (pixels.Length != W * H)
                throw new Exception(
                    $"{name}.GetPixels32: expected {W * H} pixels, got {pixels.Length}"
                );

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    Color32 expected = cpuTex.GetPixel32(x, y);
                    Color32 actual = pixels[y * W + x];
                    assertColor32Equals($"{name}.GetPixels32({x},{y})", actual, expected, 0);
                }
            }
        }
        finally
        {
            UnityEngine.Object.Destroy(tex);
        }
    }

    /// <summary>
    /// Tests a CPUTexture2D format struct's GetPixel against Texture2D.GetPixel.
    /// Only the channels specified by check flags are compared, since formats
    /// with fewer channels may fill unused channels differently than Unity.
    /// </summary>
    protected void TestFormatGetPixel<T>(
        TextureFormat fmt,
        Func<NativeArray<byte>, int, int, int, T> factory,
        string name,
        bool checkR,
        bool checkG,
        bool checkB,
        bool checkA,
        float tolerance = Tol
    )
        where T : ICPUTexture2D
    {
        var (tex, pixels) = MakeTestTexture(fmt);
        try
        {
            var rawData = tex.GetRawTextureData<byte>();
            var cpuTex = factory(rawData, tex.width, tex.height, tex.mipmapCount);

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    Color expected = pixels[x, y];
                    Color actual = cpuTex.GetPixel(x, y);

                    if (checkR)
                        assertFloatEquals($"{name}.R({x},{y})", actual.r, expected.r, tolerance);
                    if (checkG)
                        assertFloatEquals($"{name}.G({x},{y})", actual.g, expected.g, tolerance);
                    if (checkB)
                        assertFloatEquals($"{name}.B({x},{y})", actual.b, expected.b, tolerance);
                    if (checkA)
                        assertFloatEquals($"{name}.A({x},{y})", actual.a, expected.a, tolerance);
                }
            }
        }
        finally
        {
            UnityEngine.Object.Destroy(tex);
        }
    }
}
