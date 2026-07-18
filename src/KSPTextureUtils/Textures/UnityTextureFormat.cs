namespace KSPTextureUtils.Textures;

/// <summary>
/// The subset of <c>UnityEngine.TextureFormat</c> (classic enum) that the bundler
/// can emit. The integer values match Unity 2019.4's serialized <c>m_TextureFormat</c>
/// and are what <c>KSPTextureLoader</c> casts back to <c>TextureFormat</c> when it
/// builds the CPU texture from streamed bytes.
/// </summary>
public enum UnityTextureFormat
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

    /// <summary>
    /// Total size of a volume (3D) mip chain: each mip level holds a full slice
    /// stack, and the depth halves alongside width/height down the chain.
    /// </summary>
    public static long VolumeMipChainSize(
        UnityTextureFormat f,
        int width,
        int height,
        int depth,
        int mipCount
    )
    {
        long total = 0;
        int w = width,
            h = height,
            d = depth;
        for (int i = 0; i < mipCount; i++)
        {
            total += MipSize(f, w, h) * d;
            w = Math.Max(1, w >> 1);
            h = Math.Max(1, h >> 1);
            d = Math.Max(1, d >> 1);
        }
        return total;
    }

    /// <summary>
    /// Map a classic <see cref="UnityTextureFormat"/> to the integer value of Unity's
    /// <c>GraphicsFormat</c> enum, honouring <paramref name="colorSpace"/> (1 = sRGB,
    /// 0 = linear) for the formats that have both variants. The "modern" texture
    /// objects (Texture3D, Texture2DArray, CubemapArray) serialize their format in a
    /// <c>m_Format</c> field that holds a <c>GraphicsFormat</c>, not a
    /// <c>TextureFormat</c>; Texture2D and Cubemap keep the classic
    /// <c>m_TextureFormat</c>. Values verified against UnityEngine 2019.4's enum.
    /// </summary>
    public static int ToGraphicsFormat(UnityTextureFormat f, int colorSpace)
    {
        bool srgb = colorSpace == 1;
        return f switch
        {
            UnityTextureFormat.R8 => srgb ? 1 : 5, // R8_SRGB : R8_UNorm
            UnityTextureFormat.Alpha8 => 5, // R8_UNorm (no dedicated A8 GraphicsFormat)
            UnityTextureFormat.RGB24 => srgb ? 3 : 7, // R8G8B8_SRGB : R8G8B8_UNorm
            UnityTextureFormat.RGBA32 or UnityTextureFormat.ARGB32 => srgb ? 4 : 8, // R8G8B8A8_*
            UnityTextureFormat.BGRA32 => srgb ? 57 : 59, // B8G8R8A8_SRGB : B8G8R8A8_UNorm
            UnityTextureFormat.RG16 => 6, // R8G8_UNorm
            UnityTextureFormat.R16 => 21, // R16_UNorm
            UnityTextureFormat.RGB565 => 68, // R5G6B5_UNormPack16
            UnityTextureFormat.RGBA4444 => 66, // R4G4B4A4_UNormPack16
            UnityTextureFormat.RHalf => 45, // R16_SFloat
            UnityTextureFormat.RGHalf => 46, // R16G16_SFloat
            UnityTextureFormat.RGBAHalf => 48, // R16G16B16A16_SFloat
            UnityTextureFormat.RFloat => 49, // R32_SFloat
            UnityTextureFormat.RGFloat => 50, // R32G32_SFloat
            UnityTextureFormat.RGBAFloat => 52, // R32G32B32A32_SFloat
            UnityTextureFormat.RGB9e5Float => 73, // E5B9G9R9_UFloatPack32
            UnityTextureFormat.DXT1 => srgb ? 96 : 97, // RGBA_DXT1_SRGB : RGBA_DXT1_UNorm
            UnityTextureFormat.DXT5 => srgb ? 100 : 101, // RGBA_DXT5_SRGB : RGBA_DXT5_UNorm
            UnityTextureFormat.BC4 => 102, // R_BC4_UNorm
            UnityTextureFormat.BC5 => 104, // RG_BC5_UNorm
            UnityTextureFormat.BC6H => 106, // RGB_BC6H_UFloat
            UnityTextureFormat.BC7 => srgb ? 108 : 109, // RGBA_BC7_SRGB : RGBA_BC7_UNorm
            _ => 0, // None
        };
    }

    /// <summary>
    /// The inverse of <see cref="ToGraphicsFormat"/>: turn a serialized
    /// <c>GraphicsFormat</c> value back into a classic format plus its colour space
    /// (1 = sRGB, 0 = linear), or null if it maps to nothing we handle. Two mappings
    /// are one-way and resolve to the more common format: Alpha8 shares R8_UNorm, and
    /// ARGB32 shares RGBA32's pair, so both come back as R8/RGBA32.
    /// </summary>
    public static (UnityTextureFormat Format, int ColorSpace)? FromGraphicsFormat(int graphics) =>
        graphics switch
        {
            1 => (UnityTextureFormat.R8, 1),
            5 => (UnityTextureFormat.R8, 0),
            3 => (UnityTextureFormat.RGB24, 1),
            7 => (UnityTextureFormat.RGB24, 0),
            4 => (UnityTextureFormat.RGBA32, 1),
            8 => (UnityTextureFormat.RGBA32, 0),
            57 => (UnityTextureFormat.BGRA32, 1),
            59 => (UnityTextureFormat.BGRA32, 0),
            6 => (UnityTextureFormat.RG16, 0),
            21 => (UnityTextureFormat.R16, 0),
            68 => (UnityTextureFormat.RGB565, 0),
            66 => (UnityTextureFormat.RGBA4444, 0),
            45 => (UnityTextureFormat.RHalf, 0),
            46 => (UnityTextureFormat.RGHalf, 0),
            48 => (UnityTextureFormat.RGBAHalf, 0),
            49 => (UnityTextureFormat.RFloat, 0),
            50 => (UnityTextureFormat.RGFloat, 0),
            52 => (UnityTextureFormat.RGBAFloat, 0),
            73 => (UnityTextureFormat.RGB9e5Float, 0),
            96 => (UnityTextureFormat.DXT1, 1),
            97 => (UnityTextureFormat.DXT1, 0),
            100 => (UnityTextureFormat.DXT5, 1),
            101 => (UnityTextureFormat.DXT5, 0),
            102 => (UnityTextureFormat.BC4, 0),
            104 => (UnityTextureFormat.BC5, 0),
            106 => (UnityTextureFormat.BC6H, 0),
            108 => (UnityTextureFormat.BC7, 1),
            109 => (UnityTextureFormat.BC7, 0),
            _ => null,
        };

    /// <summary>
    /// Total payload size for a texture of the given shape: the number of complete
    /// mip chains a kind stores back to back (6 per cubemap, one per array slice),
    /// or the interleaved slice stack of a volume.
    /// </summary>
    public static long ShapeSize(
        UnityTextureFormat f,
        TextureKind kind,
        int width,
        int height,
        int layers,
        int mipCount
    ) =>
        kind switch
        {
            TextureKind.Texture3D => VolumeMipChainSize(f, width, height, layers, mipCount),
            TextureKind.Cubemap => 6 * MipChainSize(f, width, height, mipCount),
            TextureKind.CubemapArray => 6L * layers * MipChainSize(f, width, height, mipCount),
            TextureKind.Texture2DArray => layers * MipChainSize(f, width, height, mipCount),
            _ => MipChainSize(f, width, height, mipCount),
        };
}
