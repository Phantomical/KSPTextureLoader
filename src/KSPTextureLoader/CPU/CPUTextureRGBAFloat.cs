using UnityEngine;

namespace KSPTextureLoader.CPU;

internal sealed class CPUTextureRGBAFloat(CPUTextureData data, int width, int height, int mipCount)
    : CPUTexture2D(data, width, height, mipCount)
{
    public override TextureFormat format => TextureFormat.RGBAFloat;

    public override Color GetPixel(int x, int y, int mipLevel = 0)
    {
        int mipWidth = CPUTextureHelper.MipWidth(width, mipLevel);
        int mipHeight = CPUTextureHelper.MipHeight(height, mipLevel);
        int mipOffset = CPUTextureHelper.UncompressedMipOffset(width, height, mipLevel, 16);
        int pixelIndex = CPUTextureHelper.PixelIndex(x, y, mipWidth, mipHeight);
        int byteOffset = mipOffset + pixelIndex * 16;

        float r = CPUTextureHelper.ReadSingle(data, byteOffset);
        float g = CPUTextureHelper.ReadSingle(data, byteOffset + 4);
        float b = CPUTextureHelper.ReadSingle(data, byteOffset + 8);
        float a = CPUTextureHelper.ReadSingle(data, byteOffset + 12);

        return new Color(r, g, b, a);
    }
}
