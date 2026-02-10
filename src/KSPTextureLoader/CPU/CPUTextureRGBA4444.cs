using UnityEngine;

namespace KSPTextureLoader.CPU;

internal sealed class CPUTextureRGBA4444(CPUTextureData data, int width, int height, int mipCount)
    : CPUTexture2D(data, width, height, mipCount)
{
    public override TextureFormat format => TextureFormat.RGBA4444;

    public override Color GetPixel(int x, int y, int mipLevel = 0)
    {
        int mipWidth = CPUTextureHelper.MipWidth(width, mipLevel);
        int mipHeight = CPUTextureHelper.MipHeight(height, mipLevel);
        int mipOffset = CPUTextureHelper.UncompressedMipOffset(width, height, mipLevel, 2);
        int pixelIndex = CPUTextureHelper.PixelIndex(x, y, mipWidth, mipHeight);
        int byteOffset = mipOffset + (pixelIndex * 2);

        int lo = data[byteOffset];
        int hi = data[byteOffset + 1];

        float r = ((hi >> 4) & 0xF) * (1f / 15f);
        float g = (hi & 0xF) * (1f / 15f);
        float b = ((lo >> 4) & 0xF) * (1f / 15f);
        float a = (lo & 0xF) * (1f / 15f);

        return new Color(r, g, b, a);
    }
}
