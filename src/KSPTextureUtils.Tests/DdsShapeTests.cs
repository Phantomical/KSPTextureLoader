using System.Buffers.Binary;
using KSPTextureUtils.Textures;
using Xunit;

namespace KSPTextureUtils.Tests;

/// <summary>
/// Validates that <see cref="DdsWriter"/> emits a correct DDS header for every
/// texture shape and that <see cref="DdsReader"/> reads it back unchanged. The
/// round trip is the real assertion: kind, dimensions, layer count, format and
/// every pixel byte must survive, which pins down both the header fields and the
/// surface ordering the payload is laid out in.
/// </summary>
public class DdsShapeTests
{
    // DDS_HEADER field offsets, counting the 4-byte magic.
    const int OffHeight = 12;
    const int OffWidth = 16;
    const int OffDepth = 24;
    const int OffMipCount = 28;
    const int OffFourCC = 84;
    const int OffCaps = 108;
    const int OffCaps2 = 112;
    const int OffDx10 = 128; // dxgiFormat, resourceDimension, miscFlag, arraySize

    const uint DDSCAPS2_CUBEMAP_ALLFACES = 0xFE00; // cubemap bit + all six faces
    const uint DDSCAPS2_VOLUME = 0x200000;

    /// <summary>Every shape, with a layer count that is meaningful for it.</summary>
    public static TheoryData<TextureKind, int> Shapes =>
        new()
        {
            { TextureKind.Texture2D, 1 },
            { TextureKind.Cubemap, 1 },
            { TextureKind.Texture3D, 4 },
            { TextureKind.Texture2DArray, 3 },
            { TextureKind.CubemapArray, 2 },
        };

    /// <summary>
    /// A spread of formats: uncompressed, block-compressed with a legacy FourCC
    /// (DXT1/DXT5) and block-compressed that always needs a DX10 header (BC7).
    /// </summary>
    public static TheoryData<UnityTextureFormat> Formats =>
        new()
        {
            UnityTextureFormat.RGBA32,
            UnityTextureFormat.BGRA32,
            UnityTextureFormat.DXT1,
            UnityTextureFormat.DXT5,
            UnityTextureFormat.BC7,
            UnityTextureFormat.RGBAHalf,
        };

    public static TheoryData<TextureKind, int, UnityTextureFormat> ShapesAndFormats()
    {
        var data = new TheoryData<TextureKind, int, UnityTextureFormat>();
        foreach (var shape in Shapes)
        foreach (var format in Formats)
            data.Add((TextureKind)shape[0]!, (int)shape[1]!, (UnityTextureFormat)format[0]!);
        return data;
    }

