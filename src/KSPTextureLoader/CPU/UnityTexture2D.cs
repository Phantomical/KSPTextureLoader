using System;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader.CPU;

internal class UnityTexture2D : CPUTexture2D
{
    Texture2D texture;
    readonly bool owned;

    public UnityTexture2D(Texture2D texture, bool owned = true)
        : base()
    {
        if (texture == null)
            throw new ArgumentNullException(nameof(texture));
        if (!texture.isReadable)
            throw new Exception($"texture {texture.name} is not readable");

        this.texture = texture;
        this.owned = owned;
    }

    public override int Width => texture.width;

    public override int Height => texture.height;

    public override int MipCount => texture.mipmapCount;

    public override TextureFormat Format => texture.format;

    public override Color GetPixel(int x, int y, int mipLevel = 0) =>
        texture.GetPixel(x, y, mipLevel);

    public override Color32 GetPixel32(int x, int y, int mipLevel = 0) => GetPixel(x, y, mipLevel);

    public override Color GetPixelBilinear(float u, float v, int mipLevel = 0) =>
        texture.GetPixelBilinear(u, v, mipLevel);

    public override NativeArray<byte> GetRawTextureData() => texture.GetRawTextureData<byte>();

    public override Texture2D CompileToTexture(bool readable = false) =>
        CloneReadableTexture(texture, readable);

    public override void Dispose()
    {
        base.Dispose();
        if (owned)
            Texture2D.Destroy(texture);

        texture = null;
    }

    internal readonly struct Factory(Texture2D unity, bool owned = true) : ICPUTexture2DFactory
    {
        public CPUTexture2D CreateTexture2D<T>(T texture)
            where T : ICPUTexture2D
        {
            return new UnityTexture2D<T>(texture, unity, owned);
        }

        public CPUTexture2D CreateFallback()
        {
            return new UnityTexture2D(unity, owned);
        }
    }
}

internal class UnityTexture2D<T>(T texture, Texture2D unity, bool owned = true)
    : CPUTexture2D<T>(texture)
    where T : ICPUTexture2D
{
    Texture2D unity = unity;
    readonly bool owned = owned;

    public override Texture2D CompileToTexture(bool readable = false) =>
        CloneReadableTexture(unity, readable);

    public override void Dispose()
    {
        base.Dispose();

        if (owned)
            Texture2D.Destroy(unity);

        unity = null;

        GC.SuppressFinalize(this);
    }

    ~UnityTexture2D()
    {
        if (!owned || unity is null)
            return;

        TextureLoader.Instance?.ExecuteOnMainThread(() => Texture2D.Destroy(unity));
    }
}
