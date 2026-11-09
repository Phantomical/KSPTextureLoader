using KSPTextureLoader.Burst;
using KSPTextureLoader.CPU.Block;
using KSPTextureLoader.Utils;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace KSPTextureLoader.Jobs;

/// <summary>
/// One 2D image (a single mip of a single face, array element or depth slice)
/// within a block-compressed buffer, along with where its decompressed pixels go.
/// </summary>
internal struct DecompressSurface
{
    public long srcOffset;
    public long dstOffset;
    public int width;
    public int height;
    public int blocksPerRow;

    /// <summary>The global index of this surface's first block.</summary>
    public int firstBlock;
}

/// <summary>
/// Decodes a single 4x4 block into 16 pixels laid out row-major.
/// </summary>
internal interface IBlockDecoder
{
    /// <summary>The size of one compressed block, in bytes.</summary>
    int BlockSize { get; }

    /// <summary>The size of one decompressed pixel, in bytes.</summary>
    int PixelSize { get; }

    unsafe void Decode(byte* block, byte* pixels);
}

/// <summary>
/// Decompresses every block in a set of surfaces into an uncompressed buffer.
/// Each batch item is one block.
/// </summary>
///
/// <remarks>
/// The version of burst we ship does not support generic jobs, so this is not
/// a job itself. Each decoder has a concrete job below that forwards to it.
/// </remarks>
internal unsafe struct DecompressBlocks<TDecoder>
    where TDecoder : struct, IBlockDecoder
{
    /// <summary>The largest <see cref="IBlockDecoder.PixelSize"/> of any decoder.</summary>
    const int MaxPixelSize = 8;

    [ReadOnly]
    [NativeDisableContainerSafetyRestriction]
    public NativeArray<DecompressSurface> surfaces;

    [NativeDisableUnsafePtrRestriction]
    public byte* src;

    [NativeDisableUnsafePtrRestriction]
    public byte* dst;

    public TDecoder decoder;

    public void Execute(int start, int count)
    {
        int blockSize = decoder.BlockSize;
        int pixelSize = decoder.PixelSize;
        byte* pixels = stackalloc byte[16 * MaxPixelSize];

        int s = FindSurface(start);
        int end = start + count;
        for (int block = start; block < end; ++block)
        {
            while (s + 1 < surfaces.Length && surfaces[s + 1].firstBlock <= block)
                s += 1;

            var surface = surfaces[s];
            int local = block - surface.firstBlock;
            int x0 = (local % surface.blocksPerRow) * 4;
            int y0 = (local / surface.blocksPerRow) * 4;

            decoder.Decode(src + surface.srcOffset + (long)local * blockSize, pixels);

            int cols = math.min(4, surface.width - x0);
            int rows = math.min(4, surface.height - y0);
            byte* output = dst + surface.dstOffset;
            for (int row = 0; row < rows; ++row)
            {
                long offset = ((long)(y0 + row) * surface.width + x0) * pixelSize;
                UnsafeUtility.MemCpy(
                    output + offset,
                    pixels + row * 4 * pixelSize,
                    cols * pixelSize
                );
            }
        }
    }

    readonly int FindSurface(int block)
    {
        int lo = 0;
        int hi = surfaces.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (surfaces[mid].firstBlock <= block)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }
}

internal static class BlockDecodeUtil
{
    internal static byte UNormToByte(float v) => (byte)(math.saturate(v) * 255f + 0.5f);

    /// <summary>
    /// Decodes a signed BC4 block into 16 R8_SNorm values.
    /// </summary>
    internal static unsafe void DecodeBC4SNorm(ulong block, sbyte* output)
    {
        // -128 and -127 both map to -1.0.
        int r0 = math.max((int)(sbyte)(block & 0xFF), -127);
        int r1 = math.max((int)(sbyte)((block >> 8) & 0xFF), -127);

        int* palette = stackalloc int[8];
        palette[0] = r0;
        palette[1] = r1;
        if (r0 > r1)
        {
            for (int i = 1; i < 7; ++i)
                palette[i + 1] = DivRound((7 - i) * r0 + i * r1, 7);
        }
        else
        {
            for (int i = 1; i < 5; ++i)
                palette[i + 1] = DivRound((5 - i) * r0 + i * r1, 5);
            palette[6] = -127;
            palette[7] = 127;
        }

        ulong indices = block >> 16;
        for (int i = 0; i < 16; ++i)
        {
            output[i] = (sbyte)palette[(int)(indices & 0x7)];
            indices >>= 3;
        }
    }

