using System;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

internal sealed class CPUTexture2D_Wrapper : CPUTexture2D
{
    TextureHandle<Texture2D> handle;
    Texture2D texture;

    public override int Width => texture.width;

    public override int Height => texture.height;

    public override int MipCount => texture.mipmapCount;

    public override TextureFormat Format => texture.format;

    public CPUTexture2D_Wrapper(TextureHandle<Texture2D> handle)
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

    public override unsafe NativeArray<Color> GetPixels(
        int mipLevel = 0,
        Allocator allocator = Allocator.Temp
    )
    {
        if (texture.width * texture.height > int.MaxValue / sizeof(Color))
            throw new OutOfMemoryException("color array would be is too large to allocate");

        var pixels = texture.GetPixels(mipLevel);
        var native = new NativeArray<Color>(
            pixels.Length,
            allocator,
            NativeArrayOptions.UninitializedMemory
        );
        native.CopyFrom(pixels);
        return native;
    }

    public override unsafe NativeArray<Color32> GetPixels32(
        int mipLevel = 0,
        Allocator allocator = Allocator.Temp
    )
    {
        if (texture.width * texture.height > int.MaxValue / sizeof(Color32))
            throw new OutOfMemoryException("color array would be is too large to allocate");

        var pixels = texture.GetPixels32(mipLevel);
        var native = new NativeArray<Color32>(
            pixels.Length,
            allocator,
            NativeArrayOptions.UninitializedMemory
        );
        native.CopyFrom(pixels);
        return native;
    }

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
