using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class RG16Tests : CPUTexture2DTests
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

    [TestInfo("CPUTexture2D_RG16_GetPixels")]
    public void TestRG16GetPixels()
    {
        TestFormatGetPixels(
            TextureFormat.RG16,
            (d, w, h, m) => new CPUTexture2D.RG16(d, w, h, m),
            "RG16"
        );
    }

    [TestInfo("CPUTexture2D_RG16_GetPixels32")]
    public void TestRG16GetPixels32()
    {
        TestFormatGetPixels32(
            TextureFormat.RG16,
            (d, w, h, m) => new CPUTexture2D.RG16(d, w, h, m),
            "RG16"
        );
    }
}
