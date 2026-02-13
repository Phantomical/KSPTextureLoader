using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class BGRA32Tests : CPUTexture2DTests
{
    [TestInfo("CPUTexture2D_BGRA32")]
    public void TestBGRA32()
    {
        TestFormatGetPixel(
            TextureFormat.BGRA32,
            (d, w, h, m) => new CPUTexture2D.BGRA32(d, w, h, m),
            "BGRA32",
            checkR: true,
            checkG: true,
            checkB: true,
            checkA: true
        );
    }
}
