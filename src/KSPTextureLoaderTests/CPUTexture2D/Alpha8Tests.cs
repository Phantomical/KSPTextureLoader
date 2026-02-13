using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class Alpha8Tests : CPUTexture2DTests
{
    [TestInfo("CPUTexture2D_Alpha8")]
    public void TestAlpha8()
    {
        TestFormatGetPixel(
            TextureFormat.Alpha8,
            (d, w, h, m) => new CPUTexture2D.Alpha8(d, w, h, m),
            "Alpha8",
            checkR: true,
            checkG: true,
            checkB: true,
            checkA: true
        );
    }
}
