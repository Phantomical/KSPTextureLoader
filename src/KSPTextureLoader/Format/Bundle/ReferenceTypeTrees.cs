using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

namespace KSPTextureLoader.Format.Bundle;

/// <summary>
/// The authoritative serialized type trees for the texture classes and
/// <c>AssetBundle</c>, loaded once from an embedded reference bundle that the
/// <c>ksp-texture-util</c> <c>make-typetree</c> command produces. The reference
/// bundle is an uncompressed UnityFS bundle whose serialized file carries every
/// class's type tree (sourced from Unity's own class database) and no objects.
/// </summary>
///
/// <remarks>
/// This replaces the hand-transcribed field layouts that used to live in
/// <see cref="SerializedTypeTrees"/>: <see cref="SerializedFileWriter"/> now copies
/// each class's type entry verbatim into the bundles it generates and enables the
/// type tree, so Unity deserializes objects from the embedded tree rather than its
/// compiled-in layout. Both the verbatim type-entry bytes (for the metadata) and the
/// hierarchical root (for serializing object bodies) come from here.
/// </remarks>
internal static class ReferenceTypeTrees
{
    /// <summary>Marks a bundle directory node as a serialized file.</summary>
    const uint SerializedFileNodeFlag = 0x4;

    readonly struct Entry(byte[] rawTypeEntry, TypeTreeTreeNode root)
    {
        /// <summary>The whole type entry, verbatim, ready to copy into a new file's metadata.</summary>
        public readonly byte[] RawTypeEntry = rawTypeEntry;

        /// <summary>The hierarchical type-tree root, used to serialize an object body.</summary>
        public readonly TypeTreeTreeNode Root = root;
    }

    sealed class Data
    {
        public readonly Dictionary<int, Entry> ByClassId = [];
        public string UnityVersion;
    }

    static readonly Lazy<Data> data = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The engine version string of the reference bundle (e.g. "2019.4.18f1").</summary>
    public static string UnityVersion => data.Value.UnityVersion;

    /// <summary>The verbatim type-entry bytes for a class, for copying into a new file's type list.</summary>
    public static byte[] TypeEntry(int classId) => Get(classId).RawTypeEntry;

    /// <summary>The hierarchical type-tree root for a class, for serializing an object body.</summary>
    public static TypeTreeTreeNode Root(int classId) => Get(classId).Root;

    static Entry Get(int classId)
    {
        if (data.Value.ByClassId.TryGetValue(classId, out var entry))
            return entry;
        throw new InvalidOperationException(
            $"the reference type-tree bundle has no type tree for class {classId}"
        );
    }

    static unsafe Data Load()
    {
        byte[] bundle = LoadEmbeddedBundle();
        byte[] serialized = ExtractSerializedFile(bundle);

        var result = new Data();
        fixed (byte* p = serialized)
        {
            var file = SerializedFile.Parse(
                new EndianBinaryReader(p, serialized.Length),
                captureTypeBytes: true
            );
            result.UnityVersion = file.UnityVersion;
            foreach (var type in file.Types)
            {
                if (type.RawBytes is null || type.Tree is null)
                    continue;
                result.ByClassId[type.ClassId] = new Entry(type.RawBytes, type.Tree.Root);
            }
        }
        return result;
    }

    static byte[] LoadEmbeddedBundle()
    {
        var asm = Assembly.GetExecutingAssembly();
        string name = null;
        foreach (var candidate in asm.GetManifestResourceNames())
            if (candidate.EndsWith("typetrees.bundle", StringComparison.OrdinalIgnoreCase))
            {
                name = candidate;
                break;
            }
        if (name is null)
            throw new InvalidOperationException("embedded reference type-tree bundle not found");

        using var stream = asm.GetManifestResourceStream(name);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Unwrap the reference bundle's single serialized file. The bundle is written
    /// uncompressed with one block and one (serialized-file) node, so the file bytes
    /// are a straight slice of the block data; anything else is a malformed artifact.
    /// </summary>
    static unsafe byte[] ExtractSerializedFile(byte[] bundle)
    {
        fixed (byte* p = bundle)
        {
            var reader = new EndianBinaryReader(p, bundle.Length);
            var header = BundleHeader.Read(reader, bundle.Length);

            if (header.BlocksInfoCompression != BundleCompressionType.None)
                throw new InvalidDataException(
                    "reference type-tree bundle must be uncompressed (blocks info is compressed)"
                );

            reader.BigEndian = true;
            reader.Position = header.BlocksInfoStart;
            reader.Skip(16); // uncompressed data hash

            int blockCount = reader.ReadInt32();
            if (blockCount != 1)
                throw new InvalidDataException(
                    $"reference type-tree bundle must have a single block (got {blockCount})"
                );
            reader.Skip(8); // block uncompressed + compressed size
            ushort blockFlags = reader.ReadUInt16();
            if ((blockFlags & 0x3F) != 0)
                throw new InvalidDataException(
                    "reference type-tree bundle must be uncompressed (block is compressed)"
                );

            int nodeCount = reader.ReadInt32();
            for (int i = 0; i < nodeCount; ++i)
            {
                long offset = reader.ReadInt64();
                long size = reader.ReadInt64();
                uint flags = reader.ReadUInt32();
                reader.ReadCString(); // node path

                if ((flags & SerializedFileNodeFlag) != 0)
                    return reader.CopyBytes(header.BlockDataStart + offset, checked((int)size));
            }
        }

        throw new InvalidDataException("reference type-tree bundle has no serialized file node");
    }
}
