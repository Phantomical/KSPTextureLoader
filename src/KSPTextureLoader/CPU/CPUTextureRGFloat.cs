using UnityEngine;

namespace KSPTextureLoader.CPU;

internal sealed class CPUTextureRGFloat(CPUTextureData data, int width, int height, int mipCount)
    : CPUTexture2D(data, width, height, mipCount)
{
    public override TextureFormat format => TextureFormat.RGFloat;

    public override Color GetPixel(int x, int y, int mipLevel = 0)
    {
        int mipWidth = CPUTextureHelper.MipWidth(width, mipLevel);
        int mipHeight = CPUTextureHelper.MipHeight(height, mipLevel);
        int mipOffset = CPUTextureHelper.UncompressedMipOffset(width, height, mipLevel, 8);
        int pixelIndex = CPUTextureHelper.PixelIndex(x, y, mipWidth, mipHeight);
        int byteOffset = mipOffset + pixelIndex * 8;

        float r = CPUTextureHelper.ReadSingle(data, byteOffset);
        float g = CPUTextureHelper.ReadSingle(data, byteOffset + 4);

        return new Color(r, g, 1f, 1f);
    }
}
