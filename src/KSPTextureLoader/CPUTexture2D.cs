using System;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.CompilerServices;
using KSPTextureLoader.Burst;
using KSPTextureLoader.Utils;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

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

    public NativeArray<Color> GetPixels(int mipLevel = 0, Allocator allocator = Allocator.Temp);
    public NativeArray<Color32> GetPixels32(int mipLevel = 0, Allocator allocator = Allocator.Temp);

    public NativeArray<T> GetRawTextureData<T>()
        where T : unmanaged;
}

/// <summary>
/// Interfaces for a type implementing <see cref="ICPUTexture2D"/> that wants
/// to override how it is compiled to a texture.
/// </summary>
public interface ICompileToTexture
{
    public Texture2D CompileToTexture(bool readable);
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
[BurstCompile]
public abstract partial class CPUTexture2D : ICPUTexture2D, ICompileToTexture, IDisposable
{
    protected const float Byte2Float = 1f / 255f;
    protected const float UShort2Float = 1f / 65535f;
    protected const float Float2Byte = 255f;
    protected const float Float2Ushort = 65535f;

    public abstract int Width { get; }
    public abstract int Height { get; }
    public abstract int MipCount { get; }
    public abstract TextureFormat Format { get; }

    protected CPUTexture2D() { }

    public abstract Color GetPixel(int x, int y, int mipLevel = 0);

    public abstract Color32 GetPixel32(int x, int y, int mipLevel = 0);

    public abstract Color GetPixelBilinear(float u, float v, int mipLevel = 0);

    public abstract NativeArray<byte> GetRawTextureData();

    public abstract NativeArray<Color> GetPixels(
        int mipLevel = 0,
        Allocator allocator = Allocator.Temp
    );

    public abstract NativeArray<Color32> GetPixels32(
        int mipLevel = 0,
        Allocator allocator = Allocator.Temp
    );

    public NativeArray<T> GetRawTextureData<T>()
        where T : unmanaged => GetRawTextureData().Reinterpret<T>(sizeof(byte));

    public virtual Texture2D CompileToTexture(bool readable = false)
    {
        var texture = TextureUtils.CreateUninitializedTexture2D(
            Width,
            Height,
            MipCount,
            GraphicsFormatUtility.GetGraphicsFormat(Format, false)
        );
        texture.LoadRawTextureData(GetRawTextureData<byte>());
        texture.Apply(false, !readable);
        return texture;
    }

    public virtual void Dispose() { }

    /// <summary>
    /// Create a <see cref="CPUTexture2D"/> that wraps a type that implements
    /// <see cref="ICPUTexture2D"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="texture"></param>
    /// <returns></returns>
    public static CPUTexture2D Create<T>(T texture)
        where T : ICPUTexture2D
    {
        return new CPUTexture2D<T>(texture);
    }

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

