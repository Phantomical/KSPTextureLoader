using System.IO;

namespace KSPTextureLoader.Format.AssetBundle;

/// <summary>
/// The fixed UnityFS bundle header, plus the derived locations of the blocks
/// info and block data, as read by the slim directory reader.
/// </summary>
internal struct BundleHeader
{
    const uint BlocksInfoAtTheEnd = 0x80;
    const uint BlockInfoNeedPaddingAtStart = 0x200;
    const uint CompressionTypeMask = 0x3F;

    public uint Version;
    public string UnityRevision;
    public long Size;
    public uint CompressedBlocksInfoSize;
    public uint UncompressedBlocksInfoSize;
    public uint Flags;

    /// <summary>Absolute file offset of the (compressed) blocks info.</summary>
    public long BlocksInfoStart;

    /// <summary>Absolute file offset where the first data block begins.</summary>
    public long BlockDataStart;

    public readonly BundleCompressionType BlocksInfoCompression =>
        (BundleCompressionType)(Flags & CompressionTypeMask);

    /// <summary>
    /// Parse the header from a reader positioned at the start of the bundle.
    /// <paramref name="fileLength"/> is the total bundle length, needed to locate
    /// blocks info stored at the end of the file.
    /// </summary>
    public static BundleHeader Read(EndianBinaryReader reader, long fileLength)
    {
        reader.BigEndian = true;
        reader.Position = 0;

        string signature = reader.ReadCString();
        if (signature != "UnityFS")
            throw new InvalidDataException($"not a UnityFS bundle (signature \"{signature}\")");

        var header = new BundleHeader { Version = reader.ReadUInt32() };
        reader.ReadCString(); // player minimum version (e.g. "5.x.x")
        header.UnityRevision = reader.ReadCString(); // engine revision (e.g. "2019.4.18f1")
        header.Size = reader.ReadInt64();
        header.CompressedBlocksInfoSize = reader.ReadUInt32();
        header.UncompressedBlocksInfoSize = reader.ReadUInt32();
        header.Flags = reader.ReadUInt32();

        if (header.Version >= 7)
            reader.Align(16);

        long headerEnd = reader.Position;
        if ((header.Flags & BlocksInfoAtTheEnd) != 0)
        {
            header.BlocksInfoStart = fileLength - header.CompressedBlocksInfoSize;
            header.BlockDataStart = headerEnd;
        }
        else
        {
            header.BlocksInfoStart = headerEnd;
            header.BlockDataStart = headerEnd + header.CompressedBlocksInfoSize;
        }

        if ((header.Flags & BlockInfoNeedPaddingAtStart) != 0)
        {
            long rem = header.BlockDataStart % 16;
            if (rem != 0)
                header.BlockDataStart += 16 - rem;
        }

        return header;
    }
}
