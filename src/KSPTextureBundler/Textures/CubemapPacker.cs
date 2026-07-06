namespace KSPTextureBundler.Textures;

/// <summary>
/// Repacks a 2D "cross" texture into a <see cref="TextureKind.Cubemap"/> at build
/// time, so KSPTextureLoader can load a native cubemap instead of doing the
/// conversion in-game.
///
/// <para>The source is a 4x3 horizontal cross (width = 4 face sizes, height = 3),
/// laid out exactly like <c>TextureUtils.ConvertTexture2dToCubemap</c> expects:
/// <code>
///        col0   col1   col2   col3
/// top     .      .      -Y      .
/// mid     -Z     -X     +Z      +X
/// bot     .      .      +Y      .
/// </code>
/// where "top/bottom" are in Unity texel space (row 0 = bottom, the first row of
/// the raw texture bytes). KSPTextureLoader loads DDS bytes without a vertical
/// flip, so the six face sub-rectangles are copied straight out of the source
/// bytes in the same row order the in-game <c>GetPixels</c>/<c>SetPixels</c> path
/// uses, producing byte-for-byte the same faces without decoding anything.</para>
///
/// <para>The face data keeps the source's Unity <see cref="UnityTextureFormat"/>
/// (no conversion to RGBA32), so a compressed cross stays compressed. Only mip 0
/// is used; the result is a single-mip cubemap.</para>
/// </summary>
internal static class CubemapPacker
{
    /// <summary>
    /// Face sub-rectangle origins in texels, in Unity's cube-face serialization
    /// order (+X, -X, +Y, -Y, +Z, -Z), expressed as multiples of the face size.
    /// Matches the grid cells read by <c>ConvertTexture2dToCubemap</c>.
    /// </summary>
    static readonly (int col, int row)[] FaceCells =
    [
        (3, 1), // +X
        (1, 1), // -X
        (2, 0), // +Y
        (2, 2), // -Y
        (2, 1), // +Z
        (0, 1), // -Z
    ];

    /// <summary>
    /// Turn a decoded 2D cross <paramref name="src"/> into a single-mip cubemap
    /// source, or return a <see cref="SkippedTexture"/> explaining why it can't be
    /// (not a 2D texture, wrong cross proportions, or a face size that isn't a
    /// multiple of the format's block size).
    /// </summary>
    public static (SourceTexture? texture, SkippedTexture? skip) PackCross(SourceTexture src)
    {
        if (src.Kind != TextureKind.Texture2D)
            return Skip(src, $"cubemap conversion needs a 2D source, but this is a {src.Kind}");

        int faceSize = src.Width / 4;
        if (faceSize <= 0 || src.Width != faceSize * 4 || src.Height != faceSize * 3)
            return Skip(
                src,
                $"cubemap source must be a 4x3 cross (width = 4*face, height = 3*face), "
                    + $"but this is {src.Width}x{src.Height}"
            );

        int blockW = TextureFormatInfo.BlockWidth(src.Format);
        int blockH = TextureFormatInfo.BlockHeight(src.Format);
        if (faceSize % blockW != 0 || faceSize % blockH != 0)
            return Skip(
                src,
                $"cubemap face size {faceSize} is not a multiple of the {src.Format} block "
                    + $"size ({blockW}x{blockH}); a compressed cross must have block-aligned faces"
            );

        // The face bytes come from mip 0 only (offset 0 of the mip chain).
        long crossMip0 = TextureFormatInfo.MipSize(src.Format, src.Width, src.Height);
        if (src.Data.Length < crossMip0)
            return Skip(
                src,
                $"cubemap source has {src.Data.Length} bytes but its base mip needs {crossMip0}"
            );

        byte[] faces = ExtractFaces(src, faceSize);

        var cube = new SourceTexture
        {
            Name = src.Name,
            Kind = TextureKind.Cubemap,
            Width = faceSize,
            Height = faceSize,
            MipCount = 1,
            Format = src.Format,
            ColorSpace = src.ColorSpace,
            Data = faces,
            SourcePath = src.SourcePath,
            AddressableName = src.AddressableName,
        };
        return (cube, null);
    }

    /// <summary>
    /// Copy the six face sub-rectangles out of the cross's mip-0 bytes and
    /// concatenate them in cube-face order. Works in "elements" (a single texel
    /// for uncompressed formats, one 4x4 block for compressed ones) so the same
    /// row-copy handles both.
    /// </summary>
    static byte[] ExtractFaces(SourceTexture src, int faceSize)
    {
        int ew = TextureFormatInfo.BlockWidth(src.Format);
        int eh = TextureFormatInfo.BlockHeight(src.Format);
        int elemBytes = TextureFormatInfo.BlockOrPixelSize(src.Format);

        // faceSize is block-aligned (checked by PackCross), so a face is faceCols x
        // faceRows whole elements. Source and destination are byte[] so their length
        // fits an int; the cross's rows are strided by the full width, so each face
        // row is copied on its own (they aren't contiguous in the source).
        int crossRowBytes = src.Width / ew * elemBytes;
        int faceCols = faceSize / ew;
        int faceRows = faceSize / eh;
        int faceRowBytes = faceCols * elemBytes;

        var faces = new byte[faceRowBytes * faceRows * FaceCells.Length];
        var dst = faces.AsSpan();
        int outPos = 0;
        foreach (var (col, row) in FaceCells)
        {
            int colByte = col * faceCols * elemBytes;
            int rowElem = row * faceRows;
            for (int r = 0; r < faceRows; r++)
            {
                int srcOff = (rowElem + r) * crossRowBytes + colByte;
                src.Data.AsSpan(srcOff, faceRowBytes).CopyTo(dst.Slice(outPos, faceRowBytes));
                outPos += faceRowBytes;
            }
        }
        return faces;
    }

    static (SourceTexture?, SkippedTexture?) Skip(SourceTexture src, string detail) =>
        (
            null,
            new SkippedTexture
            {
                SourcePath = src.SourcePath,
                Reason = SkipReason.Invalid,
                Detail = detail,
            }
        );
}
