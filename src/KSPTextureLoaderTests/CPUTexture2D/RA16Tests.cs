using System;
using KSP.Testing;
using KSPTextureLoader;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class RA16Tests : KSPTextureLoaderTestBase
{
    const int W = 4;
    const int H = 4;

    static NativeArray<byte> MakeRA16Data()
    {
        var data = new NativeArray<byte>(
            W * H * 2,
            Allocator.Temp,
            NativeArrayOptions.UninitializedMemory
        );
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int idx = (y * W + x) * 2;
                data[idx] = (byte)(x * 80 + 15); // R
                data[idx + 1] = (byte)(200 - x * 20 - y * 10); // A
            }
        }
        return data;
    }

    [TestInfo("CPUTexture2D_RA16_GetPixels")]
    public void TestRA16GetPixels()
    {
        var data = MakeRA16Data();
        var cpuTex = new CPUTexture2D.RA16(data, W, H, 1);
        var pixels = cpuTex.GetPixels();

        if (pixels.Length != W * H)
            throw new Exception($"RA16.GetPixels: expected {W * H} pixels, got {pixels.Length}");

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                Color expected = cpuTex.GetPixel(x, y);
                Color actual = pixels[y * W + x];
                assertColorEquals($"RA16.GetPixels({x},{y})", actual, expected, 1e-6f);
            }
        }
    }

    [TestInfo("CPUTexture2D_RA16_GetPixels32")]
    public void TestRA16GetPixels32()
    {
        var data = MakeRA16Data();
        var cpuTex = new CPUTexture2D.RA16(data, W, H, 1);
        var pixels = cpuTex.GetPixels32();

        if (pixels.Length != W * H)
            throw new Exception($"RA16.GetPixels32: expected {W * H} pixels, got {pixels.Length}");

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                Color32 expected = cpuTex.GetPixel32(x, y);
                Color32 actual = pixels[y * W + x];
                assertColor32Equals($"RA16.GetPixels32({x},{y})", actual, expected, 0);
            }
        }
    }
}
