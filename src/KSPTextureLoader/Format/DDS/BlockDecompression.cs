using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KSPTextureLoader.Burst;
using KSPTextureLoader.Jobs;
using KSPTextureLoader.Utils;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using static KSPTextureLoader.Format.DDSLoader;

namespace KSPTextureLoader.Format.DDS;

/// <summary>
/// Decompresses block-compressed textures into an equivalent uncompressed format.
/// </summary>
///
/// <remarks>
/// Unity 6.7 refuses to create block-compressed textures whose dimensions are not
/// a multiple of the block size. DDS files like that are common in KSP, so we
/// decompress them on the CPU instead of failing to load them.
/// </remarks>
internal static class BlockDecompression
{
    const int BlockBatchSize = 1024;

    /// <summary>
    /// Get the uncompressed format that a block-compressed format decompresses
    /// to, or <see cref="GraphicsFormat.None"/> if we can't decompress it.
    /// </summary>
    internal static GraphicsFormat GetDecompressedFormat(GraphicsFormat format)
    {
        return format switch
        {
            GraphicsFormat.RGBA_DXT1_UNorm
            or GraphicsFormat.RGBA_DXT3_UNorm
            or GraphicsFormat.RGBA_DXT5_UNorm
            or GraphicsFormat.RGBA_BC7_UNorm => GraphicsFormat.R8G8B8A8_UNorm,

            GraphicsFormat.RGBA_DXT1_SRGB
            or GraphicsFormat.RGBA_DXT3_SRGB
            or GraphicsFormat.RGBA_DXT5_SRGB
            or GraphicsFormat.RGBA_BC7_SRGB => GraphicsFormat.R8G8B8A8_SRGB,

            GraphicsFormat.R_BC4_UNorm => GraphicsFormat.R8_UNorm,
            GraphicsFormat.R_BC4_SNorm => GraphicsFormat.R8_SNorm,
            GraphicsFormat.RG_BC5_UNorm => GraphicsFormat.R8G8_UNorm,
            GraphicsFormat.RG_BC5_SNorm => GraphicsFormat.R8G8_SNorm,

            // BC6H is HDR, so it needs a float format to avoid clamping.
            GraphicsFormat.RGB_BC6H_UFloat or GraphicsFormat.RGB_BC6H_SFloat =>
                GraphicsFormat.R16G16B16A16_SFloat,

            _ => GraphicsFormat.None,
        };
    }

    /// <summary>
    /// Whether this texture is block-compressed with dimensions that are not a
    /// multiple of the block size, and we know how to decompress it.
    /// </summary>
    internal static bool NeedsDecompression(GraphicsFormat format, int width, int height)
    {
        if (!GraphicsFormatUtility.IsCompressedFormat(format))
            return false;

        int blockWidth = (int)GraphicsFormatUtility.GetBlockWidth(format);
        int blockHeight = (int)GraphicsFormatUtility.GetBlockHeight(format);
        if (width % blockWidth == 0 && height % blockHeight == 0)
            return false;

        return GetDecompressedFormat(format) != GraphicsFormat.None;
    }

    /// <summary>
    /// Decompress <paramref name="metadata"/>'s pixel data, updating its format
    /// to match. Takes ownership of <paramref name="source"/>.
    /// </summary>
    internal static PixelDataSource Decompress(
        string path,
        ref TextureMetadata metadata,
        PixelDataSource source
    )
    {
        var srcFormat = metadata.format;
        var dstFormat = GetDecompressedFormat(srcFormat);
        if (dstFormat == GraphicsFormat.None)
            throw new NotSupportedException($"Cannot decompress textures with format {srcFormat}");

        var blockSize = (int)GraphicsFormatUtility.GetBlockSize(srcFormat);
        var pixelSize = (int)GraphicsFormatUtility.GetBlockSize(dstFormat);
        var surfaces = BuildSurfaces(
            metadata,
            blockSize,
            pixelSize,
            out long srcSize,
            out long dstSize,
            out int blockCount
        );

        LogWarning(path, metadata, dstFormat);

        var data = DecompressAsync(
            source.TakeData(),
            srcFormat,
            surfaces,
            srcSize,
            dstSize,
            blockCount
        );

        metadata.format = dstFormat;
        return new PixelDataSource(data, dstSize);
    }

    static List<DecompressSurface> BuildSurfaces(
        TextureMetadata metadata,
        int blockSize,
        int pixelSize,
        out long srcSize,
        out long dstSize,
        out int blockCount
    )
    {
        var surfaces = new List<DecompressSurface>();
        long src = 0;
        long dst = 0;
        int block = 0;

        void Add(int mip)
        {
            int width = Math.Max(1, metadata.width >> mip);
            int height = Math.Max(1, metadata.height >> mip);
            int blocksPerRow = (width + 3) / 4;
            int blocks = checked(blocksPerRow * ((height + 3) / 4));

            surfaces.Add(
                new DecompressSurface
                {
                    srcOffset = src,
                    dstOffset = dst,
                    width = width,
                    height = height,
                    blocksPerRow = blocksPerRow,
                    firstBlock = block,
                }
            );

            src += (long)blocks * blockSize;
            dst += (long)width * height * pixelSize;
            block = checked(block + blocks);
        }

        if (metadata.type == DDSTextureType.Texture3D)
        {
            // 3D textures store each mip as a sequence of independently
            // compressed depth slices.
            for (int mip = 0; mip < metadata.mipCount; ++mip)
            {
                int depth = Math.Max(1, metadata.depth >> mip);
                for (int z = 0; z < depth; ++z)
                    Add(mip);
            }
        }
        else
        {
            for (int element = 0; element < metadata.arraySize; ++element)
            {
                for (int mip = 0; mip < metadata.mipCount; ++mip)
                    Add(mip);
            }
        }

        srcSize = src;
        dstSize = dst;
        blockCount = block;
        return surfaces;
    }

