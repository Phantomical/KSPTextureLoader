using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class ARGB32Tests : CPUTexture2DTests
{
    [TestInfo("CPUTexture2D_ARGB32")]
    public void TestARGB32()
    {
        TestFormatGetPixel(
            TextureFormat.ARGB32,
            (d, w, h, m) => new CPUTexture2D.ARGB32(d, w, h, m),
            "ARGB32",
            checkR: true,
            checkG: true,
            checkB: true,
            checkA: true
        );
    }

    [TestInfo("CPUTexture2D_ARGB32_GetPixels")]
    public void TestARGB32GetPixels()
    {
        TestFormatGetPixels(
            TextureFormat.ARGB32,
            (d, w, h, m) => new CPUTexture2D.ARGB32(d, w, h, m),
            "ARGB32"
        );
    }

    [TestInfo("CPUTexture2D_ARGB32_GetPixels32")]
    public void TestARGB32GetPixels32()
    {
        TestFormatGetPixels32(
            TextureFormat.ARGB32,
            (d, w, h, m) => new CPUTexture2D.ARGB32(d, w, h, m),
            "ARGB32"
        );
    }
}
