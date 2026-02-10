using UnityEngine;

namespace KSPTextureLoader.CPU;

internal sealed class CPUTextureRGBAHalf(CPUTextureData data, int width, int height, int mipCount)
    : CPUTexture2D(data, width, height, mipCount)
{
    public override TextureFormat format => TextureFormat.RGBAHalf;

    public override Color GetPixel(int x, int y, int mipLevel = 0)
    {
        int mipWidth = CPUTextureHelper.MipWidth(width, mipLevel);
        int mipHeight = CPUTextureHelper.MipHeight(height, mipLevel);
        int mipOffset = CPUTextureHelper.UncompressedMipOffset(width, height, mipLevel, 8);
        int pixelIndex = CPUTextureHelper.PixelIndex(x, y, mipWidth, mipHeight);
        int byteOffset = mipOffset + pixelIndex;

        float r = CPUTextureHelper.ReadHalf(data, byteOffset);
        float g = CPUTextureHelper.ReadHalf(data, byteOffset + 2);
        float b = CPUTextureHelper.ReadHalf(data, byteOffset + 4);
        float a = CPUTextureHelper.ReadHalf(data, byteOffset + 6);

        return new Color(r, g, b, a);
    }
}
