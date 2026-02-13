using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class RGBA4444Tests : CPUTexture2DTests
{
    [TestInfo("CPUTexture2D_RGBA4444")]
    public void TestRGBA4444()
    {
        // 4-bit channels have 1/15 precision
        TestFormatGetPixel(
            TextureFormat.RGBA4444,
            (d, w, h, m) => new CPUTexture2D.RGBA4444(d, w, h, m),
            "RGBA4444",
            checkR: true,
            checkG: true,
            checkB: true,
            checkA: true,
            tolerance: 0.07f
        );
    }
}
