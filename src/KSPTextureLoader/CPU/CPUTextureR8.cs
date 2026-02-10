using UnityEngine;

namespace KSPTextureLoader.CPU;

/// <summary>
/// A CPU texture with R8 format (1 byte per pixel: R).
/// </summary>
internal sealed class CPUTextureR8(CPUTextureData data, int width, int height, int mipCount)
    : CPUTexture2D(data, width, height, mipCount)
{
    public override TextureFormat format => TextureFormat.R8;

    public override Color GetPixel(int x, int y, int mipLevel = 0) => GetPixel32(x, y, mipLevel);

    public override Color32 GetPixel32(int x, int y, int mipLevel = 0)
    {
        int mipW = CPUTextureHelper.MipWidth(width, mipLevel);
        int mipH = CPUTextureHelper.MipHeight(height, mipLevel);
        int pixelIndex = CPUTextureHelper.PixelIndex(x, y, mipW, mipH);
        int byteOffset =
            CPUTextureHelper.UncompressedMipOffset(width, height, mipLevel, 1) + pixelIndex;

        return new Color32(data[byteOffset], 255, 255, 255);
    }
}