    static Task<LargeNativeArray<byte>> DecompressAsync(
        Task<LargeNativeArray<byte>> dataTask,
        GraphicsFormat format,
        List<DecompressSurface> surfaceList,
        long srcSize,
        long dstSize,
        int blockCount
    )
    {
        return Task.Run(async () =>
        {
            using var rawGuard = new TaskArrayDisposeGuard(dataTask);
            var raw = await dataTask;
            if (raw.Length < srcSize)
                throw new Exception("Invalid DDS file: not enough data for specified texture size");

            var output = AllocatorUtil.CreateNativeArrayHGlobal<byte>(
                dstSize,
                NativeArrayOptions.UninitializedMemory
            );
            try
            {
                var surfaces = AllocatorUtil.CreateNativeArrayHGlobal<DecompressSurface>(
                    surfaceList.Count,
                    NativeArrayOptions.UninitializedMemory
                );
                using var surfacesGuard = new NativeArrayGuard<DecompressSurface>(surfaces);
                for (int i = 0; i < surfaceList.Count; ++i)
                    surfaces[i] = surfaceList[i];

                await AsyncUtil.LaunchMainThreadTask(() =>
                {
                    var handle = Schedule(format, raw, output, surfaces, blockCount);
                    return AsyncUtil.WaitFor(handle);
                });
            }
            catch
            {
                output.DisposeExt();
                throw;
            }

            return output;
        });
    }

    static unsafe JobHandle Schedule(
        GraphicsFormat format,
        LargeNativeArray<byte> src,
        LargeNativeArray<byte> dst,
        NativeArray<DecompressSurface> surfaces,
        int blockCount
    )
    {
        byte* srcPtr = src.GetUnsafePtr();
        byte* dstPtr = dst.GetUnsafePtr();

        DecompressBlocks<T> Inner<T>(T decoder = default)
            where T : struct, IBlockDecoder =>
            new()
            {
                surfaces = surfaces,
                src = srcPtr,
                dst = dstPtr,
                decoder = decoder,
            };

        // Each case needs to name its job type concretely so that burst
        // can find and compile it.
        switch (format)
        {
            case GraphicsFormat.RGBA_DXT1_UNorm:
            case GraphicsFormat.RGBA_DXT1_SRGB:
                return new DecompressDXT1Job { inner = Inner<DXT1Decoder>() }.ScheduleBatch(
                    blockCount,
                    BlockBatchSize
                );

            case GraphicsFormat.RGBA_DXT3_UNorm:
            case GraphicsFormat.RGBA_DXT3_SRGB:
                return new DecompressDXT3Job { inner = Inner<DXT3Decoder>() }.ScheduleBatch(
                    blockCount,
                    BlockBatchSize
                );

            case GraphicsFormat.RGBA_DXT5_UNorm:
            case GraphicsFormat.RGBA_DXT5_SRGB:
                return new DecompressDXT5Job { inner = Inner<DXT5Decoder>() }.ScheduleBatch(
                    blockCount,
                    BlockBatchSize
                );

            case GraphicsFormat.R_BC4_UNorm:
                return new DecompressBC4UNormJob { inner = Inner<BC4UNormDecoder>() }.ScheduleBatch(
                    blockCount,
                    BlockBatchSize
                );

            case GraphicsFormat.R_BC4_SNorm:
                return new DecompressBC4SNormJob { inner = Inner<BC4SNormDecoder>() }.ScheduleBatch(
                    blockCount,
                    BlockBatchSize
                );

            case GraphicsFormat.RG_BC5_UNorm:
                return new DecompressBC5UNormJob { inner = Inner<BC5UNormDecoder>() }.ScheduleBatch(
                    blockCount,
                    BlockBatchSize
                );

            case GraphicsFormat.RG_BC5_SNorm:
                return new DecompressBC5SNormJob { inner = Inner<BC5SNormDecoder>() }.ScheduleBatch(
                    blockCount,
                    BlockBatchSize
                );

            case GraphicsFormat.RGB_BC6H_UFloat:
            case GraphicsFormat.RGB_BC6H_SFloat:
                return new DecompressBC6HJob
                {
                    inner = Inner<BC6HDecoder>(
                        new() { signed = format == GraphicsFormat.RGB_BC6H_SFloat }
                    ),
                }.ScheduleBatch(blockCount, BlockBatchSize);

            case GraphicsFormat.RGBA_BC7_UNorm:
            case GraphicsFormat.RGBA_BC7_SRGB:
                return new DecompressBC7Job { inner = Inner<BC7Decoder>() }.ScheduleBatch(
                    blockCount,
                    BlockBatchSize
                );

            default:
                throw new NotSupportedException($"Cannot decompress textures with format {format}");
        }
    }

    static void LogWarning(string path, TextureMetadata metadata, GraphicsFormat dstFormat)
    {
        var message =
            $"[KSPTextureLoader] {path} has format {metadata.format} and dimension {metadata.width}x{metadata.height},"
            + $"but unity requires that compressed texture sizes be multiples of 4x4. It will be decompressed to {dstFormat}.";

        _ = AsyncUtil.LaunchMainThreadTask(() => Debug.LogWarning(message));
    }
}
