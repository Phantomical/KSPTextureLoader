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
}
