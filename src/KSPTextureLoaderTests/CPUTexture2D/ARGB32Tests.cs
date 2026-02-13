using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

partial class CPUTexture2DTests
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
}
