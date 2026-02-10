using UnityEngine;

namespace KSPTextureLoader.CPU;

/// <summary>
/// A CPU texture with RGB565 format (2 bytes per pixel: 5 bits R, 6 bits G, 5 bits B).
/// </summary>
internal sealed class CPUTextureRGB565(CPUTextureData data, int width, int height, int mipCount)
    : CPUTexture2D(data, width, height, mipCount)
{
    public override TextureFormat format => TextureFormat.RGB565;

    public override Color GetPixel(int x, int y, int mipLevel = 0)
    {
        int mipW = CPUTextureHelper.MipWidth(width, mipLevel);
        int mipH = CPUTextureHelper.MipHeight(height, mipLevel);
        int pixelIndex = CPUTextureHelper.PixelIndex(x, y, mipW, mipH);
        int byteOffset =
            CPUTextureHelper.UncompressedMipOffset(width, height, mipLevel, 2) + pixelIndex * 2;

        ushort pixel = CPUTextureHelper.ReadUInt16(data, byteOffset);

        float r = ((pixel >> 11) & 0x1F) * (1f / 31f);
        float g = ((pixel >> 5) & 0x3F) * (1f / 63f);
        float b = (pixel & 0x1F) * (1f / 31f);

        return new Color(r, g, b, 1f);
    }
}
