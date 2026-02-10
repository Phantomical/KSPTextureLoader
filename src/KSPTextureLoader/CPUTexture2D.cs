using System;
using System.Runtime.CompilerServices;
using KSPTextureLoader.CPU;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

/// <summary>
/// A texture that is only loaded into memory, but not into VRAM if possible.
/// </summary>
///
/// <remarks>
/// Depending on the texture source the texture data will either be a memory map
/// of the actual file on disk, or the texture data for a <see cref="Texture2D" />
/// loaded from an asset bundle.
/// </remarks>
public abstract class CPUTexture2D : IDisposable
{
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
}
