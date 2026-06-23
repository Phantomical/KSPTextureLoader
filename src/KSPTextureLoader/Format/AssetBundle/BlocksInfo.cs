using System.Collections.Generic;
using System.IO;

namespace KSPTextureLoader.Format.AssetBundle;

/// <summary>
/// A single file contained within a UnityFS bundle (a serialized file such as
/// <c>CAB-&lt;hash&gt;</c> or a resource file such as <c>CAB-&lt;hash&gt;.resS</c>).
/// </summary>
internal readonly struct BundleNode(string path, long offset, long size, uint flags)
{
    public readonly string Path = path;
    public readonly long Offset = offset;
    public readonly long Size = size;
    public readonly uint Flags = flags;

    public override string ToString() => $"{Path} (offset={Offset}, size={Size})";
}

/// <summary>
/// Parser for the decompressed UnityFS "blocks info". Only the directory of
/// contained files is returned; the block list is skipped since the mounted
/// path lets Unity's VFS handle block decompression.
/// </summary>
internal static class BlocksInfo
{
    // Each block entry is uncompressedSize (u32) + compressedSize (u32) + flags (u16).
    const long BlockEntrySize = 10;

    public static List<BundleNode> ParseNodes(EndianBinaryReader reader)
    {
        reader.BigEndian = true;
        reader.Skip(16); // uncompressed data hash

        int blockCount = reader.ReadInt32();
        if (blockCount < 0)
            throw new InvalidDataException($"invalid block count {blockCount}");
        reader.Skip(blockCount * BlockEntrySize);

        int nodeCount = reader.ReadInt32();
        if (nodeCount < 0)
            throw new InvalidDataException($"invalid node count {nodeCount}");

        var nodes = new List<BundleNode>(nodeCount);
        for (int i = 0; i < nodeCount; ++i)
        {
            long offset = reader.ReadInt64();
            long size = reader.ReadInt64();
            uint flags = reader.ReadUInt32();
            string path = reader.ReadCString();
            nodes.Add(new BundleNode(path, offset, size, flags));
        }

        return nodes;
    }
}
