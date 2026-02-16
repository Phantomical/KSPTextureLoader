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

    [TestInfo("CPUTexture2D_R16_GetPixels")]
    public void TestR16GetPixels()
    {
        TestFormatGetPixels(
            TextureFormat.R16,
            (d, w, h, m) => new CPUTexture2D.R16(d, w, h, m),
            "R16"
        );
    }

    [TestInfo("CPUTexture2D_R16_GetPixels32")]
    public void TestR16GetPixels32()
    {
        TestFormatGetPixels32(
            TextureFormat.R16,
            (d, w, h, m) => new CPUTexture2D.R16(d, w, h, m),
            "R16"
        );
    }
}
