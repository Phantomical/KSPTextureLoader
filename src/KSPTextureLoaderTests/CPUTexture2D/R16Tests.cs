using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class R16Tests : CPUTexture2DTests
{
    [TestInfo("CPUTexture2D_R16")]
    public void TestR16()
    {
        TestFormatGetPixel(
            TextureFormat.R16,
            (d, w, h, m) => new CPUTexture2D.R16(d, w, h, m),
            "R16",
            checkR: true,
            checkG: true,
            checkB: true,
            checkA: true
        );
    }
}
