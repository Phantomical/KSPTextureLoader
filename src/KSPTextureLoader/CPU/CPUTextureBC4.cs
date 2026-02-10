using UnityEngine;

namespace KSPTextureLoader.CPU;

internal sealed class CPUTextureBC4(CPUTextureData data, int width, int height, int mipCount)
    : CPUTexture2D(data, width, height, mipCount)
{
    public override TextureFormat format => TextureFormat.BC4;

    public override Color GetPixel(int x, int y, int mipLevel = 0)
    {
        int mw = width >> mipLevel;
        int mh = height >> mipLevel;

        x = Mathf.Clamp(x, 0, mw - 1);
        y = Mathf.Clamp(y, 0, mh - 1);

        int mipOffset = CPUTextureHelper.BlockCompressedMipOffset(width, height, mipLevel, 8);
        BCDecoder.BlockCoords(x, y, mw, out int blockOffset, out int lx, out int ly, 8);

        float r = BCDecoder.DecodeBC4Block(data, mipOffset + blockOffset, lx, ly);

        return new Color(r, 1f, 1f, 1f);
    }
}
