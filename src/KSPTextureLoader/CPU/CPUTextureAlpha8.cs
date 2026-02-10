using UnityEngine;

namespace KSPTextureLoader.CPU;

internal sealed class CPUTextureAlpha8(CPUTextureData data, int width, int height, int mipCount)
    : CPUTexture2D(data, width, height, mipCount)
{
    public override TextureFormat format => TextureFormat.Alpha8;

    public override Color GetPixel(int x, int y, int mipLevel = 0) => GetPixel32(x, y, mipLevel);

    public override Color32 GetPixel32(int x, int y, int mipLevel = 0)
    {
        int mipWidth = CPUTextureHelper.MipWidth(width, mipLevel);
        int mipHeight = CPUTextureHelper.MipHeight(height, mipLevel);
        int offset = CPUTextureHelper.UncompressedMipOffset(width, height, mipLevel, 1);
        int pixelIndex = CPUTextureHelper.PixelIndex(x, y, mipWidth, mipHeight);

        return new Color32(255, 255, 255, data[offset + pixelIndex]);
    }
}