    static void GetBlockMipProperties(
        int width,
        int height,
        int mipLevel,
        out int mipWidth,
        out int mipHeight,
        out int blockOffset,
        out int blocksPerRow,
        out int blockCount
    )
    {
        mipWidth = width;
        mipHeight = height;
        blockOffset = 0;

        for (int m = 0; m < mipLevel; m++)
        {
            int bw = (mipWidth + 3) / 4;
            int bh = (mipHeight + 3) / 4;
            blockOffset += bw * bh;
            mipWidth = Math.Max(mipWidth >> 1, 1);
            mipHeight = Math.Max(mipHeight >> 1, 1);
        }

        blocksPerRow = (mipWidth + 3) / 4;
        blockCount = blocksPerRow * ((mipHeight + 3) / 4);
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

    /// <summary>
    /// Decodes an entire BC4 block (8 bytes packed as a ulong) into 16 float values
    /// in row-major order (4 rows of 4 pixels).
    /// </summary>
    internal static unsafe FixedArray16<float> DecodeBC4Block(ulong bits)
    {
        FixedArray16<float> output = default;

        byte r0 = (byte)(bits & 0xFF);
        byte r1 = (byte)((bits >> 8) & 0xFF);

        float fr0 = r0 * (1f / 255f);
        float fr1 = r1 * (1f / 255f);

        float* palette = stackalloc float[8];
        palette[0] = fr0;
        palette[1] = fr1;

        if (r0 > r1)
        {
            palette[2] = (6f * fr0 + 1f * fr1) * (1f / 7f);
            palette[3] = (5f * fr0 + 2f * fr1) * (1f / 7f);
            palette[4] = (4f * fr0 + 3f * fr1) * (1f / 7f);
            palette[5] = (3f * fr0 + 4f * fr1) * (1f / 7f);
            palette[6] = (2f * fr0 + 5f * fr1) * (1f / 7f);
            palette[7] = (1f * fr0 + 6f * fr1) * (1f / 7f);
        }
        else
        {
            palette[2] = (4f * fr0 + 1f * fr1) * (1f / 5f);
            palette[3] = (3f * fr0 + 2f * fr1) * (1f / 5f);
            palette[4] = (2f * fr0 + 3f * fr1) * (1f / 5f);
            palette[5] = (1f * fr0 + 4f * fr1) * (1f / 5f);
            palette[6] = 0f;
            palette[7] = 1f;
        }

        ulong indices = bits >> 16;
        for (int i = 0; i < 16; i++)
        {
            output[i] = palette[indices & 0x7];
            indices >>= 3;
        }
        return output;
    }

    /// <summary>
    /// Decodes an entire DXT1 block (8 bytes packed as a ulong) into 16 Color values
    /// in row-major order (4 rows of 4 pixels).
    /// </summary>
    internal static unsafe FixedArray16<Color> DecodeDXT1Block(ulong bits)
    {
        FixedArray16<Color> output = default;

        ushort c0Raw = (ushort)(bits & 0xFFFF);
        ushort c1Raw = (ushort)((bits >> 16) & 0xFFFF);

        float r0 = ((c0Raw >> 11) & 0x1F) * (1f / 31f);
        float g0 = ((c0Raw >> 5) & 0x3F) * (1f / 63f);
        float b0 = (c0Raw & 0x1F) * (1f / 31f);

        float r1 = ((c1Raw >> 11) & 0x1F) * (1f / 31f);
        float g1 = ((c1Raw >> 5) & 0x3F) * (1f / 63f);
        float b1 = (c1Raw & 0x1F) * (1f / 31f);

        Color* palette = stackalloc Color[4];
        palette[0] = new Color(r0, g0, b0, 1f);
        palette[1] = new Color(r1, g1, b1, 1f);

        if (c0Raw > c1Raw)
        {
            palette[2] = new Color(
                (2f * r0 + r1) * (1f / 3f),
                (2f * g0 + g1) * (1f / 3f),
                (2f * b0 + b1) * (1f / 3f),
                1f
            );
            palette[3] = new Color(
                (r0 + 2f * r1) * (1f / 3f),
                (g0 + 2f * g1) * (1f / 3f),
                (b0 + 2f * b1) * (1f / 3f),
                1f
            );
        }
        else
        {
            palette[2] = new Color((r0 + r1) * 0.5f, (g0 + g1) * 0.5f, (b0 + b1) * 0.5f, 1f);
            palette[3] = new Color(0f, 0f, 0f, 0f);
        }

        uint indices = (uint)(bits >> 32);
        for (int i = 0; i < 16; i++)
        {
            output[i] = palette[indices & 0x3];
            indices >>= 2;
        }

        return output;
    }

    private protected static Texture2D CloneReadableTexture(Texture2D src, bool readable = false)
    {
        if (SystemInfo.copyTextureSupport.HasFlag(CopyTextureSupport.Basic))
        {
            var flags = readable
                ? default
                : TextureUtils.InternalTextureCreationFlags.DontCreateSharedTextureData;
            var copy = TextureUtils.CreateUninitializedTexture2D(
                src.width,
                src.height,
                src.mipmapCount,
                src.graphicsFormat,
                flags
            );

            if (!readable)
                TextureUtils.MarkExternalTextureAsUnreadable(copy);

            Graphics.CopyTexture(src, copy);
            return copy;
        }
        else
        {
            var copy = Texture2D.Instantiate(src);
            if (!readable)
                copy.Apply(false, true);
            return copy;
        }
    }

    static NativeArray<Color> GetPixels<TTex, TData, TJob>(
        in TTex texture,
        int mipLevel,
        Allocator allocator,
        Func<NativeArray<TData>, NativeArray<Color>, TJob> jobFunc
    )
        where TTex : ICPUTexture2D
        where TJob : struct, IJobParallelForBatch
        where TData : unmanaged
    {
        var info = GetMipProperties(in texture, mipLevel);
        var pixels = new NativeArray<Color>(
            info.width * info.height,
            allocator,
            NativeArrayOptions.UninitializedMemory
        );

        try
        {
            var data = texture.GetRawTextureData<TData>().GetSubArray(info.offset, pixels.Length);
            var job = jobFunc(data, pixels);

            if (pixels.Length < 16384)
                job.RunBatch(pixels.Length, 4096);
            else
                job.ScheduleBatch(pixels.Length, 4096).Complete();

            return pixels;
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }

    static NativeArray<Color32> GetPixels32<TTex, TData, TJob>(
        in TTex texture,
        int mipLevel,
        Allocator allocator,
        Func<NativeArray<TData>, NativeArray<Color32>, TJob> jobFunc
    )
        where TTex : ICPUTexture2D
        where TJob : struct, IJobParallelForBatch
        where TData : unmanaged
    {
        var info = GetMipProperties(in texture, mipLevel);
        var pixels = new NativeArray<Color32>(
            info.width * info.height,
            allocator,
            NativeArrayOptions.UninitializedMemory
        );

        try
        {
            var data = texture.GetRawTextureData<TData>().GetSubArray(info.offset, pixels.Length);
            var job = jobFunc(data, pixels);

            if (pixels.Length < 16384)
                job.RunBatch(pixels.Length, 4096);
            else
                job.ScheduleBatch(pixels.Length, 4096).Complete();

            return pixels;
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }

    static NativeArray<Color> GetBlockPixels<TTex, TBlock, TJob>(
        in TTex texture,
        int mipLevel,
        Allocator allocator,
        Func<NativeArray<TBlock>, TJob> func
    )
        where TTex : ICPUTexture2D
        where TBlock : unmanaged
        where TJob : struct, IGetPixelsBlockJob
    {
        GetBlockMipProperties(
            texture.Width,
            texture.Height,
            mipLevel,
            out int mipWidth,
            out int mipHeight,
            out int blockOffset,
            out int blocksPerRow,
            out int blockCount
        );

        var pixels = new NativeArray<Color>(
            mipWidth * mipHeight,
            allocator,
            NativeArrayOptions.UninitializedMemory
        );

        var blocks = texture.GetRawTextureData<TBlock>().GetSubArray(blockOffset, blockCount);
        var job = func(blocks);

        job.Schedule(blocksPerRow, mipWidth, mipHeight, pixels).Complete();
        return pixels;
    }

    static NativeArray<Color32> GetBlockPixels32<TTex, TBlock, TJob>(
        in TTex texture,
        int mipLevel,
        Allocator allocator,
        Func<NativeArray<TBlock>, TJob> func
    )
        where TTex : ICPUTexture2D
        where TBlock : unmanaged
        where TJob : struct, IGetPixelsBlockJob
    {
        GetBlockMipProperties(
            texture.Width,
            texture.Height,
            mipLevel,
            out int mipWidth,
            out int mipHeight,
            out int blockOffset,
            out int blocksPerRow,
            out int blockCount
        );

        var pixels = new NativeArray<Color32>(
            mipWidth * mipHeight,
            allocator,
            NativeArrayOptions.UninitializedMemory
        );

        var blocks = texture.GetRawTextureData<TBlock>().GetSubArray(blockOffset, blockCount);
        var job = func(blocks);

        job.Schedule(blocksPerRow, mipWidth, mipHeight, pixels).Complete();
        return pixels;
    }
    #endregion
}

/// <summary>
/// A <see cref="CPUTexture2D"/> that wraps an existing implementation of
/// <see cref="ICPUTexture2D"/>.
/// </summary>
///
/// <remarks>
/// This is meant to allow you to easily implement <see cref="CPUTexture2D"/>
/// variants that acquire the texture data from some other object. It takes
/// care of implementing all the necessary methods, while allowing you to
/// override <see cref="Dispose"/> for your own data.
/// </remarks>
public class CPUTexture2D<TTexture>(TTexture texture) : CPUTexture2D
    where TTexture : ICPUTexture2D
{
    TTexture texture = texture;

    public sealed override int Width => texture.Width;
    public sealed override int Height => texture.Height;
    public sealed override int MipCount => texture.MipCount;
    public sealed override TextureFormat Format => texture.Format;
    public TTexture Texture => texture;

    public sealed override Color GetPixel(int x, int y, int mipLevel = 0) =>
        texture.GetPixel(x, y, mipLevel);

    public sealed override Color32 GetPixel32(int x, int y, int mipLevel = 0) =>
        texture.GetPixel32(x, y, mipLevel);

    public sealed override Color GetPixelBilinear(float u, float v, int mipLevel = 0) =>
        texture.GetPixelBilinear(u, v, mipLevel);

    public sealed override NativeArray<byte> GetRawTextureData() =>
        texture.GetRawTextureData<byte>();

    public sealed override NativeArray<Color> GetPixels(
        int mipLevel = 0,
        Allocator allocator = Allocator.Temp
    ) => texture.GetPixels(mipLevel, allocator);

    public sealed override NativeArray<Color32> GetPixels32(
        int mipLevel = 0,
        Allocator allocator = Allocator.Temp
    ) => texture.GetPixels32(mipLevel, allocator);

    public override Texture2D CompileToTexture(bool readable = false)
    {
        if (CompileToTextureFunc is not null)
            return CompileToTextureFunc(ref texture, readable);

        if (Format == default)
            throw new NotSupportedException(
                $"Cannot compile a texture format accessor of type {typeof(TTexture).Name} to a Texture2D"
            );

        return base.CompileToTexture();
    }

    public override void Dispose()
    {
        DisposeFunc?.Invoke(ref texture);
        texture = default;
    }

    #region CompileToTexture Helpers
    delegate Texture2D CompileToTextureDelegate(ref TTexture texture, bool readable);

    static readonly CompileToTextureDelegate CompileToTextureFunc = MakeCompileToTextureDelegate();

    static CompileToTextureDelegate MakeCompileToTextureDelegate()
    {
        if (!typeof(ICompileToTexture).IsAssignableFrom(typeof(TTexture)))
            return null;

        var method = typeof(CPUTexture2D<TTexture>)
            .GetMethod(nameof(DoCompileToTexture), BindingFlags.Static | BindingFlags.NonPublic)
            .MakeGenericMethod(typeof(TTexture));

        return (CompileToTextureDelegate)
            Delegate.CreateDelegate(typeof(CompileToTextureDelegate), method);
    }

    static Texture2D DoCompileToTexture<T>(ref T texture, bool readable)
        where T : ICompileToTexture
    {
        return texture.CompileToTexture(readable);
    }
    #endregion

    #region Dispose Helpers
    delegate void DisposeDelegate(ref TTexture texture);

    static readonly DisposeDelegate DisposeFunc = MakeDisposeDelegate();

    static DisposeDelegate MakeDisposeDelegate()
    {
        if (!typeof(IDisposable).IsAssignableFrom(typeof(TTexture)))
            return null;

        var method = typeof(CPUTexture2D<TTexture>)
            .GetMethod(nameof(DoDispose), BindingFlags.Static | BindingFlags.NonPublic)
            .MakeGenericMethod(typeof(TTexture));

        return (DisposeDelegate)Delegate.CreateDelegate(typeof(DisposeDelegate), method);
    }

    static void DoDispose<T>(ref T value)
        where T : IDisposable
    {
        value.Dispose();
    }
    #endregion
}
