using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KSPTextureLoader.Utils;
using Unity.Collections;

namespace KSPTextureLoader.Format.AssetBundle;

/// <summary>
/// Reads just the UnityFS header and directory of a bundle (the list of
/// contained files and their decompressed sizes) without decompressing the bulk
/// block data. Used by <see cref="BundleIndex"/> to discover the internal
/// <c>CAB-&lt;hash&gt;</c> name needed to build <c>archive:/</c> paths.
/// </summary>
internal static class BundleDirectory
{
    public static async Task<List<BundleNode>> ReadAsync(string diskPath)
    {
        long fileLength = new System.IO.FileInfo(diskPath).Length;

        // The header (signature + a few strings + sizes) is tiny; a small prefix
        // is always enough to parse it and locate the blocks info.
        long prefixLength = Math.Min(fileLength, 4096);
        var prefix = await FileLoader.ReadFileContentsAsync(
            new FileLoader.FileReadInfo
            {
                path = diskPath,
                offset = 0,
                length = prefixLength,
            }
        );

        BundleHeader header;
        try
        {
            unsafe
            {
                header = BundleHeader.Read(
                    new EndianBinaryReader(prefix.GetUnsafePtr(), prefix.Length),
                    fileLength
                );
            }
        }
        finally
        {
            prefix.DisposeExt();
        }

        var compressed = await FileLoader.ReadFileContentsAsync(
            new FileLoader.FileReadInfo
            {
                path = diskPath,
                offset = header.BlocksInfoStart,
                length = header.CompressedBlocksInfoSize,
            }
        );

        try
        {
            using var uncompressed = new LargeNativeArray<byte>(
                header.UncompressedBlocksInfoSize,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory
            );

            unsafe
            {
                BundleCompression.DecompressInto(
                    header.BlocksInfoCompression,
                    compressed.GetUnsafePtr(),
                    (int)header.CompressedBlocksInfoSize,
                    uncompressed.GetUnsafePtr(),
                    (int)header.UncompressedBlocksInfoSize
                );

                return BlocksInfo.ParseNodes(
                    new EndianBinaryReader(uncompressed.GetUnsafePtr(), uncompressed.Length)
                );
            }
        }
        finally
        {
            compressed.DisposeExt();
        }
    }
}
