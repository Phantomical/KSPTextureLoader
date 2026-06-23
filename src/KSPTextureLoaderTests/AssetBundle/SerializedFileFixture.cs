using System;
using System.Collections.Generic;
using System.Text;
using KSPTextureLoader.Format.AssetBundle;

namespace KSPTextureLoaderTests.AssetBundle;

/// <summary>
/// Builds synthetic Unity serialized files (the <c>CAB-*</c> entry inside a
/// bundle) in memory so the bundle-parsing code can be unit tested without a
/// live, mounted asset bundle.
///
/// The layout deliberately mirrors the real serialized-file format (version 17,
/// little-endian object data) closely enough that <see cref="SerializedFile"/>
/// and  <see cref="Texture2DInfo"/> parse it, but every value is chosen by the
/// test so expected results are known exactly.
/// </summary>
internal static class SerializedFileFixture
{
    public const uint Version = 17;
    public const int Texture2DClassId = 28;
    public const int AssetBundleClassId = 142;
    const int AlignFlag = 0x4000;

    /// <summary>A flattened type-tree node, written into a type blob.</summary>
    public sealed class NodeSpec(
        byte level,
        bool isArray,
        string type,
        string name,
        int byteSize = -1,
        bool align = false
    )
    {
        public readonly byte Level = level;
        public readonly byte TypeFlags = (byte)(isArray ? 1 : 0);
        public readonly string Type = type;
        public readonly string Name = name;
        public readonly int ByteSize = byteSize;
        public readonly int MetaFlags = align ? AlignFlag : 0;
    }

    public sealed class TypeSpec(int classId, List<NodeSpec> tree)
    {
        public readonly int ClassId = classId;

        /// <summary>The type tree, or null to omit it (a bundle built without type trees).</summary>
        public readonly List<NodeSpec> Tree = tree;
    }

    public sealed class ObjSpec(long pathId, int typeIndex, byte[] data)
    {
        public readonly long PathId = pathId;
        public readonly int TypeIndex = typeIndex;
        public readonly byte[] Data = data;
    }

    /// <summary>The bytes of a built file plus the placement of each object.</summary>
    public sealed class FileImage
    {
        public byte[] Bytes;
        public long DataOffset;
        public long[] ByteStarts;
        public uint[] ByteSizes;

        public long ObjectDataOffset(int i) => DataOffset + ByteStarts[i];
    }

    /// <summary>A built single-Texture2D file plus the values used to build it.</summary>
    public sealed class TextureImage
    {
        public FileImage File;

        public string Name;
        public int Width;
        public int Height;
        public int Format;
        public int ExpectedMipCount;

        /// <summary>Object-relative offset of the inline pixel bytes (just past the count prefix).</summary>
        public long PixelRegionOffset;
        public byte[] Pixels;

        public bool Streamed;
        public long StreamOffset;
        public long StreamSize;
        public string StreamPath;

        public long ObjectDataOffset => File.ObjectDataOffset(0);
        public uint ByteSize => File.ByteSizes[0];

        /// <summary>The absolute (file-wide) offset where the inline pixels live.</summary>
        public long ExpectedImageDataOffset => ObjectDataOffset + PixelRegionOffset;
    }

    // ---- Type trees -------------------------------------------------------

