using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KSPTextureLoader.Format.Bundle;

/// <summary>A single flattened node of a serialized type tree.</summary>
internal readonly struct TypeTreeNode(
    ushort version,
    byte level,
    byte typeFlags,
    string type,
    string name,
    int byteSize,
    int index,
    int metaFlags
)
{
    public readonly ushort Version = version;
    public readonly byte Level = level;
    public readonly byte TypeFlags = typeFlags;
    public readonly string Type = type;
    public readonly string Name = name;
    public readonly int ByteSize = byteSize;
    public readonly int Index = index;
    public readonly int MetaFlags = metaFlags;

    /// <summary>Whether this node represents an array (size + element).</summary>
    public bool IsArray => (TypeFlags & 1) != 0;

    /// <summary>Whether the reader must be aligned to 4 bytes after this node.</summary>
    public bool AlignBytes => (MetaFlags & 0x4000) != 0;
}

/// <summary>A node in the hierarchical view of a type tree.</summary>
internal sealed class TypeTreeTreeNode(TypeTreeNode self)
{
    public readonly TypeTreeNode Self = self;
    public readonly List<TypeTreeTreeNode> Children = [];
}

/// <summary>A parsed type tree, both flat and as a hierarchy.</summary>
internal sealed class TypeTree(List<TypeTreeNode> flat, TypeTreeTreeNode root)
{
    public readonly List<TypeTreeNode> Flat = flat;
    public readonly TypeTreeTreeNode Root = root;

    /// <summary>
    /// Read a type tree stored in the "blob" format (serialized file version
    /// 12+ and version 10).
    /// </summary>
    public static TypeTree ReadBlob(EndianBinaryReader reader, uint serializedFileVersion)
    {
        int nodeCount = reader.ReadInt32();
        int stringBufferSize = reader.ReadInt32();
        if (nodeCount < 0 || stringBufferSize < 0)
            throw new InvalidDataException("invalid type tree blob header");

        var raw = new (
            ushort Version,
            byte Level,
            byte TypeFlags,
            uint TypeOff,
            uint NameOff,
            int ByteSize,
            int Index,
            int MetaFlags
        )[nodeCount];
        for (int i = 0; i < nodeCount; ++i)
        {
            ushort version = reader.ReadUInt16();
            byte level = reader.ReadByte();
            byte typeFlags = reader.ReadByte();
            uint typeOff = reader.ReadUInt32();
            uint nameOff = reader.ReadUInt32();
            int byteSize = reader.ReadInt32();
            int index = reader.ReadInt32();
            int metaFlags = reader.ReadInt32();
            if (serializedFileVersion >= 19)
                reader.ReadUInt64(); // ref type hash, unused

            raw[i] = (version, level, typeFlags, typeOff, nameOff, byteSize, index, metaFlags);
        }

        var stringBuffer = reader.ReadBytes(stringBufferSize);

        var flat = new List<TypeTreeNode>(nodeCount);
        for (int i = 0; i < nodeCount; ++i)
        {
            var r = raw[i];
            flat.Add(
                new TypeTreeNode(
                    r.Version,
                    r.Level,
                    r.TypeFlags,
                    ResolveString(r.TypeOff, stringBuffer),
                    ResolveString(r.NameOff, stringBuffer),
                    r.ByteSize,
                    r.Index,
                    r.MetaFlags
                )
            );
        }

        return new TypeTree(flat, BuildTree(flat));
    }

    static string ResolveString(uint offset, byte[] localBuffer)
    {
        if ((offset & 0x80000000) != 0)
            return CommonString.ReadAt(CommonString.Buffer, offset & 0x7FFFFFFF);
        return ReadLocal(localBuffer, offset);
    }

    static string ReadLocal(byte[] buffer, uint offset)
    {
        int start = (int)offset;
        if (start < 0 || start >= buffer.Length)
            return $"(invalid string @ {offset})";

        int end = start;
        while (end < buffer.Length && buffer[end] != 0)
            end++;

        return Encoding.ASCII.GetString(buffer, start, end - start);
    }

    static TypeTreeTreeNode BuildTree(List<TypeTreeNode> flat)
    {
        if (flat.Count == 0)
            throw new InvalidDataException("empty type tree");

        TypeTreeTreeNode root = null;
        var stack = new Stack<TypeTreeTreeNode>();

        foreach (var node in flat)
        {
            var treeNode = new TypeTreeTreeNode(node);

            while (stack.Count > 0 && stack.Peek().Self.Level >= node.Level)
                stack.Pop();

            if (stack.Count == 0)
            {
                // The first node is the real root; deeper top-level nodes would
                // be malformed, so we just keep the first.
                root ??= treeNode;
            }
            else
            {
                stack.Peek().Children.Add(treeNode);
            }

            stack.Push(treeNode);
        }

        return root;
    }
}
