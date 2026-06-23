namespace KSPTextureBundler.Textures;

/// <summary>
/// The subset of <c>UnityEngine.TextureFormat</c> (classic enum) that the bundler
/// can emit. The integer values match Unity 2019.4's serialized <c>m_TextureFormat</c>
/// and are what <c>KSPTextureLoader</c> casts back to <c>TextureFormat</c> when it
/// builds the CPU texture from streamed bytes.
/// </summary>
internal enum UnityTextureFormat
{
    Alpha8 = 1,
    RGB24 = 3,
    RGBA32 = 4,
    ARGB32 = 5,
    RGB565 = 7,
    R16 = 9,
    DXT1 = 10,
    DXT5 = 12,
    RGBA4444 = 13,
    BGRA32 = 14,
    RHalf = 15,
    RGHalf = 16,
    RGBAHalf = 17,
    RFloat = 18,
    RGFloat = 19,
    RGBAFloat = 20,
    RGB9e5Float = 22,
    BC6H = 24,
    BC7 = 25,
    BC4 = 26,
    BC5 = 27,
    RG16 = 62,
    R8 = 63,
}

internal static class TextureFormatInfo
{
    /// <summary>Block width in texels (1 for uncompressed, 4 for BC/DXT).</summary>
    public static int BlockWidth(UnityTextureFormat f) => IsBlockCompressed(f) ? 4 : 1;

    public static int BlockHeight(UnityTextureFormat f) => IsBlockCompressed(f) ? 4 : 1;

    public static bool IsBlockCompressed(UnityTextureFormat f) =>
        f
            is UnityTextureFormat.DXT1
                or UnityTextureFormat.DXT5
                or UnityTextureFormat.BC4
                or UnityTextureFormat.BC5
                or UnityTextureFormat.BC6H
                or UnityTextureFormat.BC7;

    /// <summary>Bytes per 4x4 block (compressed) or bytes per texel (uncompressed).</summary>
    public static int BlockOrPixelSize(UnityTextureFormat f) =>
        f switch
        {
            UnityTextureFormat.DXT1 or UnityTextureFormat.BC4 => 8,
            UnityTextureFormat.DXT5
            or UnityTextureFormat.BC5
            or UnityTextureFormat.BC6H
            or UnityTextureFormat.BC7 => 16,

            UnityTextureFormat.Alpha8 or UnityTextureFormat.R8 => 1,
            UnityTextureFormat.R16
            or UnityTextureFormat.RG16
            or UnityTextureFormat.RGB565
            or UnityTextureFormat.RGBA4444
            or UnityTextureFormat.RHalf => 2,
            UnityTextureFormat.RGB24 => 3,
            UnityTextureFormat.RGBA32
            or UnityTextureFormat.ARGB32
            or UnityTextureFormat.BGRA32
            or UnityTextureFormat.RGHalf
            or UnityTextureFormat.RFloat
            or UnityTextureFormat.RGB9e5Float => 4,
            UnityTextureFormat.RGBAHalf or UnityTextureFormat.RGFloat => 8,
            UnityTextureFormat.RGBAFloat => 16,
            _ => throw new ArgumentOutOfRangeException(nameof(f), f, "unknown texture format size"),
        };

    /// <summary>
    /// Size in bytes of a single mip level at <paramref name="width"/> x
    /// <paramref name="height"/>, matching Unity's block-aligned layout.
    /// </summary>
    public static long MipSize(UnityTextureFormat f, int width, int height)
    {
        int bw = BlockWidth(f);
        int bh = BlockHeight(f);
        long blocksX = Math.Max(1, (width + bw - 1) / bw);
        long blocksY = Math.Max(1, (height + bh - 1) / bh);
        return blocksX * blocksY * BlockOrPixelSize(f);
    }

    /// <summary>Total size of a full mip chain of <paramref name="mipCount"/> levels.</summary>
    public static long MipChainSize(UnityTextureFormat f, int width, int height, int mipCount)
    {
        long total = 0;
        int w = width,
            h = height;
        for (int i = 0; i < mipCount; i++)
        {
            total += MipSize(f, w, h);
            w = Math.Max(1, w >> 1);
            h = Math.Max(1, h >> 1);
        }
        return total;
    }
}
