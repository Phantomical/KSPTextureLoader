using System;
using KSP.Testing;
using KSPTextureLoader.Format.AssetBundle;

namespace KSPTextureLoaderTests.AssetBundle;

/// <summary>
/// Tests for <see cref="SerializedFile"/> header/type/object parsing and the
/// behavior when a bundle is built without type trees.
/// </summary>
public unsafe class SerializedFileTests : BundleParseTestBase
{
    static SerializedFileFixture.FileImage BuildTwoObjectFile()
    {
        // A Texture2D and an AssetBundle object, the same shape the index walks.
        // These header tests never walk the object data, so placeholder bytes are
        // enough for the object table entries.
        return SerializedFileFixture.BuildFile(
            enableTypeTree: true,
            types:
            [
                new(
                    SerializedFileFixture.Texture2DClassId,
                    SerializedFileFixture.Texture2DTree(useMipCount: true)
                ),
                new(
                    SerializedFileFixture.AssetBundleClassId,
                    SerializedFileFixture.MinimalTree("AssetBundle")
                ),
            ],
            objs:
            [
                new(pathId: 100, typeIndex: 0, data: new byte[40]),
                new(pathId: 200, typeIndex: 1, data: new byte[8]),
            ]
        );
    }

    [TestInfo("SerializedFile_ParsesHeader")]
    public void TestParsesHeader()
    {
        var image = BuildTwoObjectFile();
        fixed (byte* p = image.Bytes)
        {
            var file = SerializedFile.Parse(new EndianBinaryReader(p, image.Bytes.Length));

            AssertEqual("version", file.Version, 17u);
            AssertEqual("big-endian", file.BigEndian, false);
            AssertEqual("data-offset", file.DataOffset, image.DataOffset);
        }
    }

    [TestInfo("SerializedFile_ParsesTypes")]
    public void TestParsesTypes()
    {
        var image = BuildTwoObjectFile();
        fixed (byte* p = image.Bytes)
        {
            var file = SerializedFile.Parse(new EndianBinaryReader(p, image.Bytes.Length));

            AssertEqual("type-count", file.Types.Count, 2);
            AssertEqual(
                "type0-classid",
                file.Types[0].ClassId,
                SerializedFileFixture.Texture2DClassId
            );
            AssertEqual(
                "type1-classid",
                file.Types[1].ClassId,
                SerializedFileFixture.AssetBundleClassId
            );
            AssertTrue("type0-has-tree", file.Types[0].Tree is not null);
            // The type tree root is the Texture2D's first top-level field.
            AssertEqual("root-name", file.Types[0].Tree.Root.Self.Name, "Base");
        }
    }

    [TestInfo("SerializedFile_ParsesObjectTable")]
    public void TestParsesObjectTable()
    {
        var image = BuildTwoObjectFile();
        fixed (byte* p = image.Bytes)
        {
            var file = SerializedFile.Parse(new EndianBinaryReader(p, image.Bytes.Length));

            AssertEqual("object-count", file.Objects.Count, 2);

            var tex = file.Objects[0];
            AssertEqual("obj0-pathid", tex.PathId, 100L);
            AssertEqual("obj0-classid", tex.ClassId, SerializedFileFixture.Texture2DClassId);
            AssertEqual("obj0-bytestart", tex.ByteStart, image.ByteStarts[0]);
            AssertEqual("obj0-bytesize", tex.ByteSize, image.ByteSizes[0]);
            AssertEqual("obj0-data-offset", file.ObjectDataOffset(tex), image.ObjectDataOffset(0));

            var bundle = file.Objects[1];
            AssertEqual("obj1-pathid", bundle.PathId, 200L);
            AssertEqual("obj1-classid", bundle.ClassId, SerializedFileFixture.AssetBundleClassId);
        }
    }

    [TestInfo("SerializedFile_RejectsVersion22Plus")]
    public void TestRejectsNewVersions()
    {
        // Forge the version field (big-endian at byte offset 8) up to 22.
        var image = BuildTwoObjectFile();
        image.Bytes[8] = 0;
        image.Bytes[9] = 0;
        image.Bytes[10] = 0;
        image.Bytes[11] = 22;
        var bytes = image.Bytes;
        fixed (byte* p = bytes)
        {
            var reader = new EndianBinaryReader(p, bytes.Length);
            AssertThrows<NotSupportedException>(
                "version-22-rejected",
                () => SerializedFile.Parse(reader)
            );
        }
    }

    // ---- Missing type tree ----------------------------------------------

    [TestInfo("SerializedFile_NoTypeTree_ParsesButCannotReadObjects")]
    public void TestNoTypeTreeParsesButCannotReadObjects()
    {
        // A bundle built WITHOUT type trees: the header, type table and object
        // table still parse, but the objects cannot be walked.
        var image = SerializedFileFixture.BuildFile(
            enableTypeTree: false,
            types:
            [
                new(
                    SerializedFileFixture.Texture2DClassId,
                    SerializedFileFixture.Texture2DTree(useMipCount: true)
                ),
            ],
            objs: [new(pathId: 1, typeIndex: 0, data: new byte[16])]
        );

        fixed (byte* p = image.Bytes)
        {
            var file = SerializedFile.Parse(new EndianBinaryReader(p, image.Bytes.Length));

            AssertEqual("object-count", file.Objects.Count, 1);
            AssertEqual("classid", file.Objects[0].ClassId, SerializedFileFixture.Texture2DClassId);
            AssertTrue("tree-is-null", file.Types[0].Tree is null);

            var obj = file.Objects[0];
            var region = new EndianBinaryReader(p, image.Bytes.Length);

            // Every route that needs the type tree reports it is unsupported.
            AssertThrows<NotSupportedException>("RootNode", () => file.RootNode(obj));
            AssertThrows<NotSupportedException>("ReadObject", () => file.ReadObject(obj));
            AssertThrows<NotSupportedException>(
                "ReadObjectFrom",
                () => file.ReadObjectFrom(obj, region)
            );
        }
    }

    [TestInfo("SerializedFile_NoTypeTree_Texture2DInfoExtractRejected")]
    public void TestNoTypeTreeTexture2DInfoRejected()
    {
        var image = SerializedFileFixture.BuildFile(
            enableTypeTree: false,
            types:
            [
                new(
                    SerializedFileFixture.Texture2DClassId,
                    SerializedFileFixture.Texture2DTree(useMipCount: true)
                ),
            ],
            objs: [new(pathId: 1, typeIndex: 0, data: new byte[16])]
        );

        fixed (byte* p = image.Bytes)
        {
            var file = SerializedFile.Parse(new EndianBinaryReader(p, image.Bytes.Length));
            var obj = file.Objects[0];

            // Texture2DInfo.Extract surfaces the same NotSupportedException (NOT
            // IncompleteObjectRegionException), so BundleIndex skips the texture
            // and the loader falls back to the GPU path.
            var region = new EndianBinaryReader(p, image.Bytes.Length);
            AssertThrows<NotSupportedException>(
                "extract-complete",
                () => Texture2DInfo.Extract(file, obj, region, regionIsComplete: true)
            );

            var region2 = new EndianBinaryReader(p, image.Bytes.Length);
            AssertThrows<NotSupportedException>(
                "extract-capped",
                () => Texture2DInfo.Extract(file, obj, region2, regionIsComplete: false)
            );
        }
    }
}
