using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

partial class CPUTexture2DTests
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
}
