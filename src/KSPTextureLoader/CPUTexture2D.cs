using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

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
    protected const float Byte2Float = 1f / 255f;
    protected const float UShort2Float = 1f / 65535f;

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
        if (handle is null)
            throw new ArgumentNullException(nameof(handle));

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
            TextureFormat.DXT1 => new CPUTexture2D_TextureHandle<DXT1>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.DXT5 => new CPUTexture2D_TextureHandle<DXT5>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.BC4 => new CPUTexture2D_TextureHandle<BC4>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.BC5 => new CPUTexture2D_TextureHandle<BC5>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.BC7 => new CPUTexture2D_TextureHandle<BC7>(
                handle,
                new(
                    texture.GetRawTextureData<byte>(),
                    texture.width,
                    texture.height,
                    texture.mipmapCount
                )
            ),
            TextureFormat.BC6H => new CPUTexture2D_TextureHandle<BC6H>(
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

    /// <summary>
    /// Create a new CPUTexture2D backed by a memory-mapped file.
    /// </summary>
    /// <param name="mmf">The memory-mapped file. Ownership is transferred to the returned texture.</param>
    /// <param name="accessor">The view accessor. Ownership is transferred to the returned texture.</param>
    /// <param name="data">A NativeArray view over the texture data region of the memory-mapped file.</param>
    /// <param name="width">The texture width in pixels.</param>
    /// <param name="height">The texture height in pixels.</param>
    /// <param name="mipCount">The number of mip levels.</param>
    /// <param name="format">The texture format.</param>
    /// <returns>A CPUTexture2D wrapping the data.</returns>
    /// <exception cref="NotSupportedException">Thrown if the format is unsupported. All parameters are disposed before throwing.</exception>
    internal static CPUTexture2D Create(
        MemoryMappedFile mmf,
        MemoryMappedViewAccessor accessor,
        NativeArray<byte> data,
        int width,
        int height,
        int mipCount,
        TextureFormat format
    )
    {
        if (mmf is null)
            throw new ArgumentNullException(nameof(mmf));
        if (accessor is null)
            throw new ArgumentNullException(nameof(accessor));

        return format switch
        {
            TextureFormat.Alpha8 => new CPUTexture2D_MemoryMapped<Alpha8>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.R8 => new CPUTexture2D_MemoryMapped<R8>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.RG16 => new CPUTexture2D_MemoryMapped<RG16>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.RGB24 => new CPUTexture2D_MemoryMapped<RGB24>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.RGBA32 => new CPUTexture2D_MemoryMapped<RGBA32>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.ARGB32 => new CPUTexture2D_MemoryMapped<ARGB32>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.BGRA32 => new CPUTexture2D_MemoryMapped<BGRA32>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.R16 => new CPUTexture2D_MemoryMapped<R16>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.RGB565 => new CPUTexture2D_MemoryMapped<RGB565>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.RGBA4444 => new CPUTexture2D_MemoryMapped<RGBA4444>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.ARGB4444 => new CPUTexture2D_MemoryMapped<ARGB4444>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.RFloat => new CPUTexture2D_MemoryMapped<RFloat>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.RGFloat => new CPUTexture2D_MemoryMapped<RGFloat>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.RGBAFloat => new CPUTexture2D_MemoryMapped<RGBAFloat>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.RHalf => new CPUTexture2D_MemoryMapped<RHalf>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.RGHalf => new CPUTexture2D_MemoryMapped<RGHalf>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.RGBAHalf => new CPUTexture2D_MemoryMapped<RGBAHalf>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.DXT1 => new CPUTexture2D_MemoryMapped<DXT1>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.DXT5 => new CPUTexture2D_MemoryMapped<DXT5>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.BC4 => new CPUTexture2D_MemoryMapped<BC4>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.BC5 => new CPUTexture2D_MemoryMapped<BC5>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.BC7 => new CPUTexture2D_MemoryMapped<BC7>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            TextureFormat.BC6H => new CPUTexture2D_MemoryMapped<BC6H>(
                mmf,
                accessor,
                new(data, width, height, mipCount)
            ),
            _ => ThrowNotSupported<CPUTexture2D>(mmf, accessor, format),
        };
    }

    static T ThrowNotSupported<T>(
        MemoryMappedFile mmf,
        MemoryMappedViewAccessor accessor,
        TextureFormat format
    )
    {
        accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
        accessor?.Dispose();
        mmf?.Dispose();
        throw new NotSupportedException(
            $"Unsupported texture format for memory-mapped CPU texture: {format}"
        );
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

    static void GetBlockIndex(
        int width,
        int height,
        int x,
        int y,
        int mipLevel,
        out int blockIndex,
        out int pixelIndex
    )
    {
        int mipWidth = width;
        int mipHeight = height;
        int blockOffset = 0;

        for (int m = 0; m < mipLevel; m++)
        {
            int bw = (mipWidth + 3) / 4;
            int bh = (mipHeight + 3) / 4;
            blockOffset += bw * bh;
            mipWidth = Math.Max(mipWidth >> 1, 1);
            mipHeight = Math.Max(mipHeight >> 1, 1);
        }

        x = Mathf.Clamp(x, 0, mipWidth - 1);
        y = Mathf.Clamp(y, 0, mipHeight - 1);

        int blocksPerRow = (mipWidth + 3) / 4;
        blockIndex = blockOffset + (y / 4) * blocksPerRow + (x / 4);
        pixelIndex = (y % 4) * 4 + (x % 4);
    }

    static int GetTotalBlockCount(int width, int height, int mipCount)
    {
        int count = 0;
        for (int m = 0; m < mipCount; m++)
        {
            int bw = (width + 3) / 4;
            int bh = (height + 3) / 4;
            count += bw * bh;
            width = Math.Max(width >> 1, 1);
            height = Math.Max(height >> 1, 1);
        }
        return count;
    }

    static Color DecodeDXT1Color(ulong bits, int pixelIndex)
    {
        ushort c0Raw = (ushort)(bits & 0xFFFF);
        ushort c1Raw = (ushort)((bits >> 16) & 0xFFFF);

        float r0 = ((c0Raw >> 11) & 0x1F) * (1f / 31f);
        float g0 = ((c0Raw >> 5) & 0x3F) * (1f / 63f);
        float b0 = (c0Raw & 0x1F) * (1f / 31f);

        float r1 = ((c1Raw >> 11) & 0x1F) * (1f / 31f);
        float g1 = ((c1Raw >> 5) & 0x3F) * (1f / 63f);
        float b1 = (c1Raw & 0x1F) * (1f / 31f);

        int localY = pixelIndex / 4;
        int localX = pixelIndex % 4;
        byte indexByte = (byte)((bits >> (32 + localY * 8)) & 0xFF);
        int index = (indexByte >> (localX * 2)) & 0x3;

        if (c0Raw > c1Raw)
        {
            // 4-color mode
            return index switch
            {
                0 => new Color(r0, g0, b0, 1f),
                1 => new Color(r1, g1, b1, 1f),
                2 => new Color(
                    (2f * r0 + r1) * (1f / 3f),
                    (2f * g0 + g1) * (1f / 3f),
                    (2f * b0 + b1) * (1f / 3f),
                    1f
                ),
                _ => new Color(
                    (r0 + 2f * r1) * (1f / 3f),
                    (g0 + 2f * g1) * (1f / 3f),
                    (b0 + 2f * b1) * (1f / 3f),
                    1f
                ),
            };
        }
        else
        {
            // 3-color + transparent-black mode
            return index switch
            {
                0 => new Color(r0, g0, b0, 1f),
                1 => new Color(r1, g1, b1, 1f),
                2 => new Color((r0 + r1) * 0.5f, (g0 + g1) * 0.5f, (b0 + b1) * 0.5f, 1f),
                _ => new Color(0f, 0f, 0f, 0f),
            };
        }
    }

    static float DecodeBC4Channel(ulong bits, int pixelIndex)
    {
        byte r0 = (byte)(bits & 0xFF);
        byte r1 = (byte)((bits >> 8) & 0xFF);

        // 48-bit index data starts at bit 16; each pixel has a 3-bit index
        int code = (int)((bits >> (16 + pixelIndex * 3)) & 0x7);

        float fr0 = r0 * (1f / 255f);
        float fr1 = r1 * (1f / 255f);

        if (r0 > r1)
        {
            return code switch
            {
                0 => fr0,
                1 => fr1,
                2 => (6f * fr0 + 1f * fr1) * (1f / 7f),
                3 => (5f * fr0 + 2f * fr1) * (1f / 7f),
                4 => (4f * fr0 + 3f * fr1) * (1f / 7f),
                5 => (3f * fr0 + 4f * fr1) * (1f / 7f),
                6 => (2f * fr0 + 5f * fr1) * (1f / 7f),
                _ => (1f * fr0 + 6f * fr1) * (1f / 7f),
            };
        }
        else
        {
            return code switch
            {
                0 => fr0,
                1 => fr1,
                2 => (4f * fr0 + 1f * fr1) * (1f / 5f),
                3 => (3f * fr0 + 2f * fr1) * (1f / 5f),
                4 => (2f * fr0 + 3f * fr1) * (1f / 5f),
                5 => (1f * fr0 + 4f * fr1) * (1f / 5f),
                6 => 0f,
                _ => 1f,
            };
        }
    }
    #endregion
}
