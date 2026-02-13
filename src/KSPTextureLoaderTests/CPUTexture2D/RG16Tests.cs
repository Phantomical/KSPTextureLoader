using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

partial class CPUTexture2DTests
{
    [TestInfo("CPUTexture2D_RG16")]
    public void TestRG16()
    {
        TestFormatGetPixel(
            TextureFormat.RG16,
            (d, w, h, m) => new CPUTexture2D.RG16(d, w, h, m),
            "RG16",
            checkR: true,
            checkG: true,
            checkB: true,
            checkA: true
        );
    }
}
