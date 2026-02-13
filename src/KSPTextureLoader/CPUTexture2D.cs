using System;
using System.Runtime.CompilerServices;
using KSPTextureLoader.CPU;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace KSPTextureLoader;

public interface ICPUTexture2D
{
    public int Width { get; }
    public int Height { get; }
    public int MipCount { get; }
    public TextureFormat Format { get; }

    public Color GetPixel(int x, int y, int mipLevel = 0);
    public Color32 GetPixel32(int x, int y, int mipLevel = 0);
    public Color GetPixelBilinear(float u, float v, int mipLevel = 0);

    public NativeArray<T> GetRawTextureData<T>()
        where T : unmanaged;
}

/// <summary>
/// A texture that is only loaded into memory, but not into VRAM if possible.
/// </summary>
///
/// <remarks>
/// Depending on the texture source the texture data will either be a memory map
/// of the actual file on disk, or the texture data for a <see cref="Texture2D" />
/// loaded from an asset bundle.
/// </remarks>
public abstract partial class CPUTexture2D : ICPUTexture2D, IDisposable
{
    const float Byte2Float = 1f / 255f;
    const float Float2Byte = 255f;

    public abstract int Width { get; }
    public abstract int Height { get; }
    public abstract int MipCount { get; }
    public abstract TextureFormat Format { get; }

    private protected CPUTexture2D() { }

    public abstract Color GetPixel(int x, int y, int mipLevel = 0);

    public abstract Color32 GetPixel32(int x, int y, int mipLevel = 0);

    public abstract Color GetPixelBilinear(float u, float v, int mipLevel = 0);

    public abstract NativeArray<byte> GetRawTextureData();

    public NativeArray<T> GetRawTextureData<T>()
        where T : unmanaged => GetRawTextureData().Reinterpret<T>(sizeof(byte));

    public virtual void Dispose() { }

    /// <summary>
    /// Create a new CPUTexture2D that wraps an existing texture handle.
    /// </summary>
    /// <param name="handle"></param>
    /// <returns></returns>
    public static CPUTexture2D Create(TextureHandle<Texture2D> handle)
    {
        var texture = handle.GetTexture();
        if (!texture.isReadable)
            throw new Exception($"texture {texture.name} is not readable");

        return texture.format switch
        {
            TextureFormat.Alpha8 => new CPUTexture2D_TextureHandle<Alpha8>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.R8 => new CPUTexture2D_TextureHandle<R8>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.RG16 => new CPUTexture2D_TextureHandle<RG16>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.RGB24 => new CPUTexture2D_TextureHandle<RGB24>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.RGBA32 => new CPUTexture2D_TextureHandle<RGBA32>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.ARGB32 => new CPUTexture2D_TextureHandle<ARGB32>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.BGRA32 => new CPUTexture2D_TextureHandle<BGRA32>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.R16 => new CPUTexture2D_TextureHandle<R16>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.RGB565 => new CPUTexture2D_TextureHandle<RGB565>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.RGBA4444 => new CPUTexture2D_TextureHandle<RGBA4444>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.ARGB4444 => new CPUTexture2D_TextureHandle<ARGB4444>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.RFloat => new CPUTexture2D_TextureHandle<RFloat>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.RGFloat => new CPUTexture2D_TextureHandle<RGFloat>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.RGBAFloat => new CPUTexture2D_TextureHandle<RGBAFloat>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.RHalf => new CPUTexture2D_TextureHandle<RHalf>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.RGHalf => new CPUTexture2D_TextureHandle<RGHalf>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.RGBAHalf => new CPUTexture2D_TextureHandle<RGBAHalf>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            _ => new CPUTexture2D_Texture(handle),
        };
    }

    #region Internal Helpers
    struct MipProperties
    {
        public int width;
        public int height;
        public int offset;
    }

    private static MipProperties GetMipProperties<T>(in T tex, int mip)
        where T : ICPUTexture2D
    {
        MipProperties p = new()
        {
            width = tex.Width,
            height = tex.Height,
            offset = 0,
        };

        if (mip > tex.MipCount)
            ThrowMipCountOutOfRange(in tex, mip);

        while (mip > 0)
        {
            p.offset += p.width * p.height;
            p.width = Math.Max(p.width >> 1, 1);
            p.height = Math.Max(p.height >> 1, 1);
        }

        return p;
    }

    /// <summary>
    /// Gets the total texture size, in elements. This is not correct for
    /// compressed block formats.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="tex"></param>
    /// <returns></returns>
    private static int GetTotalSize<T>(in T tex)
        where T : ICPUTexture2D
    {
        int w = tex.Width;
        int h = tex.Height;
        int size = 0;

        for (int m = tex.MipCount; m > 0; --m)
        {
            size += w * h;
            w = Mathf.Max(w >> 1, 1);
            h = Mathf.Max(h >> 1, 1);
        }

        return size;
    }

    static void ThrowMipCountOutOfRange<T>(in T tex, int mip)
        where T : ICPUTexture2D
    {
        throw new IndexOutOfRangeException($"mip index {mip} is out of range");
    }

    static Color GetPixelBilinear<T>(in T tex, float u, float v, int mipLevel = 0)
        where T : ICPUTexture2D
    {
        // Clamp wrap mode
        u = Mathf.Clamp01(u);
        v = Mathf.Clamp01(v);

        int w = Math.Max(1, tex.Width >> mipLevel);
        int h = Math.Max(1, tex.Height >> mipLevel);

        // Map UV to continuous pixel coordinates
        // Pixel centers are at (i + 0.5) / dimension, so offset by -0.5
        float x = u * w - 0.5f;
        float y = v * h - 0.5f;

        int x0 = Mathf.FloorToInt(x);
        int y0 = Mathf.FloorToInt(y);
        int x1 = x0 + 1;
        int y1 = y0 + 1;

        float fx = x - x0;
        float fy = y - y0;

        // Clamp to valid pixel range
        x0 = Mathf.Clamp(x0, 0, w - 1);
        x1 = Mathf.Clamp(x1, 0, w - 1);
        y0 = Mathf.Clamp(y0, 0, h - 1);
        y1 = Mathf.Clamp(y1, 0, h - 1);

        Color c00 = tex.GetPixel(x0, y0, mipLevel);
        Color c10 = tex.GetPixel(x1, y0, mipLevel);
        Color c01 = tex.GetPixel(x0, y1, mipLevel);
        Color c11 = tex.GetPixel(x1, y1, mipLevel);

        return Color.Lerp(Color.Lerp(c00, c10, fx), Color.Lerp(c01, c11, fx), fy);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe NativeArray<T> GetNonOwningNativeArray<T>(NativeArray<T> array)
        where T : unmanaged
    {
        return NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(
            NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(array),
            array.Length,
            Allocator.Invalid
        );
    }
    #endregion
}

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

    public override void Dispose()
    {
        handle?.Dispose();

        handle = null;
        texture = null;
    }
}

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
