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
public abstract partial class CPUTexture2D : IDisposable
{
    const float Byte2Float = 1f / 255f;
    const float Float2Byte = 255f;

    private CPUTextureData textureData;

    public int width { get; protected set; }
    public int height { get; protected set; }
    public int mipCount { get; protected set; }
    public abstract TextureFormat format { get; }

    protected NativeArray<byte> data;

    private protected CPUTexture2D(CPUTextureData data, int width, int height, int mipCount)
    {
        this.textureData = data;
        this.width = width;
        this.height = height;
        this.mipCount = mipCount;
    }

    public abstract Color GetPixel(int x, int y, int mipLevel = 0);

    public virtual Color32 GetPixel32(int x, int y, int mipLevel = 0) => GetPixel(x, y, mipLevel);

    public virtual Color GetPixelBilinear(float u, float v, int mipLevel = 0)
    {
        if (mipLevel < 0 || mipLevel >= mipCount)
            throw new ArgumentOutOfRangeException(nameof(mipLevel));

        u = Clamp(u, 0f, 1f);
        v = Clamp(v, 0f, 1f);

        var width = Math.Max(1, this.width >> mipLevel);
        var height = Math.Max(1, this.height >> mipLevel);

        var c = ConstructBilinearCoords(u, v, width, height);

        return Color.Lerp(
            Color.Lerp(
                GetPixel(c.minU, c.minV, mipLevel),
                GetPixel(c.maxU, c.minV, mipLevel),
                c.midU
            ),
            Color.Lerp(
                GetPixel(c.minU, c.maxV, mipLevel),
                GetPixel(c.maxU, c.maxV, mipLevel),
                c.midU
            ),
            c.midV
        );
    }

    public virtual void Dispose() { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Clamp(float x, float min, float max)
    {
        if (x < min)
            return min;
        if (x > max)
            return max;
        return x;
    }

    protected struct BilinearCoords
    {
        public int minU;
        public int maxU;
        public float midU;

        public int minV;
        public int maxV;
        public float midV;
    }

    protected static BilinearCoords ConstructBilinearCoords(float u, float v, int width, int height)
    {
        BilinearCoords coords;

        var centerU = u * width;
        var centerV = v * height;

        coords.minU = Mathf.FloorToInt(centerU);
        coords.maxU = Mathf.CeilToInt(centerU);

        coords.minV = Mathf.FloorToInt(centerV);
        coords.maxV = Mathf.CeilToInt(centerV);

        coords.midU = centerU - coords.minU;
        coords.midV = centerV - coords.minV;

        if (coords.maxU == width)
            coords.maxU = 0;
        if (coords.maxV == height)
            coords.maxV = 0;

        return coords;
    }

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
}
