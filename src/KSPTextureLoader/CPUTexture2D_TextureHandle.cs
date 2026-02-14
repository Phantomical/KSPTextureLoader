using KSPTextureLoader;
using UnityEngine;

internal sealed class CPUTexture2D_TextureHandle<TTexture>(
    TextureHandle<Texture2D> handle,
    TTexture texture
) : CPUTexture2D<TTexture>(texture)
    where TTexture : ICPUTexture2D
{
    TextureHandle<Texture2D> handle = handle;

    public override void Dispose()
    {
        base.Dispose();

        handle?.Dispose();
        handle = null;
    }
}
