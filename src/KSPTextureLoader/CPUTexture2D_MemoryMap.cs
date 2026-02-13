using System.IO.MemoryMappedFiles;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

internal sealed class CPUTexture2D_MemoryMapped<TTexture> : CPUTexture2D
    where TTexture : ICPUTexture2D
{
    MemoryMappedFile mmf;
    MemoryMappedViewAccessor accessor;
    TTexture texture;

    public override int Width => texture.Width;
    public override int Height => texture.Height;
    public override int MipCount => texture.MipCount;
    public override TextureFormat Format => texture.Format;

    internal CPUTexture2D_MemoryMapped(
        MemoryMappedFile mmf,
        MemoryMappedViewAccessor accessor,
        TTexture texture
    )
    {
        this.mmf = mmf;
        this.accessor = accessor;
        this.texture = texture;
    }

    public override Color GetPixel(int x, int y, int mipLevel = 0) =>
        texture.GetPixel(x, y, mipLevel);

    public override Color32 GetPixel32(int x, int y, int mipLevel = 0) =>
        texture.GetPixel32(x, y, mipLevel);

    public override Color GetPixelBilinear(float u, float v, int mipLevel = 0) =>
        texture.GetPixelBilinear(u, v, mipLevel);

    public override NativeArray<byte> GetRawTextureData() => texture.GetRawTextureData<byte>();

    public override void Dispose()
    {
        accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
        accessor?.Dispose();
        mmf?.Dispose();

        accessor = null;
        mmf = null;
        texture = default;
    }
}
