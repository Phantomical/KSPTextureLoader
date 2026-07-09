using System;
using System.Collections.Generic;
using System.IO;

namespace KSPTextureLoader.Format.Bundle;

/// <summary>One entry in a serialized file's type list.</summary>
internal struct SerializedType
{
    public int ClassId;
    public bool IsStripped;
    public short ScriptTypeIndex;
    public TypeTree Tree;

    /// <summary>
    /// The verbatim bytes of this whole type entry (class id, strip/script fields,
    /// hashes, type-tree blob and dependencies), or <c>null</c> when parsing did not
    /// request capture. Lets a writer re-emit the entry byte-for-byte instead of
    /// re-serializing the tree. See <see cref="SerializedFile.Parse"/>'s
    /// <c>captureTypeBytes</c> flag.
    /// </summary>
    public byte[] RawBytes;
}

/// <summary>One object entry in a serialized file's object table.</summary>
internal struct SerializedObject
{
    public long PathId;
    public long ByteStart; // relative to the serialized file's data section
    public uint ByteSize;
    public int TypeIndex;
    public int ClassId;
}

/// <summary>
/// A parsed Unity serialized file (the <c>CAB-&lt;hash&gt;</c> entry inside a
/// bundle). Only the header, type list (with type trees) and object table are
/// parsed; script and external reference tables are skipped.
/// </summary>
///
/// <remarks>
/// Targets serialized file format version 21 (Unity 2019.4) but accepts the
/// 13–21 range. Versions 22+ (which widen header fields to 64-bit) are rejected.
/// </remarks>
internal sealed class SerializedFile
{
    public uint Version { get; private set; }
    public long DataOffset { get; private set; }
    public bool BigEndian { get; private set; }

    /// <summary>The engine version string from the header (e.g. "2019.4.18f1").</summary>
    public string UnityVersion { get; private set; }
    public List<SerializedType> Types { get; } = [];
    public List<SerializedObject> Objects { get; } = [];

    readonly EndianBinaryReader reader;

    SerializedFile(EndianBinaryReader reader) => this.reader = reader;

    /// <param name="reader">A reader positioned at the start of the serialized file.</param>
    /// <param name="captureTypeBytes">
    /// When <c>true</c>, each parsed type entry keeps its verbatim bytes in
    /// <see cref="SerializedType.RawBytes"/> so a writer can re-emit it exactly.
    /// </param>
    public static SerializedFile Parse(EndianBinaryReader reader, bool captureTypeBytes = false)
    {
        var file = new SerializedFile(reader);
        file.ParseHeader(captureTypeBytes);
        return file;
    }

    void ParseHeader(bool captureTypeBytes)
    {
        // The header is always big-endian until the endianness byte.
        reader.BigEndian = true;
        reader.Position = 0;

        reader.ReadUInt32(); // metadataSize
        reader.ReadUInt32(); // fileSize
        uint version = reader.ReadUInt32();
        uint dataOffset = reader.ReadUInt32();

        if (version >= 22)
            throw new NotSupportedException(
                $"serialized file format version {version} is not supported (expected <= 21)"
            );

        bool bigEndian = true;
        if (version >= 9)
        {
            bigEndian = reader.ReadByte() != 0;
            reader.Skip(3); // reserved
        }

        Version = version;
        DataOffset = dataOffset;
        BigEndian = bigEndian;
        reader.BigEndian = bigEndian;

        if (version >= 7)
            UnityVersion = reader.ReadCString(); // unity version string
        if (version >= 8)
            reader.ReadInt32(); // target platform

        bool enableTypeTree = version < 13 || reader.ReadBoolean();

        int typeCount = reader.ReadInt32();
        for (int i = 0; i < typeCount; ++i)
            Types.Add(ReadType(version, enableTypeTree, captureTypeBytes));

        int bigIdEnabled = 0;
        if (version >= 7 && version < 14)
            bigIdEnabled = reader.ReadInt32();

        int objectCount = reader.ReadInt32();
        for (int i = 0; i < objectCount; ++i)
        {
            var obj = new SerializedObject();

            if (version >= 14)
            {
                reader.Align(4);
                obj.PathId = reader.ReadInt64();
            }
            else
            {
                obj.PathId = bigIdEnabled != 0 ? reader.ReadInt64() : reader.ReadInt32();
            }

            obj.ByteStart = version >= 22 ? reader.ReadInt64() : reader.ReadUInt32();
            obj.ByteSize = reader.ReadUInt32();
            obj.TypeIndex = reader.ReadInt32();

            if (version >= 16)
            {
                obj.ClassId =
                    obj.TypeIndex >= 0 && obj.TypeIndex < Types.Count
                        ? Types[obj.TypeIndex].ClassId
                        : -1;
            }
            else
            {
                obj.ClassId = obj.TypeIndex;
                reader.ReadInt16(); // script type index
                if (version <= 10)
                    reader.ReadByte(); // isDestroyed
            }

            Objects.Add(obj);
        }

        // Script and external reference tables follow but are not needed here.
    }

