using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class RGB565Tests : CPUTexture2DTests
{
    [TestInfo("CPUTexture2D_RGB565")]
    public void TestRGB565()
    {
        // 5-bit channels have ~1/31 precision
        TestFormatGetPixel(
            TextureFormat.RGB565,
            (d, w, h, m) => new CPUTexture2D.RGB565(d, w, h, m),
            "RGB565",
            checkR: true,
            checkG: true,
            checkB: true,
            checkA: true,
            tolerance: 0.04f
        );
    }
}