    static int DivRound(int num, int den) =>
        num >= 0 ? (num + den / 2) / den : -((-num + den / 2) / den);

    internal static unsafe void DecodeBC4UNorm(ulong block, byte* output, int stride)
    {
        var values = BC4.DecodeBlock(block);
        for (int i = 0; i < 16; ++i)
            output[i * stride] = UNormToByte(values[i]);
    }
}

#region Decoders
/// <summary>DXT1 (BC1) to RGBA32.</summary>
internal struct DXT1Decoder : IBlockDecoder
{
    public readonly int BlockSize => 8;
    public readonly int PixelSize => 4;

    public readonly unsafe void Decode(byte* block, byte* pixels)
    {
        *(FixedArray16<Color32>*)pixels = DXT1.DecodeBlock32(*(ulong*)block);
    }
}

/// <summary>DXT3 (BC2) to RGBA32.</summary>
internal struct DXT3Decoder : IBlockDecoder
{
    public readonly int BlockSize => 16;
    public readonly int PixelSize => 4;

    public readonly unsafe void Decode(byte* block, byte* pixels)
    {
        ulong alpha = *(ulong*)block;
        var colors = (Color32*)pixels;
        *(FixedArray16<Color32>*)pixels = DXT1.DecodeBlock32(*(ulong*)(block + 8));

        for (int i = 0; i < 16; ++i)
            colors[i].a = (byte)(((alpha >> (i * 4)) & 0xF) * 17);
    }
}

/// <summary>DXT5 (BC3) to RGBA32.</summary>
internal struct DXT5Decoder : IBlockDecoder
{
    public readonly int BlockSize => 16;
    public readonly int PixelSize => 4;

    public readonly unsafe void Decode(byte* block, byte* pixels)
    {
        *(FixedArray16<Color32>*)pixels = DXT1.DecodeBlock32(*(ulong*)(block + 8));
        BlockDecodeUtil.DecodeBC4UNorm(*(ulong*)block, pixels + 3, 4);
    }
}

/// <summary>BC4 to R8.</summary>
internal struct BC4UNormDecoder : IBlockDecoder
{
    public readonly int BlockSize => 8;
    public readonly int PixelSize => 1;

    public readonly unsafe void Decode(byte* block, byte* pixels) =>
        BlockDecodeUtil.DecodeBC4UNorm(*(ulong*)block, pixels, 1);
}

/// <summary>Signed BC4 to R8_SNorm.</summary>
internal struct BC4SNormDecoder : IBlockDecoder
{
    public readonly int BlockSize => 8;
    public readonly int PixelSize => 1;

    public readonly unsafe void Decode(byte* block, byte* pixels) =>
        BlockDecodeUtil.DecodeBC4SNorm(*(ulong*)block, (sbyte*)pixels);
}

/// <summary>BC5 to RG16.</summary>
internal struct BC5UNormDecoder : IBlockDecoder
{
    public readonly int BlockSize => 16;
    public readonly int PixelSize => 2;

    public readonly unsafe void Decode(byte* block, byte* pixels)
    {
        BlockDecodeUtil.DecodeBC4UNorm(*(ulong*)block, pixels, 2);
        BlockDecodeUtil.DecodeBC4UNorm(*(ulong*)(block + 8), pixels + 1, 2);
    }
}

/// <summary>Signed BC5 to R8G8_SNorm.</summary>
internal struct BC5SNormDecoder : IBlockDecoder
{
    public readonly int BlockSize => 16;
    public readonly int PixelSize => 2;

