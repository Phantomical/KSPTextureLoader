using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class RGBA32Tests : CPUTexture2DTests
{
    [TestInfo("CPUTexture2D_RGBA32")]
    public void TestRGBA32()
    {
        TestFormatGetPixel(
            TextureFormat.RGBA32,
            (d, w, h, m) => new CPUTexture2D.RGBA32(d, w, h, m),
            "RGBA32",
            checkR: true,
            checkG: true,
            checkB: true,
            checkA: true
        );
    }
}
