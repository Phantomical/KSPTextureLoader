using System;
using System.IO;

namespace KSPTextureLoader.Format.Bundle;

/// <summary>Block compression modes used by UnityFS bundles.</summary>
internal enum BundleCompressionType
{
    None = 0,
    Lzma = 1,
    Lz4 = 2,
    Lz4Hc = 3,
}

/// <summary>
/// Decompresses the UnityFS blocks-info directory so the contained file names
/// can be read. The bulk block data is never decompressed here; Unity's VFS
/// handles that when the contents are read through <c>archive:/</c>. Only
/// <c>None</c> and <c>LZ4</c>/<c>LZ4HC</c> are supported.
/// </summary>
internal static class BundleCompression
{
    public static unsafe void DecompressInto(
        BundleCompressionType type,
        byte* src,
        int srcLength,
        byte* dst,
        int dstLength
    )
    {
        switch (type)
        {
            case BundleCompressionType.None:
                Buffer.MemoryCopy(src, dst, dstLength, Math.Min(srcLength, dstLength));
                break;

            case BundleCompressionType.Lz4:
            case BundleCompressionType.Lz4Hc:
                int written = Lz4.Decompress(src, srcLength, dst, dstLength);
                if (written != dstLength)
                    throw new InvalidDataException(
                        $"LZ4 block produced {written} bytes, expected {dstLength}"
                    );
                break;

            case BundleCompressionType.Lzma:
                throw new NotSupportedException(
                    "the bundle's blocks-info directory is LZMA-compressed, which is not supported. "
                        + "Rebuild the bundle with LZ4 (ChunkBasedCompression) or uncompressed."
                );

            default:
                throw new NotSupportedException($"unknown bundle compression type {(int)type}");
        }
    }
}
