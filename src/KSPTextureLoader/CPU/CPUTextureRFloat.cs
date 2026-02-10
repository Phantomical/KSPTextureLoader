using UnityEngine;

namespace KSPTextureLoader.CPU;

/// <summary>
/// A CPU texture with RFloat format (4 bytes per pixel: single precision float for R channel).
/// </summary>
internal sealed class CPUTextureRFloat(CPUTextureData data, int width, int height, int mipCount)
    : CPUTexture2D(data, width, height, mipCount)
{
    public override TextureFormat format => TextureFormat.RFloat;

    public override Color GetPixel(int x, int y, int mipLevel = 0)
    {
        int mipW = CPUTextureHelper.MipWidth(width, mipLevel);
        int mipH = CPUTextureHelper.MipHeight(height, mipLevel);
        int pixelIndex = CPUTextureHelper.PixelIndex(x, y, mipW, mipH);
        int byteOffset =
            CPUTextureHelper.UncompressedMipOffset(width, height, mipLevel, 4) + pixelIndex * 4;

        float v = CPUTextureHelper.ReadSingle(data, byteOffset);

        return new Color(v, 1f, 1f, 1f);
    }
}
