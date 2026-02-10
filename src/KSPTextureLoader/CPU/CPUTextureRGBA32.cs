using UnityEngine;

namespace KSPTextureLoader.CPU;

internal sealed class CPUTextureRGBA32(CPUTextureData data, int width, int height, int mipCount)
    : CPUTexture2D(data, width, height, mipCount)
{
    public override TextureFormat format => TextureFormat.RGBA32;

    public override Color GetPixel(int x, int y, int mipLevel = 0) => GetPixel32(x, y, mipLevel);

    public override Color32 GetPixel32(int x, int y, int mipLevel = 0)
    {
        if (mipLevel < 0 || mipLevel >= mipCount)
            throw new System.ArgumentOutOfRangeException(nameof(mipLevel));

        int mipW = CPUTextureHelper.MipWidth(width, mipLevel);
        int mipH = CPUTextureHelper.MipHeight(height, mipLevel);

        int pixelIndex = CPUTextureHelper.PixelIndex(x, y, mipW, mipH);
        int byteOffset =
            CPUTextureHelper.UncompressedMipOffset(width, height, mipLevel, 4) + (pixelIndex * 4);

        return new Color32(
            data[byteOffset],
            data[byteOffset + 1],
            data[byteOffset + 2],
            data[byteOffset + 3]
        );
    }
}
