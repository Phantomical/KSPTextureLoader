using System;
using KSP.Testing;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoaderTests;

public abstract class KSPTextureLoaderTestBase : UnitTest
{
    const float DefaultTolerance = 0.004f; // ~1/255, enough for byte->float roundtrip
    const int DefaultByteTolerance = 1;

    protected void assertFloatEquals(
        string name,
        float actual,
        float expected,
        float tol = DefaultTolerance
    )
    {
        if (Math.Abs(actual - expected) > tol)
            throw new Exception(
                $"TEST {name}: FAIL! Float {actual:F6} != {expected:F6} (tol={tol})"
            );
    }

    protected void assertColorEquals(
        string name,
        Color actual,
        Color expected,
        float tol = DefaultTolerance
    )
    {
        if (
            Math.Abs(actual.r - expected.r) > tol
            || Math.Abs(actual.g - expected.g) > tol
            || Math.Abs(actual.b - expected.b) > tol
            || Math.Abs(actual.a - expected.a) > tol
        )
        {
            throw new Exception(
                $"TEST {name}: FAIL! Color({actual.r:F4},{actual.g:F4},{actual.b:F4},{actual.a:F4}) != "
                    + $"({expected.r:F4},{expected.g:F4},{expected.b:F4},{expected.a:F4}) (tol={tol})"
            );
        }
    }

    protected void assertColor32Equals(
        string name,
        Color32 actual,
        Color32 expected,
        int tol = DefaultByteTolerance
    )
    {
        if (
            Math.Abs(actual.r - expected.r) > tol
            || Math.Abs(actual.g - expected.g) > tol
            || Math.Abs(actual.b - expected.b) > tol
            || Math.Abs(actual.a - expected.a) > tol
        )
        {
            throw new Exception(
                $"TEST {name}: FAIL! Color32({actual.r},{actual.g},{actual.b},{actual.a}) != "
                    + $"({expected.r},{expected.g},{expected.b},{expected.a}) (tol={tol})"
            );
        }
    }

    protected static Texture2D CreateTexture(int w, int h, TextureFormat fmt, byte[] rawData)
    {
        var tex = new Texture2D(w, h, fmt, false);
        tex.LoadRawTextureData(rawData);
        tex.Apply(false, false);
        return tex;
    }

    protected static byte[] MakeGradientData(int width, int height, int bpp)
    {
        var data = new byte[width * height * bpp];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = (y * width + x) * bpp;
                for (int c = 0; c < bpp; c++)
                {
                    data[idx + c] = (byte)((x * 37 + y * 59 + c * 97) % 256);
                }
            }
        }
        return data;
    }
}
