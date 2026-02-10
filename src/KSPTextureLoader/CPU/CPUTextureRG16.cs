using UnityEngine;

namespace KSPTextureLoader.CPU;

internal sealed class CPUTextureRG16(CPUTextureData data, int width, int height, int mipCount)
    : CPUTexture2D(data, width, height, mipCount)
{
    public override TextureFormat format => TextureFormat.RG16;

    public override Color GetPixel(int x, int y, int mipLevel = 0) => GetPixel32(x, y, mipLevel);

    public override Color32 GetPixel32(int x, int y, int mipLevel = 0)
    {
        int mipWidth = CPUTextureHelper.MipWidth(width, mipLevel);
        int mipHeight = CPUTextureHelper.MipHeight(height, mipLevel);
        int mipOffset = CPUTextureHelper.UncompressedMipOffset(width, height, mipLevel, 2);
        int pixelIndex = CPUTextureHelper.PixelIndex(x, y, mipWidth, mipHeight);
        int byteOffset = mipOffset + (pixelIndex * 2);

        return new Color32(data[byteOffset], data[byteOffset + 1], 255, 255);
    }
}
