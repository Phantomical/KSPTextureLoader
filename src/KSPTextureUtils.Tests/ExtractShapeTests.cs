using KSPTextureUtils.Textures;
using Xunit;

namespace KSPTextureUtils.Tests;

/// <summary>
/// End-to-end cover for every texture shape: bundle a DDS, extract it again, and
/// require the result to be byte-identical to the input. This exercises the whole
/// chain — DdsReader, the bundle writer's per-kind field layouts, and the extract
/// path that has to recover the shape from the serialized object — so a mismatch
/// anywhere in it shows up as a differing file.
/// </summary>
public class ExtractShapeTests
{
    public static TheoryData<TextureKind, int, UnityTextureFormat> Cases =>
        new()
        {
            { TextureKind.Texture2D, 1, UnityTextureFormat.RGBA32 },
            { TextureKind.Texture2D, 1, UnityTextureFormat.DXT5 },
            { TextureKind.Cubemap, 1, UnityTextureFormat.RGBA32 },
            { TextureKind.Cubemap, 1, UnityTextureFormat.DXT1 },
            { TextureKind.Texture3D, 4, UnityTextureFormat.RGBA32 },
            { TextureKind.Texture2DArray, 3, UnityTextureFormat.RGBA32 },
            { TextureKind.Texture2DArray, 3, UnityTextureFormat.DXT5 },
            { TextureKind.CubemapArray, 2, UnityTextureFormat.RGBA32 },
        };

    [Theory]
    [MemberData(nameof(Cases))]
    public void BundleThenExtract_RoundTripsFile(
        TextureKind kind,
        int layers,
        UnityTextureFormat format
    )
    {
        const int Size = 8;
        const int Mips = 3;

        using var work = new ShapeFixtures.TempDir();
        string inputDir = Path.Combine(work.Path, "in");
        string outputDir = Path.Combine(work.Path, "out");
        Directory.CreateDirectory(inputDir);

        // The source DDS is produced by DdsWriter, which DdsShapeTests validates
        // against the raw header bytes independently.
        var payload = ShapeFixtures.Payload(format, kind, Size, Size, layers, Mips);
        byte[] source = DdsWriter.Write(format, Size, Size, Mips, payload, kind, layers);
        string sourcePath = Path.Combine(inputDir, "shape.dds");
        File.WriteAllBytes(sourcePath, source);

        string bundlePath = Path.Combine(work.Path, "shape.unity3d");
        int build = Commands.Build(
            [inputDir],
            bundlePath,
            name: null,
            seedPath: null,
            prefix: null,
            propertiesPath: null
        );
        Assert.Equal(0, build);

        int extract = Commands.Extract(bundlePath, outputDir, flat: true);
        Assert.Equal(0, extract);

        string extracted = Path.Combine(outputDir, "shape.dds");
        Assert.True(File.Exists(extracted), $"{kind} was not extracted");
        Assert.Equal(source, File.ReadAllBytes(extracted));
    }

    /// <summary>
    /// A cubemap built from a 4x3 PNG cross has a container key ending in .png, but
    /// six faces cannot be written to a PNG — it has to come back out as a DDS.
    /// </summary>
    [Fact]
    public void BundleThenExtract_CubemapFromPngCross_WritesDds()
    {
        const int Face = 4;

        using var work = new ShapeFixtures.TempDir();
        string inputDir = Path.Combine(work.Path, "in");
        string outputDir = Path.Combine(work.Path, "out");
        Directory.CreateDirectory(inputDir);

        // A 4x3 cross whose twelve cells each hold a distinct grey, so each packed
        // face can be traced back to the cell it came from.
        var cross = new byte[Face * 4 * Face * 3 * 4];
        for (int row = 0; row < 3; row++)
        for (int col = 0; col < 4; col++)
        {
            byte tag = (byte)(20 + (row * 4 + col) * 20);
            for (int y = 0; y < Face; y++)
            for (int x = 0; x < Face; x++)
            {
                int px = ((row * Face + y) * Face * 4 + col * Face + x) * 4;
                cross[px + 0] = cross[px + 1] = cross[px + 2] = tag;
                cross[px + 3] = 255;
            }
        }

        string pngPath = Path.Combine(inputDir, "cross.png");
        File.WriteAllBytes(
            pngPath,
            PngWriter.Write(UnityTextureFormat.RGBA32, Face * 4, Face * 3, cross)
        );

        string propsPath = Path.Combine(work.Path, "props.yaml");
        File.WriteAllText(propsPath, "properties:\n  - files: 'cross.png'\n    cubemap: true\n");

        string bundlePath = Path.Combine(work.Path, "cross.unity3d");
        Assert.Equal(
            0,
            Commands.Build(
                [inputDir],
                bundlePath,
                name: null,
                seedPath: null,
                prefix: null,
                propertiesPath: propsPath
            )
        );
        Assert.Equal(0, Commands.Extract(bundlePath, outputDir, flat: true));

        // Written as DDS despite the .png container key.
        string extracted = Path.Combine(outputDir, "cross.dds");
        Assert.True(File.Exists(extracted), "cubemap was not written as a DDS");
        Assert.False(File.Exists(Path.Combine(outputDir, "cross.png")));

        var (texture, skip) = DdsReader.Read(extracted);
        Assert.Null(skip);
        Assert.NotNull(texture);
        Assert.Equal(TextureKind.Cubemap, texture.Kind);
        Assert.Equal(Face, texture.Width);
        Assert.Equal(Face, texture.Height);

        // Faces are serialized +X,-X,+Y,-Y,+Z,-Z, taken from these cross cells
        // (column, row) — the layout documented in the README.
        var expected = new[]
        {
            (col: 3, row: 1), // +X
            (col: 1, row: 1), // -X
            (col: 2, row: 0), // +Y
            (col: 2, row: 2), // -Y
            (col: 2, row: 1), // +Z
            (col: 0, row: 1), // -Z
        };
        int faceBytes = Face * Face * 4;
        for (int i = 0; i < expected.Length; i++)
        {
            byte want = (byte)(20 + (expected[i].row * 4 + expected[i].col) * 20);
            var face = texture.Data.AsSpan(i * faceBytes, faceBytes);
            for (int p = 0; p < faceBytes; p += 4)
                Assert.Equal(want, face[p]);
        }
    }
}
