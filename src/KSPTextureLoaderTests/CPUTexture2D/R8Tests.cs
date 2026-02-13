using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

partial class CPUTexture2DTests
{
    [TestInfo("CPUTexture2D_R8")]
    public void TestR8()
    {
        TestFormatGetPixel(
            TextureFormat.R8,
            (d, w, h, m) => new CPUTexture2D.R8(d, w, h, m),
            "R8",
            checkR: true,
            checkG: true,
            checkB: true,
            checkA: true
        );
    }
}
