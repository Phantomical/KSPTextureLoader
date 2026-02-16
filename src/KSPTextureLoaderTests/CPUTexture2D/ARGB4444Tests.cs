using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class ARGB4444Tests : CPUTexture2DTests
{
    [TestInfo("CPUTexture2D_ARGB4444")]
    public void TestARGB4444()
    {
        // 4-bit channels have 1/15 precision
        TestFormatGetPixel(
            TextureFormat.ARGB4444,
            (d, w, h, m) => new CPUTexture2D.ARGB4444(d, w, h, m),
            "ARGB4444",
            checkR: true,
            checkG: true,
            checkB: true,
            checkA: true,
            tolerance: 0.07f
        );
    }

    [TestInfo("CPUTexture2D_ARGB4444_GetPixels")]
    public void TestARGB4444GetPixels()
    {
        TestFormatGetPixels(
            TextureFormat.ARGB4444,
            (d, w, h, m) => new CPUTexture2D.ARGB4444(d, w, h, m),
            "ARGB4444"
        );
    }

    [TestInfo("CPUTexture2D_ARGB4444_GetPixels32")]
    public void TestARGB4444GetPixels32()
    {
        TestFormatGetPixels32(
            TextureFormat.ARGB4444,
            (d, w, h, m) => new CPUTexture2D.ARGB4444(d, w, h, m),
            "ARGB4444"
        );
    }
}
