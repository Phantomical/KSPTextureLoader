using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class R8Tests : CPUTexture2DTests
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

    [TestInfo("CPUTexture2D_R8_GetPixels")]
    public void TestR8GetPixels()
    {
        TestFormatGetPixels(
            TextureFormat.R8,
            (d, w, h, m) => new CPUTexture2D.R8(d, w, h, m),
            "R8"
        );
    }

    [TestInfo("CPUTexture2D_R8_GetPixels32")]
    public void TestR8GetPixels32()
    {
        TestFormatGetPixels32(
            TextureFormat.R8,
            (d, w, h, m) => new CPUTexture2D.R8(d, w, h, m),
            "R8"
        );
    }
}
