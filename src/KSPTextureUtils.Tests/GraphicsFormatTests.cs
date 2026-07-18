using KSPTextureUtils.Textures;
using Xunit;

namespace KSPTextureUtils.Tests;

/// <summary>
/// <see cref="TextureFormatInfo.FromGraphicsFormat"/> has to be a faithful inverse
/// of <see cref="TextureFormatInfo.ToGraphicsFormat"/>, because the modern texture
/// objects (Texture3D, Texture2DArray, CubemapArray) serialize their format as a
/// GraphicsFormat and extract has nothing else to recover it from.
/// </summary>
public class GraphicsFormatTests
{
    /// <summary>
    /// Formats that survive the trip unchanged, paired with whether they have
    /// distinct sRGB and linear GraphicsFormat values. The ones that don't collapse
    /// to a single value, so their colour space is not recoverable.
    /// </summary>
    public static TheoryData<UnityTextureFormat, bool> RoundTrips =>
        new()
        {
            { UnityTextureFormat.R8, true },
            { UnityTextureFormat.RGB24, true },
            { UnityTextureFormat.RGBA32, true },
            { UnityTextureFormat.BGRA32, true },
            { UnityTextureFormat.DXT1, true },
            { UnityTextureFormat.DXT5, true },
            { UnityTextureFormat.BC7, true },
            { UnityTextureFormat.RG16, false },
            { UnityTextureFormat.R16, false },
            { UnityTextureFormat.RGB565, false },
            { UnityTextureFormat.RGBA4444, false },
            { UnityTextureFormat.RHalf, false },
            { UnityTextureFormat.RGHalf, false },
            { UnityTextureFormat.RGBAHalf, false },
            { UnityTextureFormat.RFloat, false },
            { UnityTextureFormat.RGFloat, false },
            { UnityTextureFormat.RGBAFloat, false },
            { UnityTextureFormat.RGB9e5Float, false },
            { UnityTextureFormat.BC4, false },
            { UnityTextureFormat.BC5, false },
            { UnityTextureFormat.BC6H, false },
        };

    [Theory]
    [MemberData(nameof(RoundTrips))]
    public void FromGraphicsFormat_InvertsToGraphicsFormat(
        UnityTextureFormat format,
        bool hasSrgbVariant
    )
    {
        foreach (int colorSpace in new[] { 0, 1 })
        {
            int graphics = TextureFormatInfo.ToGraphicsFormat(format, colorSpace);
            var mapped = TextureFormatInfo.FromGraphicsFormat(graphics);

            Assert.NotNull(mapped);
            Assert.Equal(format, mapped.Value.Format);

            // Only formats with both variants can carry the colour space back; the
            // rest have a single GraphicsFormat value and always report linear.
            int expectedSpace = hasSrgbVariant ? colorSpace : 0;
            Assert.Equal(expectedSpace, mapped.Value.ColorSpace);
        }
    }

    /// <summary>
    /// Two mappings are deliberately one-way, because Unity has no distinct
    /// GraphicsFormat for them: Alpha8 shares R8_UNorm and ARGB32 shares RGBA32's
    /// pair. They must come back as the shared format rather than as null.
    /// </summary>
    [Theory]
    [InlineData(UnityTextureFormat.Alpha8, UnityTextureFormat.R8)]
    [InlineData(UnityTextureFormat.ARGB32, UnityTextureFormat.RGBA32)]
    public void FromGraphicsFormat_AliasedFormats_ResolveToSharedFormat(
        UnityTextureFormat source,
        UnityTextureFormat expected
    )
    {
        int graphics = TextureFormatInfo.ToGraphicsFormat(source, colorSpace: 0);
        var mapped = TextureFormatInfo.FromGraphicsFormat(graphics);

        Assert.NotNull(mapped);
        Assert.Equal(expected, mapped.Value.Format);
    }

    [Fact]
    public void FromGraphicsFormat_UnknownValue_ReturnsNull()
    {
        // 0 is GraphicsFormat.None, which ToGraphicsFormat emits for anything it
        // cannot map; it must not decode back into a real format.
        Assert.Null(TextureFormatInfo.FromGraphicsFormat(0));
        Assert.Null(TextureFormatInfo.FromGraphicsFormat(9999));
    }

    /// <summary>
    /// ShapeSize decides how many bytes extract demands before writing a DDS, so it
    /// has to match the layout each kind actually stores.
    /// </summary>
    [Theory]
    [InlineData(TextureKind.Texture2D, 1, 1, 4 * 4 * 4)]
    [InlineData(TextureKind.Cubemap, 1, 1, 6 * 4 * 4 * 4)]
    [InlineData(TextureKind.Texture2DArray, 3, 1, 3 * 4 * 4 * 4)]
    [InlineData(TextureKind.CubemapArray, 2, 1, 12 * 4 * 4 * 4)]
    // A volume's single mip holds the whole slice stack: 4x4 x 4 slices.
    [InlineData(TextureKind.Texture3D, 4, 1, 4 * 4 * 4 * 4)]
    public void ShapeSize_MatchesSurfaceLayout(
        TextureKind kind,
        int layers,
        int mips,
        long expected
    )
    {
        long actual = TextureFormatInfo.ShapeSize(
            UnityTextureFormat.RGBA32,
            kind,
            width: 4,
            height: 4,
            layers,
            mips
        );
        Assert.Equal(expected, actual);
    }
}
