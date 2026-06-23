using KSP.Testing;
using KSPTextureLoader.Format.AssetBundle;

namespace KSPTextureLoaderTests.AssetBundle;

/// <summary>
/// Tests for <see cref="Texture2DInfo.Extract"/>, the field walk at the heart of
/// the bundle index. Verifies the two cases (inline vs streamed) and the capped
/// header read that recovers an inline texture's metadata without ever touching
/// its pixels.
/// </summary>
public unsafe class Texture2DInfoTests : BundleParseTestBase
{
    // ---- Inline, fully-resident object ----------------------------------

    [TestInfo("Texture2DInfo_InlineComplete")]
    public void TestInlineComplete()
    {
        var t = SerializedFileFixture.InlineTexture(
            name: "inline_rgba",
            width: 8,
            height: 4,
            format: 4,
            mipCount: 1,
            pixels: SerializedFileFixture.Pattern(64)
        );

        fixed (byte* p = t.File.Bytes)
        {
            var file = SerializedFile.Parse(new EndianBinaryReader(p, t.File.Bytes.Length));
            var obj = file.Objects[0];
            var region = new EndianBinaryReader(p, t.File.Bytes.Length).Slice(
                file.ObjectDataOffset(obj),
                obj.ByteSize
            );

            var info = Texture2DInfo.Extract(file, obj, region, regionIsComplete: true);

            AssertEqual("name", info.Name, "inline_rgba");
            AssertEqual("width", info.Width, 8);
            AssertEqual("height", info.Height, 4);
            AssertEqual("format", info.TextureFormat, 4);
            AssertEqual("mipcount", info.MipCount, 1);
            AssertEqual("not-streamed", info.Streamed, false);
            AssertEqual("image-length", info.ImageDataLength, 64L);
            AssertEqual("image-offset", info.ImageDataOffset, t.ExpectedImageDataOffset);
        }
    }

    /// <summary>The recorded image-data offset/length point at exactly the bytes we wrote.</summary>
    [TestInfo("Texture2DInfo_InlineOffsetIsByteExact")]
    public void TestInlineOffsetIsByteExact()
    {
        var pixels = SerializedFileFixture.Pattern(48);
        var t = SerializedFileFixture.InlineTexture(name: "px", pixels: pixels);

        fixed (byte* p = t.File.Bytes)
        {
            var file = SerializedFile.Parse(new EndianBinaryReader(p, t.File.Bytes.Length));
            var obj = file.Objects[0];
            var region = new EndianBinaryReader(p, t.File.Bytes.Length).Slice(
                file.ObjectDataOffset(obj),
                obj.ByteSize
            );

            var info = Texture2DInfo.Extract(file, obj, region, regionIsComplete: true);

            // Read back the bytes the index would later fetch and compare.
            var fetched = new byte[info.ImageDataLength];
            for (int i = 0; i < fetched.Length; ++i)
                fetched[i] = t.File.Bytes[info.ImageDataOffset + i];

            AssertBytesEqual("inline-pixels", fetched, pixels);
        }
    }

    // ---- Inline, capped header read -------------------------------------

    [TestInfo("Texture2DInfo_InlineCappedDoesNotReadPixels")]
    public void TestInlineCappedDoesNotReadPixels()
    {
        var t = SerializedFileFixture.InlineTexture(
            name: "inline_capped",
            width: 16,
            height: 16,
            format: 4,
            mipCount: 1,
            pixels: SerializedFileFixture.Pattern(256)
        );

        fixed (byte* p = t.File.Bytes)
        {
            var file = SerializedFile.Parse(new EndianBinaryReader(p, t.File.Bytes.Length));
            var obj = file.Objects[0];

            // Cap the region just past the image-data count prefix: the pixels are
            // NOT in the buffer at all, proving the walk never needs them.
            long cap = t.PixelRegionOffset;
            AssertTrue("cap-smaller-than-object", cap < obj.ByteSize);

            var region = new EndianBinaryReader(p, t.File.Bytes.Length).Slice(
                file.ObjectDataOffset(obj),
                cap
            );

            var info = Texture2DInfo.Extract(file, obj, region, regionIsComplete: false);

            AssertEqual("name", info.Name, "inline_capped");
            AssertEqual("width", info.Width, 16);
            AssertEqual("height", info.Height, 16);
            AssertEqual("not-streamed", info.Streamed, false);
            AssertEqual("image-length", info.ImageDataLength, 256L);
            AssertEqual("image-offset", info.ImageDataOffset, t.ExpectedImageDataOffset);
        }
    }

