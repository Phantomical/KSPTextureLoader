using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class RGFloatTests : CPUTexture2DTests
{
    [TestInfo("CPUTexture2D_RGFloat")]
    public void TestRGFloat()
    {
        TestFormatGetPixel(
            TextureFormat.RGFloat,
            (d, w, h, m) => new CPUTexture2D.RGFloat(d, w, h, m),
            "RGFloat",
            checkR: true,
            checkG: true,
            checkB: true,
            checkA: true,
            tolerance: 0.001f
        );
    }
}
