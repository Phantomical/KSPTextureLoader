using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

internal sealed class CPUTexture2D_Texture : CPUTexture2D
{
    TextureHandle<Texture2D> handle;
    Texture2D texture;

    public override int Width => texture.width;

    public override int Height => texture.height;

    public override int MipCount => texture.mipmapCount;

    public override TextureFormat Format => texture.format;

    public CPUTexture2D_Texture(TextureHandle<Texture2D> handle)
    {
        using (handle)
        {
            this.texture = handle.GetTexture();
            this.handle = handle.Acquire();
        }
    }

    public override Color GetPixel(int x, int y, int mipLevel = 0) =>
        texture.GetPixel(x, y, mipLevel);

    public override Color32 GetPixel32(int x, int y, int mipLevel = 0) =>
        texture.GetPixel(x, y, mipLevel);

    public override Color GetPixelBilinear(float u, float v, int mipLevel = 0) =>
        texture.GetPixelBilinear(u, v, mipLevel);

    public override NativeArray<byte> GetRawTextureData() => texture.GetRawTextureData<byte>();

    public override Texture2D CompileToTexture(bool readable = false) =>
        CloneReadableTexture(texture, readable);

    public override void Dispose()
    {
        handle?.Dispose();

        handle = null;
        texture = null;
    }
}