    [Theory]
    [MemberData(nameof(ShapesAndFormats))]
    public void Write_ThenRead_RoundTripsShapeAndPixels(
        TextureKind kind,
        int layers,
        UnityTextureFormat format
    )
    {
        const int Size = 8;
        const int Mips = 3;

        var payload = ShapeFixtures.Payload(format, kind, Size, Size, layers, Mips);
        byte[] dds = DdsWriter.Write(format, Size, Size, Mips, payload, kind, layers);

        var (texture, skip) = ReadFromBytes(dds);
        Assert.Null(skip);
        Assert.NotNull(texture);

        Assert.Equal(kind, texture.Kind);
        Assert.Equal(format, texture.Format);
        Assert.Equal(Size, texture.Width);
        Assert.Equal(Mips, texture.MipCount);
        // Height is not serialized for a cubemap array (its faces are square).
        Assert.Equal(Size, texture.Height);
        if (kind is TextureKind.Texture3D or TextureKind.Texture2DArray or TextureKind.CubemapArray)
            Assert.Equal(layers, texture.Layers);

        // The strong assertion: every face/slice/mip byte back where it started.
        Assert.Equal(payload, texture.Data);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Write_SetsShapeHeaderFields(TextureKind kind, int layers)
    {
        const int Size = 8;
        const int Mips = 2;
        var format = UnityTextureFormat.RGBA32;

        var payload = ShapeFixtures.Payload(format, kind, Size, Size, layers, Mips);
        byte[] dds = DdsWriter.Write(format, Size, Size, Mips, payload, kind, layers);

        Assert.Equal((uint)Size, Read(dds, OffWidth));
        Assert.Equal((uint)Size, Read(dds, OffHeight));
        Assert.Equal((uint)Mips, Read(dds, OffMipCount));

        uint caps2 = Read(dds, OffCaps2);
        uint depth = Read(dds, OffDepth);
        (uint dim, uint misc, uint arraySize) = Dx10(dds);

        switch (kind)
        {
            case TextureKind.Cubemap:
                Assert.Equal(DDSCAPS2_CUBEMAP_ALLFACES, caps2);
                Assert.Equal(4u, misc); // DDS_RESOURCE_MISC_TEXTURECUBE
                Assert.Equal(3u, dim); // a cube is six 2D surfaces, not a volume
                Assert.Equal(1u, arraySize); // arraySize counts cubes, not faces
                break;

            case TextureKind.CubemapArray:
                Assert.Equal(DDSCAPS2_CUBEMAP_ALLFACES, caps2);
                Assert.Equal(4u, misc);
                Assert.Equal(3u, dim);
                Assert.Equal((uint)layers, arraySize);
                break;

            case TextureKind.Texture3D:
                Assert.Equal(DDSCAPS2_VOLUME, caps2);
                Assert.Equal((uint)layers, depth);
                Assert.Equal(4u, dim); // DDS_DIMENSION_TEXTURE3D
                Assert.Equal(0u, misc);
                break;

            case TextureKind.Texture2DArray:
                Assert.Equal(0u, caps2);
                Assert.Equal((uint)layers, arraySize);
                Assert.Equal(3u, dim);
                Assert.Equal(0u, misc);
                break;

            case TextureKind.Texture2D:
                Assert.Equal(0u, caps2);
                Assert.Equal(0u, depth);
                Assert.Equal(1u, arraySize);
                break;
        }

        // Anything holding more than one surface must be flagged complex.
        if (kind != TextureKind.Texture2D)
            Assert.NotEqual(0u, Read(dds, OffCaps) & 0x8);
    }

    /// <summary>
    /// DXT1/DXT5 normally go out with a legacy FourCC, but a legacy header has
    /// nowhere to put an array size — so an array has to fall back to the DX10
    /// header, and must carry a real DXGI format rather than leaving it UNKNOWN.
    /// </summary>
    [Theory]
    [InlineData(UnityTextureFormat.DXT1, 71u)] // BC1_UNORM
    [InlineData(UnityTextureFormat.DXT5, 77u)] // BC3_UNORM
    public void Write_ArrayOfLegacyFourCcFormat_FallsBackToDx10(
        UnityTextureFormat format,
        uint expectedDxgi
    )
    {
        const int Size = 8;
        const int Mips = 2;
        const int Layers = 3;

        var payload = ShapeFixtures.Payload(
            format,
            TextureKind.Texture2DArray,
            Size,
            Size,
            Layers,
            Mips
        );
        byte[] dds = DdsWriter.Write(
            format,
            Size,
            Size,
            Mips,
            payload,
            TextureKind.Texture2DArray,
            Layers
        );

        Assert.Equal("DX10", System.Text.Encoding.ASCII.GetString(dds, OffFourCC, 4));
        Assert.Equal(expectedDxgi, Read(dds, OffDx10));
        Assert.Equal((uint)Layers, Read(dds, OffDx10 + 12));

        // And the reader still recovers the original format, not UNKNOWN.
        var (texture, _) = ReadFromBytes(dds);
        Assert.NotNull(texture);
        Assert.Equal(format, texture.Format);
        Assert.Equal(TextureKind.Texture2DArray, texture.Kind);
        Assert.Equal(payload, texture.Data);
    }

    /// <summary>
    /// A single cubemap keeps the legacy header when its format has a FourCC — the
    /// cubemap flags live in dwCaps2, so no DX10 header is needed.
    /// </summary>
    [Fact]
    public void Write_CubemapOfLegacyFourCcFormat_KeepsLegacyHeader()
    {
        const int Size = 8;
        var format = UnityTextureFormat.DXT5;
        var payload = ShapeFixtures.Payload(format, TextureKind.Cubemap, Size, Size, 1, 1);

        byte[] dds = DdsWriter.Write(format, Size, Size, 1, payload, TextureKind.Cubemap, 1);

        Assert.Equal("DXT5", System.Text.Encoding.ASCII.GetString(dds, OffFourCC, 4));
        Assert.Equal(DDSCAPS2_CUBEMAP_ALLFACES, Read(dds, OffCaps2));

        var (texture, _) = ReadFromBytes(dds);
        Assert.NotNull(texture);
        Assert.Equal(TextureKind.Cubemap, texture.Kind);
        Assert.Equal(payload, texture.Data);
    }

    static uint Read(byte[] dds, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(offset, 4));

    static (uint dim, uint misc, uint arraySize) Dx10(byte[] dds)
    {
        // Only present when the pixel format is "DX10"; otherwise report the
        // defaults a legacy header implies.
        if (System.Text.Encoding.ASCII.GetString(dds, OffFourCC, 4) != "DX10")
            return (3u, 0u, 1u);
        return (Read(dds, OffDx10 + 4), Read(dds, OffDx10 + 8), Read(dds, OffDx10 + 12));
    }

    static (SourceTexture? texture, SkippedTexture? skip) ReadFromBytes(byte[] dds)
    {
        using var dir = new ShapeFixtures.TempDir();
        string path = dir.File("shape.dds");
        File.WriteAllBytes(path, dds);
        return DdsReader.Read(path);
    }
}