    SerializedType ReadType(uint version, bool enableTypeTree, bool captureBytes)
    {
        long start = reader.Position;
        var type = new SerializedType { ClassId = reader.ReadInt32() };

        if (version >= 16)
            type.IsStripped = reader.ReadBoolean();
        if (version >= 17)
            type.ScriptTypeIndex = reader.ReadInt16();

        if (version >= 13)
        {
            bool readsScriptHash =
                (version < 16 && type.ClassId < 0) || (version >= 16 && type.ClassId == 114);
            if (readsScriptHash)
                reader.Skip(16); // script id hash
            reader.Skip(16); // old type hash
        }

        if (enableTypeTree)
        {
            if (version >= 12 || version == 10)
                type.Tree = TypeTree.ReadBlob(reader, version);
            else
                throw new NotSupportedException(
                    "the legacy (non-blob) type tree format is not supported"
                );

            if (version >= 21)
            {
                int dependencyCount = reader.ReadInt32();
                reader.Skip((long)dependencyCount * 4);
            }
        }

        if (captureBytes)
            type.RawBytes = reader.CopyBytes(start, checked((int)(reader.Position - start)));

        return type;
    }

    /// <summary>
    /// Walk an object's serialized data using its type tree. Throws if the type
    /// tree was not present in the file.
    /// </summary>
    public TypeTreeValue ReadObject(SerializedObject obj)
    {
        var type = Types[obj.TypeIndex];
        if (type.Tree is null)
            throw new NotSupportedException(
                "the bundle was built without type trees, which is required to parse it"
            );

        reader.BigEndian = BigEndian;
        reader.Position = DataOffset + obj.ByteStart;
        return TypeTreeReader.Read(type.Tree.Root, reader);
    }

    /// <summary>
    /// Walk an object's serialized data using a <paramref name="regionReader"/>
    /// whose position 0 corresponds to the start of that object's data (i.e. a
    /// buffer holding only this object's region), rather than the file-wide
    /// reader. Throws if the type tree was not present in the file.
    /// </summary>
    public TypeTreeValue ReadObjectFrom(SerializedObject obj, EndianBinaryReader regionReader)
    {
        var type = Types[obj.TypeIndex];
        if (type.Tree is null)
            throw new NotSupportedException(
                "the bundle was built without type trees, which is required to parse it"
            );

        regionReader.BigEndian = BigEndian;
        regionReader.Position = 0;
        return TypeTreeReader.Read(type.Tree.Root, regionReader);
    }

    /// <summary>
    /// The hierarchical type tree root for an object, used to walk its fields
    /// manually. Throws if the type tree was not present in the file.
    /// </summary>
    public TypeTreeTreeNode RootNode(SerializedObject obj)
    {
        var type = Types[obj.TypeIndex];
        if (type.Tree is null)
            throw new NotSupportedException(
                "the bundle was built without type trees, which is required to parse it"
            );
        return type.Tree.Root;
    }

    /// <summary>The absolute offset of an object's data within this reader.</summary>
    public long ObjectDataOffset(SerializedObject obj) => DataOffset + obj.ByteStart;
}
