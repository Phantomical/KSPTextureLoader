using System;
using System.Collections.Generic;
using System.Text;

namespace KSPTextureLoader.Format.AssetBundle;

/// <summary>
/// Writer for serialized unity asset bundle files.
/// </summary>
///
/// <remarks>
/// We don't emit type tree data, so the emitted files are only compatible with
/// Unity 2019.4, and possibly only 2019.4.18f1.
/// </remarks>
internal static class SerializedFileWriter
{
    // 2019.4 writes serialized-file format 21, and without type trees Unity
    // requires an exact version match — anything else is rejected at load with
    // "not built with the right version or build target".
    const uint FormatVersion = 21;

    const int ObjectAlignment = 16;

    /// <summary>One object to place in the serialized file.</summary>
    public readonly struct ObjectEntry(long pathId, int classId, SerializedValue value)
    {
        public readonly long PathId = pathId;
        public readonly int ClassId = classId;
        public readonly SerializedValue Value = value;
    }

    /// <summary>
    /// Serialize the given objects into a complete serialized-file byte array.
    /// <paramref name="targetPlatform"/> is the Unity <c>BuildTarget</c> the
    /// bundle is tagged for; it must match the running player or Unity rejects
    /// the bundle at load.
    /// </summary>
    public static byte[] Build(IReadOnlyList<ObjectEntry> objects, int targetPlatform)
    {
        if (objects is null || objects.Count == 0)
            throw new ArgumentException("at least one object is required", nameof(objects));

        var objectData = new byte[objects.Count][];
        for (int i = 0; i < objects.Count; ++i)
        {
            var ow = new EndianBinaryWriter { BigEndian = false };
            WriteValue(ow, SerializedTypeTrees.Root(objects[i].ClassId), objects[i].Value);
            objectData[i] = ow.ToArray();
        }

        // Lay out the object data section. Keeping the section and every object
        // start 16-aligned means alignment within an object's own buffer agrees
        // with the file-absolute alignment the reader performs.
        var byteStart = new long[objects.Count];
        long sectionLength = 0;
        for (int i = 0; i < objects.Count; ++i)
        {
            sectionLength = Align(sectionLength, ObjectAlignment);
            byteStart[i] = sectionLength;
            sectionLength += objectData[i].Length;
        }

        var typeOrder = new List<int>();
        var typeIndex = new Dictionary<int, int>();
        var objTypeIndex = new int[objects.Count];
        for (int i = 0; i < objects.Count; ++i)
        {
            int classId = objects[i].ClassId;
            if (!typeIndex.TryGetValue(classId, out int idx))
            {
                idx = typeOrder.Count;
                typeIndex[classId] = idx;
                typeOrder.Add(classId);
            }
            objTypeIndex[i] = idx;
        }

        // The metadata block, little-endian, starting right after the 20-byte
        // header.
        var meta = new EndianBinaryWriter { BigEndian = false };
        meta.WriteCString(SerializedTypeTrees.UnityVersion);
        meta.WriteInt32(targetPlatform);
        meta.WriteBoolean(false); // m_EnableTypeTree

        meta.WriteInt32(typeOrder.Count);
        foreach (int classId in typeOrder)
        {
            meta.WriteInt32(classId);
            meta.WriteBoolean(false); // m_IsStrippedType
            meta.WriteInt16(-1); // m_ScriptTypeIndex
            meta.WriteBytes(SerializedTypeTrees.OldTypeHash(classId));
        }

        meta.WriteInt32(objects.Count);
        for (int i = 0; i < objects.Count; ++i)
        {
            meta.Align(4);
            meta.WriteInt64(objects[i].PathId);
            meta.WriteUInt32(checked((uint)byteStart[i]));
            meta.WriteUInt32(checked((uint)objectData[i].Length));
            meta.WriteInt32(objTypeIndex[i]);
        }

        meta.WriteInt32(0); // m_ScriptTypes count
        meta.WriteInt32(0); // m_Externals count
        meta.WriteInt32(0); // m_RefTypes count
        meta.WriteCString(""); // userInformation

        byte[] metaBytes = meta.ToArray();

        // Assemble the file: big-endian header, metadata, padding, object data.
        int headerLen = 20;
        long dataOffset = Align(headerLen + (long)metaBytes.Length, ObjectAlignment);
        long fileSize = dataOffset + sectionLength;

        var w = new EndianBinaryWriter((int)Math.Min(fileSize, int.MaxValue)) { BigEndian = true };
        w.WriteUInt32(checked((uint)metaBytes.Length)); // m_MetadataSize
        w.WriteUInt32(checked((uint)fileSize)); // m_FileSize
        w.WriteUInt32(FormatVersion);
        w.WriteUInt32(checked((uint)dataOffset));
        w.WriteByte(0); // m_Endianess: 0 == little-endian data
        w.WriteZeros(3); // reserved

        w.WriteBytes(metaBytes);

        while (w.Length < dataOffset)
            w.WriteByte(0);

        for (int i = 0; i < objects.Count; ++i)
        {
            while (w.Length < dataOffset + byteStart[i])
                w.WriteByte(0);
            w.WriteBytes(objectData[i]);
        }

        return w.ToArray();
    }

