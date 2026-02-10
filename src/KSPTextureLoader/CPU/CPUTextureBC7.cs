using UnityEngine;

namespace KSPTextureLoader.CPU;

internal sealed class CPUTextureBC7(CPUTextureData data, int width, int height, int mipCount)
    : CPUTexture2D(data, width, height, mipCount)
{
    public override TextureFormat format => TextureFormat.BC7;

    public override Color GetPixel(int x, int y, int mipLevel = 0)
    {
        int mw = CPUTextureHelper.MipWidth(width, mipLevel);
        int mh = CPUTextureHelper.MipHeight(height, mipLevel);

        x = CPUTextureHelper.Clamp(x, 0, mw - 1);
        y = CPUTextureHelper.Clamp(y, 0, mh - 1);

        int mipOffset = CPUTextureHelper.BlockCompressedMipOffset(width, height, mipLevel, 16);
        BCDecoder.BlockCoords(x, y, mw, out int blockOffset, out int lx, out int ly, 16);

        BCDecoder.DecodeBC7Pixel(
            data,
            mipOffset + blockOffset,
            lx,
            ly,
            out float r,
            out float g,
            out float b,
            out float a
        );

        return new Color(r, g, b, a);
    }
}
