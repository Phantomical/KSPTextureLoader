using KSPTextureLoader;
using Unity.Collections;
using UnityEngine;

internal sealed class CPUTexture2D_TextureHandle<TTexture>(
    TextureHandle<Texture2D> handle,
    TTexture texture
) : CPUTexture2D
    where TTexture : ICPUTexture2D
{
    TextureHandle<Texture2D> handle = handle;
    TTexture texture = texture;

    public override int Width => texture.Width;
    public override int Height => texture.Height;
    public override int MipCount => texture.MipCount;
    public override TextureFormat Format => texture.Format;

    public override Color GetPixel(int x, int y, int mipLevel = 0) =>
        texture.GetPixel(x, y, mipLevel);

    public override Color32 GetPixel32(int x, int y, int mipLevel = 0) =>
        texture.GetPixel32(x, y, mipLevel);

    public override Color GetPixelBilinear(float u, float v, int mipLevel = 0) =>
        texture.GetPixelBilinear(u, v, mipLevel);

    public override NativeArray<byte> GetRawTextureData() => texture.GetRawTextureData<byte>();

    public override void Dispose()
    {
        handle?.Dispose();

        handle = null;
        texture = default;
    }
}