    static long Align(long value, int alignment)
    {
        long rem = value % alignment;
        return rem == 0 ? value : value + (alignment - rem);
    }

    // The generic object serializer: the exact inverse of TypeTreeReader.Read.

    static void WriteValue(EndianBinaryWriter w, TypeTreeTreeNode node, SerializedValue v)
    {
        var self = node.Self;

        if (self.IsArray)
        {
            WriteArray(w, node, v);
        }
        else if (node.Children.Count == 0)
        {
            WritePrimitive(w, self, v);
        }
        else if (node.Children.Count == 1 && node.Children[0].Self.IsArray)
        {
            // string / vector / map / TypelessData wrapper around a single Array
            // child; the value is the array payload itself.
            WriteValue(w, node.Children[0], v);
        }
        else
        {
            foreach (var child in node.Children)
                WriteValue(w, child, RequireField(v, child.Self.Name, self.Type));
        }

        if (self.AlignBytes)
            w.Align(4);
    }

    static void WriteArray(EndianBinaryWriter w, TypeTreeTreeNode node, SerializedValue v)
    {
        // child[0] is the "size" int, child[1] is the element template.
        var elem = node.Children[1];
        bool elemIsLeaf = elem.Children.Count == 0;

        if (elemIsLeaf && elem.Self.ByteSize == 1)
        {
            if (elem.Self.Type == "char")
            {
                byte[] bytes = v.Str is null ? [] : Encoding.UTF8.GetBytes(v.Str);
                w.WriteInt32(bytes.Length);
                w.WriteBytes(bytes);
            }
            else
            {
                byte[] bytes = v.Bytes ?? [];
                w.WriteInt32(bytes.Length);
                w.WriteBytes(bytes);
            }
        }
        else
        {
            var elements = v.Elements ?? [];
            w.WriteInt32(elements.Count);
            foreach (var e in elements)
                WriteValue(w, elem, e);
        }
    }

    static void WritePrimitive(EndianBinaryWriter w, TypeTreeNode node, SerializedValue v)
    {
        switch (node.Type)
        {
            case "SInt8":
                w.WriteSByte((sbyte)v.Int);
                break;
            case "UInt8":
            case "char":
                w.WriteByte((byte)v.Int);
                break;
            case "SInt16":
            case "short":
                w.WriteInt16((short)v.Int);
                break;
            case "UInt16":
            case "unsigned short":
                w.WriteUInt16((ushort)v.Int);
                break;
            case "SInt32":
            case "int":
                w.WriteInt32((int)v.Int);
                break;
            case "UInt32":
            case "unsigned int":
            case "Type*":
                w.WriteUInt32((uint)v.Int);
                break;
            case "SInt64":
            case "long long":
                w.WriteInt64(v.Int);
                break;
            case "UInt64":
            case "unsigned long long":
            case "FileSize":
                w.WriteUInt64((ulong)v.Int);
                break;
            case "float":
                w.WriteSingle((float)v.Float);
                break;
            case "double":
                w.WriteDouble(v.Float);
                break;
            case "bool":
                w.WriteBoolean(v.Int != 0);
                break;
            default:
                // Unknown leaf: emit its declared size as zeros to stay aligned.
                if (node.ByteSize > 0)
                    w.WriteZeros(node.ByteSize);
                break;
        }
    }

    static SerializedValue RequireField(SerializedValue v, string name, string ownerType)
    {
        if (v?.Fields is null || !v.Fields.TryGetValue(name, out var field))
            throw new InvalidOperationException(
                $"missing value for field \"{name}\" of \"{ownerType}\""
            );
        return field;
    }
}