    [TestInfo("Texture2DInfo_CappedHeaderTooSmallSignalsIncomplete")]
    public void TestCappedHeaderTooSmallSignalsIncomplete()
    {
        var t = SerializedFileFixture.InlineTexture(name: "inline_truncated");

        fixed (byte* p = t.File.Bytes)
        {
            var file = SerializedFile.Parse(new EndianBinaryReader(p, t.File.Bytes.Length));
            var obj = file.Objects[0];

            // Far too small to even read m_Name: the walk runs off the end and
            // asks the caller to re-read the whole object.
            var region = new EndianBinaryReader(p, t.File.Bytes.Length).Slice(
                file.ObjectDataOffset(obj),
                6
            );

            AssertThrows<IncompleteObjectRegionException>(
                "incomplete",
                () => Texture2DInfo.Extract(file, obj, region, regionIsComplete: false)
            );
        }
    }

    // ---- Streamed -------------------------------------------------------

    [TestInfo("Texture2DInfo_StreamedComplete")]
    public void TestStreamedComplete()
    {
        var t = SerializedFileFixture.StreamedTexture(
            name: "streamed_dxt1",
            width: 32,
            height: 32,
            format: 10,
            streamOffset: 8192,
            streamSize: 512,
            streamPath: "archive:/CAB-abc/streamed_dxt1.resS"
        );

        fixed (byte* p = t.File.Bytes)
        {
            var file = SerializedFile.Parse(new EndianBinaryReader(p, t.File.Bytes.Length));
            var obj = file.Objects[0];
            var region = new EndianBinaryReader(p, t.File.Bytes.Length).Slice(
                file.ObjectDataOffset(obj),
                obj.ByteSize
            );

            var info = Texture2DInfo.Extract(file, obj, region, regionIsComplete: true);

            AssertEqual("name", info.Name, "streamed_dxt1");
            AssertEqual("width", info.Width, 32);
            AssertEqual("height", info.Height, 32);
            AssertEqual("format", info.TextureFormat, 10);
            AssertEqual("streamed", info.Streamed, true);
            AssertEqual("stream-offset", info.StreamOffset, 8192L);
            AssertEqual("stream-size", info.StreamSize, 512L);
            AssertEqual("stream-path", info.StreamPath, "archive:/CAB-abc/streamed_dxt1.resS");
            AssertEqual("no-inline", info.ImageDataLength, 0L);
        }
    }

    /// <summary>
    /// A streamed object read with only a capped header has no inline pixels, so
    /// the walk can't reach <c>m_StreamData</c> and must request the full object.
    /// </summary>
    [TestInfo("Texture2DInfo_StreamedCappedSignalsIncomplete")]
    public void TestStreamedCappedSignalsIncomplete()
    {
        var t = SerializedFileFixture.StreamedTexture(name: "streamed_capped");

        fixed (byte* p = t.File.Bytes)
        {
            var file = SerializedFile.Parse(new EndianBinaryReader(p, t.File.Bytes.Length));
            var obj = file.Objects[0];

            var region = new EndianBinaryReader(p, t.File.Bytes.Length).Slice(
                file.ObjectDataOffset(obj),
                t.PixelRegionOffset
            );

            AssertThrows<IncompleteObjectRegionException>(
                "streamed-incomplete",
                () => Texture2DInfo.Extract(file, obj, region, regionIsComplete: false)
            );
        }
    }

    // ---- Mip count fallback ---------------------------------------------

    [TestInfo("Texture2DInfo_MipMapBooleanComputesMipCount")]
    public void TestMipMapBooleanComputesMipCount()
    {
        // Older layout with m_MipMap=true: mip count is derived from dimensions.
        var t = SerializedFileFixture.MipMapTexture(
            name: "mipped",
            width: 8,
            height: 8,
            mipMap: true
        );

        fixed (byte* p = t.File.Bytes)
        {
            var file = SerializedFile.Parse(new EndianBinaryReader(p, t.File.Bytes.Length));
            var obj = file.Objects[0];
            var region = new EndianBinaryReader(p, t.File.Bytes.Length).Slice(
                file.ObjectDataOffset(obj),
                obj.ByteSize
            );

            var info = Texture2DInfo.Extract(file, obj, region, regionIsComplete: true);

            // 8x8 -> 8,4,2,1 == 4 mip levels.
            AssertEqual("mipcount", info.MipCount, 4);
            AssertEqual("expected-matches-fixture", info.MipCount, t.ExpectedMipCount);
        }
    }

    [TestInfo("Texture2DInfo_MipMapFalseIsSingleLevel")]
    public void TestMipMapFalseIsSingleLevel()
    {
        var t = SerializedFileFixture.MipMapTexture(
            name: "flat",
            width: 8,
            height: 8,
            mipMap: false
        );

        fixed (byte* p = t.File.Bytes)
        {
            var file = SerializedFile.Parse(new EndianBinaryReader(p, t.File.Bytes.Length));
            var obj = file.Objects[0];
            var region = new EndianBinaryReader(p, t.File.Bytes.Length).Slice(
                file.ObjectDataOffset(obj),
                obj.ByteSize
            );

            var info = Texture2DInfo.Extract(file, obj, region, regionIsComplete: true);

            AssertEqual("mipcount", info.MipCount, 1);
        }
    }
}
