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

    [TestInfo("CPUTexture2D_Alpha8_GetPixels")]
    public void TestAlpha8GetPixels()
    {
        TestFormatGetPixels(
            TextureFormat.Alpha8,
            (d, w, h, m) => new CPUTexture2D.Alpha8(d, w, h, m),
            "Alpha8"
        );
    }

    [TestInfo("CPUTexture2D_Alpha8_GetPixels32")]
    public void TestAlpha8GetPixels32()
    {
        TestFormatGetPixels32(
            TextureFormat.Alpha8,
            (d, w, h, m) => new CPUTexture2D.Alpha8(d, w, h, m),
            "Alpha8"
        );
    }
}