    /// <summary>
    /// A Texture2D type tree. <paramref name="useMipCount"/> selects the modern
    /// <c>m_MipCount</c> (int) layout; otherwise the older <c>m_MipMap</c> (bool).
    /// </summary>
    public static List<NodeSpec> Texture2DTree(bool useMipCount)
    {
        var nodes = new List<NodeSpec>
        {
            new(0, false, "Texture2D", "Base"),
            // m_Name (aligned string)
            new(1, false, "string", "m_Name"),
            new(2, true, "Array", "Array", align: true),
            new(3, false, "int", "size", 4),
            new(3, false, "char", "data", 1),
            new(1, false, "int", "m_Width", 4),
            new(1, false, "int", "m_Height", 4),
            new(1, false, "int", "m_TextureFormat", 4),
        };

        if (useMipCount)
            nodes.Add(new(1, false, "int", "m_MipCount", 4));
        else
            nodes.Add(new(1, false, "bool", "m_MipMap", 1));

        nodes.AddRange(
            new NodeSpec[]
            {
                // image data: a typeless byte array (count prefix + raw bytes)
                new(1, false, "TypelessData", "image data"),
                new(2, true, "Array", "Array"),
                new(3, false, "int", "size", 4),
                new(3, false, "UInt8", "data", 1),
                // m_StreamData { offset, size, path }
                new(1, false, "StreamingInfo", "m_StreamData"),
                new(2, false, "UInt64", "offset", 8),
                new(2, false, "UInt32", "size", 4),
                new(2, false, "string", "path"),
                new(3, true, "Array", "Array", align: true),
                new(4, false, "int", "size", 4),
                new(4, false, "char", "data", 1),
            }
        );

        return nodes;
    }

    /// <summary>A minimal one-node tree, enough to populate a type table entry.</summary>
    public static List<NodeSpec> MinimalTree(string typeName) => [new(0, false, typeName, "Base")];

    // ---- Object data ------------------------------------------------------

    /// <summary>
    /// Build a Texture2D object's serialized data. Returns the bytes and the
    /// object-relative offset of the inline pixels (just past the count prefix).
    /// </summary>
    static (byte[] data, long pixelRegionOffset) BuildTextureObject(
        string name,
        int width,
        int height,
        int format,
        int? mipCount,
        bool? mipMap,
        byte[] inlinePixels,
        long streamOffset,
        long streamSize,
        string streamPath
    )
    {
        var o = new Buf();

        // m_Name: length-prefixed, padded to 4.
        var nameBytes = Encoding.ASCII.GetBytes(name);
        o.I32(nameBytes.Length);
        o.Raw(nameBytes);
        o.Align(4);

        o.I32(width);
        o.I32(height);
        o.I32(format);

        if (mipCount.HasValue)
            o.I32(mipCount.Value);
        else
            o.U8(mipMap.GetValueOrDefault() ? 1 : 0);

        // image data: count prefix then raw bytes (only present when inline).
        int count = inlinePixels?.Length ?? 0;
        o.I32(count);
        long pixelRegionOffset = o.Pos;
        if (count > 0)
            o.Raw(inlinePixels);

        // m_StreamData { offset, size, path }
        o.U64((ulong)streamOffset);
        o.U32((uint)streamSize);
        var pathBytes = Encoding.ASCII.GetBytes(streamPath ?? "");
        o.I32(pathBytes.Length);
        o.Raw(pathBytes);
        o.Align(4);

        o.Align(4); // object data is a whole number of 4-byte words
        return (o.ToArray(), pixelRegionOffset);
    }

    // ---- File assembly ----------------------------------------------------

