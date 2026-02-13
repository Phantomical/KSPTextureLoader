using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

partial class CPUTexture2DTests
{
    [TestInfo("CPUTexture2D_RGB24")]
    public void TestRGB24()
    {
        TestFormatGetPixel(
            TextureFormat.RGB24,
            (d, w, h, m) => new CPUTexture2D.RGB24(d, w, h, m),
            "RGB24",
            checkR: true,
            checkG: true,
            checkB: true,
            checkA: true
        );
    }
}