    public readonly unsafe void Decode(byte* block, byte* pixels)
    {
        sbyte* r = stackalloc sbyte[16];
        sbyte* g = stackalloc sbyte[16];
        BlockDecodeUtil.DecodeBC4SNorm(*(ulong*)block, r);
        BlockDecodeUtil.DecodeBC4SNorm(*(ulong*)(block + 8), g);

        var output = (sbyte*)pixels;
        for (int i = 0; i < 16; ++i)
        {
            output[i * 2 + 0] = r[i];
            output[i * 2 + 1] = g[i];
        }
    }
}

/// <summary>BC6H to RGBAHalf.</summary>
internal struct BC6HDecoder : IBlockDecoder
{
    public bool signed;

    public readonly int BlockSize => 16;
    public readonly int PixelSize => 8;

    public readonly unsafe void Decode(byte* block, byte* pixels)
    {
        var colors = BC6H.DecodeBlock(*(ulong*)block, *(ulong*)(block + 8), signed);
        var output = (ushort*)pixels;
        for (int i = 0; i < 16; ++i)
        {
            var c = colors[i];
            output[i * 4 + 0] = (ushort)math.f32tof16(c.r);
            output[i * 4 + 1] = (ushort)math.f32tof16(c.g);
            output[i * 4 + 2] = (ushort)math.f32tof16(c.b);
            output[i * 4 + 3] = 0x3C00; // 1.0
        }
    }
}

/// <summary>BC7 to RGBA32.</summary>
internal struct BC7Decoder : IBlockDecoder
{
    public readonly int BlockSize => 16;
    public readonly int PixelSize => 4;

    public readonly unsafe void Decode(byte* block, byte* pixels)
    {
        *(FixedArray16<Color32>*)pixels = BC7.DecodeBlock32(*(ulong*)block, *(ulong*)(block + 8));
    }
}
#endregion

#region Jobs
[BurstCompile(FloatMode = FloatMode.Fast)]
internal struct DecompressDXT1Job : IJobParallelForBatch
{
    public DecompressBlocks<DXT1Decoder> inner;

    public void Execute(int start, int count) => inner.Execute(start, count);
}

[BurstCompile(FloatMode = FloatMode.Fast)]
internal struct DecompressDXT3Job : IJobParallelForBatch
{
    public DecompressBlocks<DXT3Decoder> inner;

    public void Execute(int start, int count) => inner.Execute(start, count);
}

[BurstCompile(FloatMode = FloatMode.Fast)]
internal struct DecompressDXT5Job : IJobParallelForBatch
{
    public DecompressBlocks<DXT5Decoder> inner;

    public void Execute(int start, int count) => inner.Execute(start, count);
}

[BurstCompile(FloatMode = FloatMode.Fast)]
internal struct DecompressBC4UNormJob : IJobParallelForBatch
{
    public DecompressBlocks<BC4UNormDecoder> inner;

    public void Execute(int start, int count) => inner.Execute(start, count);
}

[BurstCompile(FloatMode = FloatMode.Fast)]
internal struct DecompressBC4SNormJob : IJobParallelForBatch
{
    public DecompressBlocks<BC4SNormDecoder> inner;

    public void Execute(int start, int count) => inner.Execute(start, count);
}

[BurstCompile(FloatMode = FloatMode.Fast)]
internal struct DecompressBC5UNormJob : IJobParallelForBatch
{
    public DecompressBlocks<BC5UNormDecoder> inner;

    public void Execute(int start, int count) => inner.Execute(start, count);
}

[BurstCompile(FloatMode = FloatMode.Fast)]
internal struct DecompressBC5SNormJob : IJobParallelForBatch
{
    public DecompressBlocks<BC5SNormDecoder> inner;

    public void Execute(int start, int count) => inner.Execute(start, count);
}

[BurstCompile(FloatMode = FloatMode.Fast)]
internal struct DecompressBC6HJob : IJobParallelForBatch
{
    public DecompressBlocks<BC6HDecoder> inner;

    public void Execute(int start, int count) => inner.Execute(start, count);
}

[BurstCompile(FloatMode = FloatMode.Fast)]
internal struct DecompressBC7Job : IJobParallelForBatch
{
    public DecompressBlocks<BC7Decoder> inner;

    public void Execute(int start, int count) => inner.Execute(start, count);
}
#endregion