    public static FileImage BuildFile(bool enableTypeTree, List<TypeSpec> types, List<ObjSpec> objs)
    {
        var b = new Buf();

        // Header (big-endian up to the endianness byte).
        b.U32BE(0); // metadataSize (unused by the parser)
        b.U32BE(0); // fileSize (unused by the parser)
        b.U32BE(Version);
        int dataOffsetPatch = b.Pos;
        b.U32BE(0); // dataOffset, patched once known

        b.U8(0); // endianness: 0 == little-endian
        b.Raw(new byte[3]); // reserved

        // Little-endian from here on.
        b.Raw(Encoding.ASCII.GetBytes("2019.4.18f1"));
        b.U8(0); // unity version terminator
        b.I32(19); // target platform (StandaloneWindows64)
        b.U8((byte)(enableTypeTree ? 1 : 0));

        b.I32(types.Count);
        foreach (var t in types)
            WriteType(b, t, enableTypeTree);

        // No bigIDEnabled field for version 17 (only 7 <= version < 14).

        // Object table.
        var byteStarts = new long[objs.Count];
        var byteSizes = new uint[objs.Count];
        long cum = 0;
        for (int i = 0; i < objs.Count; ++i)
        {
            byteStarts[i] = cum;
            byteSizes[i] = (uint)objs[i].Data.Length;
            cum += objs[i].Data.Length;
        }

        b.I32(objs.Count);
        for (int i = 0; i < objs.Count; ++i)
        {
            b.Align(4); // version >= 14 aligns before the path id
            b.I64(objs[i].PathId);
            b.U32((uint)byteStarts[i]);
            b.U32(byteSizes[i]);
            b.I32(objs[i].TypeIndex);
        }

        // The script/external tables follow in a real file but the parser stops
        // here, so object data can begin at the next aligned offset.
        long dataOffset = Align(b.Pos, 16);
        b.PatchU32BE(dataOffsetPatch, (uint)dataOffset);
        while (b.Pos < dataOffset)
            b.U8(0);

        for (int i = 0; i < objs.Count; ++i)
        {
            while (b.Pos < dataOffset + byteStarts[i])
                b.U8(0);
            b.Raw(objs[i].Data);
        }

        return new FileImage
        {
            Bytes = b.ToArray(),
            DataOffset = dataOffset,
            ByteStarts = byteStarts,
            ByteSizes = byteSizes,
        };
    }

    static void WriteType(Buf b, TypeSpec type, bool enableTypeTree)
    {
        b.I32(type.ClassId);
        b.U8(0); // IsStripped (version >= 16)
        b.I16(0); // ScriptTypeIndex (version >= 17)

        // version >= 13: optional script id hash (only for MonoBehaviour) + old type hash.
        if (type.ClassId == 114)
            b.Raw(new byte[16]);
        b.Raw(new byte[16]);

        if (enableTypeTree)
            WriteTypeBlob(b, type.Tree);
        // No dependency list for version 17 (only version >= 21).
    }

    static void WriteTypeBlob(Buf b, List<NodeSpec> nodes)
    {
        var strings = new StringTable();
        var offsets = new (uint type, uint name)[nodes.Count];
        for (int i = 0; i < nodes.Count; ++i)
            offsets[i] = (strings.Add(nodes[i].Type), strings.Add(nodes[i].Name));
        var stringBuffer = strings.ToArray();

        b.I32(nodes.Count);
        b.I32(stringBuffer.Length);
        for (int i = 0; i < nodes.Count; ++i)
        {
            var n = nodes[i];
            b.U16(1); // node version
            b.U8(n.Level);
            b.U8(n.TypeFlags);
            b.U32(offsets[i].type);
            b.U32(offsets[i].name);
            b.I32(n.ByteSize);
            b.I32(i); // index
            b.I32(n.MetaFlags);
            // No ref-type hash for version 17 (only version >= 19).
        }
        b.Raw(stringBuffer);
    }

    // ---- High-level Texture2D file builders -------------------------------

    public static TextureImage InlineTexture(
        string name = "inline_tex",
        int width = 8,
        int height = 4,
        int format = 4, // RGBA32
        int mipCount = 1,
        byte[] pixels = null
    )
    {
        pixels ??= Pattern(64);
        var (data, pixelOffset) = BuildTextureObject(
            name,
            width,
            height,
            format,
            mipCount,
            null,
            pixels,
            0,
            0,
            ""
        );
        var file = BuildFile(
            true,
            [new(Texture2DClassId, Texture2DTree(useMipCount: true))],
            [new(1, 0, data)]
        );
        return new TextureImage
        {
            File = file,
            Name = name,
            Width = width,
            Height = height,
            Format = format,
            ExpectedMipCount = mipCount,
            PixelRegionOffset = pixelOffset,
            Pixels = pixels,
            Streamed = false,
        };
    }

