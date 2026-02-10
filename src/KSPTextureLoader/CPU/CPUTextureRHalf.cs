using UnityEngine;

namespace KSPTextureLoader.CPU;

/// <summary>
/// A CPU texture with RHalf format (2 bytes per pixel: half-precision float R).
/// </summary>
internal sealed class CPUTextureRHalf(CPUTextureData data, int width, int height, int mipCount)
    : CPUTexture2D(data, width, height, mipCount)
{
    public override TextureFormat format => TextureFormat.RHalf;

    public override Color GetPixel(int x, int y, int mipLevel = 0)
    {
        int mipW = CPUTextureHelper.MipWidth(width, mipLevel);
        int mipH = CPUTextureHelper.MipHeight(height, mipLevel);
        int pixelIndex = CPUTextureHelper.PixelIndex(x, y, mipW, mipH);
        int byteOffset =
            CPUTextureHelper.UncompressedMipOffset(width, height, mipLevel, 2) + pixelIndex * 2;

        float v = CPUTextureHelper.ReadHalf(data, byteOffset);

        return new Color(v, 1f, 1f, 1f);
    }
}
