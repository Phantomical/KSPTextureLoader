using UnityEngine;

namespace KSPTextureLoader.CPU;

internal sealed class CPUTextureBC5(CPUTextureData data, int width, int height, int mipCount)
    : CPUTexture2D(data, width, height, mipCount)
{
    public override TextureFormat format => TextureFormat.BC5;

    public override Color GetPixel(int x, int y, int mipLevel = 0)
    {
        int mw = Mathf.Max(1, width >> mipLevel);
        int mh = Mathf.Max(1, height >> mipLevel);

        x = Mathf.Clamp(x, 0, mw - 1);
        y = Mathf.Clamp(y, 0, mh - 1);

        int mipOffset = CPUTextureHelper.BlockCompressedMipOffset(width, height, mipLevel, 16);
        BCDecoder.BlockCoords(x, y, mw, out int blockOffset, out int lx, out int ly, 16);

        float r = BCDecoder.DecodeBC4Block(data, mipOffset + blockOffset, lx, ly);
        float g = BCDecoder.DecodeBC4Block(data, mipOffset + blockOffset + 8, lx, ly);

        return new Color(r, g, 1f, 1f);
    }
}
