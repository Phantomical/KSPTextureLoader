using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class RGB24Tests : CPUTexture2DTests
{
    [TestInfo("CPUTexture2D_RGB24")]
    public void TestRGB24()
    {
        TestFormatGetPixel(
            TextureFormat.RGB24,
            (d, w, h, m) => new CPUTexture2D.RGB24(d, w, h, m),
            "RGB24",
            checkR: true,
            checkG: true,
            checkB: true,
            checkA: true
        );
    }

    [TestInfo("CPUTexture2D_RGB24_GetPixels")]
    public void TestRGB24GetPixels()
    {
        TestFormatGetPixels(
            TextureFormat.RGB24,
            (d, w, h, m) => new CPUTexture2D.RGB24(d, w, h, m),
            "RGB24"
        );
    }

    [TestInfo("CPUTexture2D_RGB24_GetPixels32")]
    public void TestRGB24GetPixels32()
    {
        TestFormatGetPixels32(
            TextureFormat.RGB24,
            (d, w, h, m) => new CPUTexture2D.RGB24(d, w, h, m),
            "RGB24"
        );
    }
}
