using UnityEngine;

namespace KSPTextureLoader.CPU;

internal sealed class CPUTextureR16(CPUTextureData data, int width, int height, int mipCount)
    : CPUTexture2D(data, width, height, mipCount)
{
    public override TextureFormat format => TextureFormat.R16;

    public override Color GetPixel(int x, int y, int mipLevel = 0)
    {
        int mipWidth = CPUTextureHelper.MipWidth(width, mipLevel);
        int mipHeight = CPUTextureHelper.MipHeight(height, mipLevel);
        int mipOffset = CPUTextureHelper.UncompressedMipOffset(width, height, mipLevel, 2);
        int pixelIndex = CPUTextureHelper.PixelIndex(x, y, mipWidth, mipHeight);
        int byteOffset = mipOffset + (pixelIndex * 2);

        ushort rawValue = CPUTextureHelper.ReadUInt16(data, byteOffset);
        float v = rawValue * CPUTextureHelper.UShort2Float;

        return new Color(v, 1f, 1f, 1f);
    }
}
