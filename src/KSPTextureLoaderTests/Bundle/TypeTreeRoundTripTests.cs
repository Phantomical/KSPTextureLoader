using KSP.Testing;
using KSPTextureLoader.Format.Bundle;

namespace KSPTextureLoaderTests.Bundle;

/// <summary>
/// End-to-end tests for the type-tree-emitting write path: a bundle built by
/// <see cref="TextureBundleBuilder"/> (which enables the type tree and copies each
/// class's type entry verbatim from the embedded reference bundle via
/// <see cref="ReferenceTypeTrees"/>) must round-trip back through the loader's own
/// <see cref="SerializedFile"/> reader with every field intact.
/// </summary>
public unsafe class TypeTreeRoundTripTests : BundleParseTestBase
{
    [TestInfo("ReferenceTypeTrees_HasEveryClass")]
    public void TestReferenceTypeTreesHasEveryClass()
    {
        AssertEqual("unity-version", ReferenceTypeTrees.UnityVersion, "2019.4.18f1");

        int[] classIds =
        [
            SerializedTypeTrees.Texture2DClassId,
            SerializedTypeTrees.CubemapClassId,
            SerializedTypeTrees.Texture3DClassId,
            SerializedTypeTrees.Texture2DArrayClassId,
            SerializedTypeTrees.CubemapArrayClassId,
            SerializedTypeTrees.AssetBundleClassId,
        ];
        foreach (int id in classIds)
        {
            AssertTrue(
                $"class-{id}-type-entry",
                ReferenceTypeTrees.TypeEntry(id) is { Length: > 0 }
            );
            AssertTrue($"class-{id}-root", ReferenceTypeTrees.Root(id) is not null);
        }
    }

    [TestInfo("TypeTreeRoundTrip_ClassicTexture2D")]
    public void TestClassicTexture2DRoundTrip()
    {
        const long pixels = 2048;
        var req = new TextureBundleBuilder.TextureRequest
        {
            ClassId = SerializedTypeTrees.Texture2DClassId,
            Name = "roundtrip_tex",
            Width = 64,
            Height = 32,
            MipCount = 1,
            Format = 10, // DXT1
            ColorSpace = 1,
            Readable = false,
        };

        var built = TextureBundleBuilder.Build(req, pixels);
        byte[] serialized = ExtractSerializedFile(built.Prefix);

        fixed (byte* p = serialized)
        {
            var file = SerializedFile.Parse(new EndianBinaryReader(p, serialized.Length));

            AssertEqual("version", file.Version, 21u);
            AssertEqual("object-count", file.Objects.Count, 2);
            // m_EnableTypeTree == true: every type entry carries a parsed tree.
            foreach (var t in file.Types)
                AssertTrue($"type-{t.ClassId}-has-tree", t.Tree is not null);

            var texObj = FindObject(file, SerializedTypeTrees.Texture2DClassId);
            var tex = file.ReadObject(texObj);
            AssertEqual("m_Name", tex.Field("m_Name").AsString(), "roundtrip_tex");
            AssertEqual("m_Width", tex.Field("m_Width").AsInt(), 64L);
            AssertEqual("m_Height", tex.Field("m_Height").AsInt(), 32L);
            AssertEqual("m_TextureFormat", tex.Field("m_TextureFormat").AsInt(), 10L);
            AssertEqual("m_MipCount", tex.Field("m_MipCount").AsInt(), 1L);

            var sd = tex.Field("m_StreamData");
            AssertEqual("stream-size", sd.Field("size").AsInt(), pixels);
            AssertEqual("stream-offset", sd.Field("offset").AsInt(), 0L);
            AssertTrue("stream-path-set", !string.IsNullOrEmpty(sd.Field("path").AsString()));

            // The AssetBundle container must map the requested key to the texture.
            var abObj = FindObject(file, SerializedTypeTrees.AssetBundleClassId);
            var container = file.ReadObject(abObj).Field("m_Container");
            AssertTrue("container-one-entry", container.Elements is { Count: 1 });
            var pair = container.Elements[0];
            AssertEqual("container-key", pair.Field("first").AsString(), built.AssetName);
            AssertEqual(
                "container-pathid",
                pair.Field("second").Field("asset").Field("m_PathID").AsInt(),
                texObj.PathId
            );
        }
    }

    [TestInfo("TypeTreeRoundTrip_ModernTexture2DArray")]
    public void TestModernTexture2DArrayRoundTrip()
    {
        const long pixels = 4096;
        var req = new TextureBundleBuilder.TextureRequest
        {
            ClassId = SerializedTypeTrees.Texture2DArrayClassId,
            Name = "roundtrip_arr",
            Width = 32,
            Height = 32,
            Depth = 4,
            MipCount = 1,
            Format = 50, // GraphicsFormat
            ColorSpace = 0,
            Readable = true,
        };

        var built = TextureBundleBuilder.Build(req, pixels);
        byte[] serialized = ExtractSerializedFile(built.Prefix);

        fixed (byte* p = serialized)
        {
            var file = SerializedFile.Parse(new EndianBinaryReader(p, serialized.Length));
            foreach (var t in file.Types)
                AssertTrue($"type-{t.ClassId}-has-tree", t.Tree is not null);

            var obj = FindObject(file, SerializedTypeTrees.Texture2DArrayClassId);
            var tex = file.ReadObject(obj);
            AssertEqual("m_Name", tex.Field("m_Name").AsString(), "roundtrip_arr");
            AssertEqual("m_Width", tex.Field("m_Width").AsInt(), 32L);
            AssertEqual("m_Format", tex.Field("m_Format").AsInt(), 50L);
            AssertEqual("m_Depth", tex.Field("m_Depth").AsInt(), 4L);
            AssertEqual("m_DataSize", tex.Field("m_DataSize").AsInt(), pixels);
            AssertEqual("m_IsReadable", tex.Field("m_IsReadable").AsInt(), 1L);
        }
    }

    static SerializedObject FindObject(SerializedFile file, int classId)
    {
        foreach (var o in file.Objects)
            if (o.ClassId == classId)
                return o;
        throw new System.Exception($"no object of class {classId} in file");
    }

    // Unwrap the single serialized file from an uncompressed UnityFS bundle prefix.
    static byte[] ExtractSerializedFile(byte[] bundle)
    {
        fixed (byte* p = bundle)
        {
            var reader = new EndianBinaryReader(p, bundle.Length);
            var header = BundleHeader.Read(reader, bundle.Length);

            reader.BigEndian = true;
            reader.Position = header.BlocksInfoStart;
            reader.Skip(16); // uncompressed data hash
            int blockCount = reader.ReadInt32();
            reader.Skip(blockCount * 10L); // block entries
            int nodeCount = reader.ReadInt32();
            for (int i = 0; i < nodeCount; ++i)
            {
                long offset = reader.ReadInt64();
                long size = reader.ReadInt64();
                uint flags = reader.ReadUInt32();
                reader.ReadCString();
                if ((flags & 0x4) != 0)
                    return reader.CopyBytes(header.BlockDataStart + offset, (int)size);
            }
        }
        throw new System.Exception("no serialized file node in bundle prefix");
    }
}
