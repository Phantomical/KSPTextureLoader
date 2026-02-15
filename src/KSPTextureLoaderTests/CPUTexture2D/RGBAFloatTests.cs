using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class RGBAFloatTests : CPUTexture2DTests
{
    [TestInfo("CPUTexture2D_RGBAFloat")]
    public void TestRGBAFloat()
    {
        TestFormatGetPixel(
            TextureFormat.RGBAFloat,
            (d, w, h, m) => new CPUTexture2D.RGBAFloat(d, w, h, m),
            "RGBAFloat",
            checkR: true,
            checkG: true,
            checkB: true,
            checkA: true,
            tolerance: 0.001f
        );
    }

    [TestInfo("CPUTexture2D_RGBAFloat_GetPixels")]
    public void TestRGBAFloatGetPixels()
    {
        TestFormatGetPixels(
            TextureFormat.RGBAFloat,
            (d, w, h, m) => new CPUTexture2D.RGBAFloat(d, w, h, m),
            "RGBAFloat"
        );
    }

    [TestInfo("CPUTexture2D_RGBAFloat_GetPixels32")]
    public void TestRGBAFloatGetPixels32()
    {
        TestFormatGetPixels32(
            TextureFormat.RGBAFloat,
            (d, w, h, m) => new CPUTexture2D.RGBAFloat(d, w, h, m),
            "RGBAFloat"
        );
    }
}
