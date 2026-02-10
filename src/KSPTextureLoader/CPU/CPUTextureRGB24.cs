using UnityEngine;

namespace KSPTextureLoader.CPU;

/// <summary>
/// A CPU texture with RGB24 format (3 bytes per pixel: R, G, B).
/// </summary>
internal sealed class CPUTextureRGB24(CPUTextureData data, int width, int height, int mipCount)
    : CPUTexture2D(data, width, height, mipCount)
{
    public override TextureFormat format => TextureFormat.RGB24;

    public override Color GetPixel(int x, int y, int mipLevel = 0) => GetPixel32(x, y, mipLevel);

    public override Color32 GetPixel32(int x, int y, int mipLevel = 0)
    {
        int mipW = CPUTextureHelper.MipWidth(width, mipLevel);
        int mipH = CPUTextureHelper.MipHeight(height, mipLevel);
        int pixelIndex = CPUTextureHelper.PixelIndex(x, y, mipW, mipH);
        int byteOffset =
            CPUTextureHelper.UncompressedMipOffset(width, height, mipLevel, 3) + pixelIndex * 3;

        return new Color32(data[byteOffset], data[byteOffset + 1], data[byteOffset + 2], 255);
    }
}
