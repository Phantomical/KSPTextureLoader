using UnityEngine;

namespace KSPTextureLoader.CPU;

internal sealed class CPUTextureDXT5(CPUTextureData data, int width, int height, int mipCount)
    : CPUTexture2D(data, width, height, mipCount)
{
    public override TextureFormat format => TextureFormat.DXT5;

    public override Color GetPixel(int x, int y, int mipLevel = 0)
    {
        int mw = width >> mipLevel;
        int mh = height >> mipLevel;

        x =
            x < 0 ? 0
            : x >= mw ? mw - 1
            : x;
        y =
            y < 0 ? 0
            : y >= mh ? mh - 1
            : y;

        int mipOffset = CPUTextureHelper.BlockCompressedMipOffset(width, height, mipLevel, 16);
        BCDecoder.BlockCoords(x, y, mw, out int blockOffset, out int lx, out int ly, 16);

        float a = BCDecoder.DecodeBC4Block(data, mipOffset + blockOffset, lx, ly);
        BCDecoder.DecodeDXT1Pixel(
            data,
            mipOffset + blockOffset + 8,
            lx,
            ly,
            out float r,
            out float g,
            out float b,
            out _
        );

        return new Color(r, g, b, a);
    }
}
