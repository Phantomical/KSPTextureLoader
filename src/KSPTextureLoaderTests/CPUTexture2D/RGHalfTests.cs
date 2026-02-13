using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class RGHalfTests : CPUTexture2DTests
{
    [TestInfo("CPUTexture2D_RGHalf")]
    public void TestRGHalf()
    {
        TestFormatGetPixel(
            TextureFormat.RGHalf,
            (d, w, h, m) => new CPUTexture2D.RGHalf(d, w, h, m),
            "RGHalf",
            checkR: true,
            checkG: true,
            checkB: true,
            checkA: true,
            tolerance: 0.002f
        );
    }
}
