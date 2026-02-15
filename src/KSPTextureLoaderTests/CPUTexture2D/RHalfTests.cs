using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class RHalfTests : CPUTexture2DTests
{
    [TestInfo("CPUTexture2D_RHalf")]
    public void TestRHalf()
    {
        TestFormatGetPixel(
            TextureFormat.RHalf,
            (d, w, h, m) => new CPUTexture2D.RHalf(d, w, h, m),
            "RHalf",
            checkR: true,
            checkG: true,
            checkB: true,
            checkA: true,
            tolerance: 0.002f
        );
    }

    [TestInfo("CPUTexture2D_RHalf_GetPixels")]
    public void TestRHalfGetPixels()
    {
        TestFormatGetPixels(
            TextureFormat.RHalf,
            (d, w, h, m) => new CPUTexture2D.RHalf(d, w, h, m),
            "RHalf"
        );
    }

    [TestInfo("CPUTexture2D_RHalf_GetPixels32")]
    public void TestRHalfGetPixels32()
    {
        TestFormatGetPixels32(
            TextureFormat.RHalf,
            (d, w, h, m) => new CPUTexture2D.RHalf(d, w, h, m),
            "RHalf"
        );
    }
}