    public static TextureImage StreamedTexture(
        string name = "streamed_tex",
        int width = 16,
        int height = 16,
        int format = 10, // DXT1
        int mipCount = 1,
        long streamOffset = 4096,
        long streamSize = 128,
        string streamPath = "archive:/CAB-deadbeef/streamed_tex.resS"
    )
    {
        var (data, pixelOffset) = BuildTextureObject(
            name,
            width,
            height,
            format,
            mipCount,
            null,
            null,
            streamOffset,
            streamSize,
            streamPath
        );
        var file = BuildFile(
            true,
            [new(Texture2DClassId, Texture2DTree(useMipCount: true))],
            [new(1, 0, data)]
        );
        return new TextureImage
        {
            File = file,
            Name = name,
            Width = width,
            Height = height,
            Format = format,
            ExpectedMipCount = mipCount,
            PixelRegionOffset = pixelOffset,
            Pixels = null,
            Streamed = true,
            StreamOffset = streamOffset,
            StreamSize = streamSize,
            StreamPath = streamPath,
        };
    }

    /// <summary>An inline texture whose mip count comes from the older m_MipMap bool.</summary>
    public static TextureImage MipMapTexture(
        string name = "mipmap_tex",
        int width = 4,
        int height = 4,
        int format = 4,
        bool mipMap = true,
        byte[] pixels = null
    )
    {
        pixels ??= Pattern(32);
        var (data, pixelOffset) = BuildTextureObject(
            name,
            width,
            height,
            format,
            null,
            mipMap,
            pixels,
            0,
            0,
            ""
        );
        var file = BuildFile(
            true,
            [new(Texture2DClassId, Texture2DTree(useMipCount: false))],
            [new(1, 0, data)]
        );
        return new TextureImage
        {
            File = file,
            Name = name,
            Width = width,
            Height = height,
            Format = format,
            ExpectedMipCount = mipMap ? ExpectedMips(width, height) : 1,
            PixelRegionOffset = pixelOffset,
            Pixels = pixels,
            Streamed = false,
        };
    }

    // ---- Helpers ----------------------------------------------------------

    public static byte[] Pattern(int n)
    {
        var bytes = new byte[n];
        for (int i = 0; i < n; ++i)
            bytes[i] = (byte)((i * 37 + 11) & 0xFF);
        return bytes;
    }

    static int ExpectedMips(int width, int height)
    {
        int count = 1;
        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width >> 1);
            height = Math.Max(1, height >> 1);
            count++;
        }
        return count;
    }

    static long Align(long value, int alignment)
    {
        long rem = value % alignment;
        return rem == 0 ? value : value + (alignment - rem);
    }

    /// <summary>A growable little/big-endian byte writer with offset patching.</summary>
    sealed class Buf
    {
        readonly List<byte> bytes = new();

        public int Pos => bytes.Count;

        public void U8(int v) => bytes.Add((byte)v);

        public void Raw(byte[] b) => bytes.AddRange(b);

        void LE(ulong v, int n)
        {
            for (int i = 0; i < n; ++i)
                bytes.Add((byte)(v >> (8 * i)));
        }

        public void U16(ushort v) => LE(v, 2);

        public void I16(short v) => LE((ushort)v, 2);

        public void U32(uint v) => LE(v, 4);

        public void I32(int v) => LE((uint)v, 4);

        public void U64(ulong v) => LE(v, 8);

        public void I64(long v) => LE((ulong)v, 8);

        public void U32BE(uint v)
        {
            for (int i = 3; i >= 0; --i)
                bytes.Add((byte)(v >> (8 * i)));
        }

        public void Align(int alignment)
        {
            while (Pos % alignment != 0)
                bytes.Add(0);
        }

        public void PatchU32BE(int index, uint v)
        {
            for (int i = 0; i < 4; ++i)
                bytes[index + i] = (byte)(v >> (8 * (3 - i)));
        }

        public byte[] ToArray() => bytes.ToArray();
    }

    sealed class StringTable
    {
        readonly List<byte> buffer = new();
        readonly Dictionary<string, uint> offsets = new();

        public uint Add(string s)
        {
            if (offsets.TryGetValue(s, out var existing))
                return existing;

            uint offset = (uint)buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes(s));
            buffer.Add(0);
            offsets[s] = offset;
            return offset;
        }

        public byte[] ToArray() => buffer.ToArray();
    }
}
