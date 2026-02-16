using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class RFloatTests : CPUTexture2DTests
{
    [TestInfo("CPUTexture2D_RFloat")]
    public void TestRFloat()
    {
        TestFormatGetPixel(
            TextureFormat.RFloat,
            (d, w, h, m) => new CPUTexture2D.RFloat(d, w, h, m),
            "RFloat",
            checkR: true,
            checkG: true,
            checkB: true,
            checkA: true,
            tolerance: 0.001f
        );
    }

    [TestInfo("CPUTexture2D_RFloat_GetPixels")]
    public void TestRFloatGetPixels()
    {
        TestFormatGetPixels(
            TextureFormat.RFloat,
            (d, w, h, m) => new CPUTexture2D.RFloat(d, w, h, m),
            "RFloat"
        );
    }

    [TestInfo("CPUTexture2D_RFloat_GetPixels32")]
    public void TestRFloatGetPixels32()
    {
        TestFormatGetPixels32(
            TextureFormat.RFloat,
            (d, w, h, m) => new CPUTexture2D.RFloat(d, w, h, m),
            "RFloat"
        );
    }
}
