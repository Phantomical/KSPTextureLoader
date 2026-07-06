using System;

namespace KSPTextureLoader.Format.Bundle;

/// <summary>
/// Emits the prefix of a <c>UnityFS</c> bundle wrapping a single serialized
/// file (the <c>CAB-&lt;hash&gt;</c> entry) and its streamed resource
/// (<c>CAB-&lt;hash&gt;.resS</c>) — the write-side counterpart of
/// <see cref="BundleDirectory"/> / <see cref="BlocksInfo"/>. The resS bytes
/// themselves are not part of the prefix: the complete bundle is the prefix
/// followed by the pixel payload, spliced together by <see cref="BundleStream"/>
/// so the pixel data never has to be copied into a managed array.
/// </summary>
///
/// <remarks>
/// The layout matches what Unity 2019.4.18f1 itself produces, except that the
/// bundle is left uncompressed: it is loaded once and discarded, so compressing
/// it would only add work.
/// </remarks>
internal static class BundleWriter
{
    const string Signature = "UnityFS";
    const uint FormatVersion = 7;
    const string PlayerMinVersion = "5.x.x";
    const string EngineRevision = "2019.4.18f1";

    // Low 6 bits are the blocks-info compression type (0 == none); 0x40 is
    // kArchiveBlocksAndDirectoryInfoCombined, set by real 2019.4 bundles.
    const uint BundleFlags = 0x40;

    // Marks a directory node as a serialized file.
    const uint SerializedFileNodeFlag = 0x4;

    /// <summary>
    /// Build the bundle prefix — header, blocks info and the serialized file —
    /// from an already-serialized file and the length of its resource stream.
    /// The caller supplies the <paramref name="resSLength"/> resS bytes right
    /// after the prefix to form the complete bundle. <paramref name="cab"/> is
    /// the internal archive name; the resource node is named
    /// "<paramref name="cab"/>.resS" to match the <c>m_StreamData.path</c>
    /// written into the texture object.
    /// </summary>
    public static byte[] BuildPrefix(string cab, byte[] serializedFile, long resSLength)
    {
        if (string.IsNullOrEmpty(cab))
            throw new ArgumentException("cab name is required", nameof(cab));
        if (serializedFile is null)
            throw new ArgumentNullException(nameof(serializedFile));
        if (resSLength < 0)
            throw new ArgumentOutOfRangeException(nameof(resSLength));

        string resSName = cab + ".resS";

        // The blocks-info block sizes are unsigned 32-bit, and everything here
        // lives in a single uncompressed block.
        long blockDataSize = serializedFile.Length + resSLength;
        if (blockDataSize > uint.MaxValue)
            throw new InvalidOperationException(
                "texture data is too large to fit in a streamed bundle"
            );

        var blocksInfo = BuildBlocksInfo(
            cab,
            resSName,
            serializedFile.Length,
            resSLength,
            checked((uint)blockDataSize)
        );

        var w = new EndianBinaryWriter(64 + blocksInfo.Length + serializedFile.Length)
        {
            BigEndian = true,
        };

        w.WriteCString(Signature);
        w.WriteUInt32(FormatVersion);
        w.WriteCString(PlayerMinVersion);
        w.WriteCString(EngineRevision);

        int sizePos = w.Length;
        w.WriteInt64(0); // total bundle size, patched once known
        w.WriteUInt32((uint)blocksInfo.Length); // compressed blocks-info size
        w.WriteUInt32((uint)blocksInfo.Length); // uncompressed blocks-info size
        w.WriteUInt32(BundleFlags);

        // Bundle version 7 pads the header to a 16-byte boundary.
        w.Align(16);

        w.WriteBytes(blocksInfo);
        w.WriteBytes(serializedFile);

        w.PatchInt64(sizePos, w.Length + resSLength);
        return w.ToArray();
    }

    static byte[] BuildBlocksInfo(
        string cab,
        string resSName,
        int serializedFileSize,
        long resSSize,
        uint blockDataSize
    )
    {
        var w = new EndianBinaryWriter(128) { BigEndian = true };

        w.WriteZeros(16); // uncompressed data hash

        // A single uncompressed block spanning the whole virtual file system.
        w.WriteInt32(1);
        w.WriteUInt32(blockDataSize); // uncompressed size
        w.WriteUInt32(blockDataSize); // compressed size
        w.WriteUInt16(0); // compression type 0 == none

        // Two directory nodes: the serialized file, then its resS right after.
        w.WriteInt32(2);

        w.WriteInt64(0); // offset within the block data
        w.WriteInt64(serializedFileSize);
        w.WriteUInt32(SerializedFileNodeFlag);
        w.WriteCString(cab);

        w.WriteInt64(serializedFileSize);
        w.WriteInt64(resSSize);
        w.WriteUInt32(0);
        w.WriteCString(resSName);

        return w.ToArray();
    }
}
