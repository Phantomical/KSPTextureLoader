using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class RGBAHalfTests : CPUTexture2DTests
{
    [TestInfo("CPUTexture2D_RGBAHalf")]
    public void TestRGBAHalf()
    {
        TestFormatGetPixel(
            TextureFormat.RGBAHalf,
            (d, w, h, m) => new CPUTexture2D.RGBAHalf(d, w, h, m),
            "RGBAHalf",
            checkR: true,
            checkG: true,
            checkB: true,
            checkA: true,
            tolerance: 0.002f
        );
    }

    [TestInfo("CPUTexture2D_RGBAHalf_GetPixels")]
    public void TestRGBAHalfGetPixels()
    {
        TestFormatGetPixels(
            TextureFormat.RGBAHalf,
            (d, w, h, m) => new CPUTexture2D.RGBAHalf(d, w, h, m),
            "RGBAHalf"
        );
    }

    [TestInfo("CPUTexture2D_RGBAHalf_GetPixels32")]
    public void TestRGBAHalfGetPixels32()
    {
        TestFormatGetPixels32(
            TextureFormat.RGBAHalf,
            (d, w, h, m) => new CPUTexture2D.RGBAHalf(d, w, h, m),
            "RGBAHalf"
        );
    }
}
