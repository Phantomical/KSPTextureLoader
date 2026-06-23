namespace KSPTextureBundler.Textures;

/// <summary>
/// Which Unity texture object a source maps to. Drives both the serialized class
/// (Texture2D 28, Cubemap 89, Texture3D 117, Texture2DArray 187, CubemapArray 188)
/// and the field layout written into the bundle.
/// </summary>
internal enum TextureKind
{
    Texture2D,
    Cubemap,
    Texture2DArray,
    Texture3D,
    CubemapArray,
}

/// <summary>
/// A decoded source texture ready to be written into a bundle: its Unity
/// serialized <see cref="TextureFormat"/>, dimensions, mip count and the raw
/// mip-chain bytes exactly as they will live in the <c>.resS</c> stream.
/// </summary>
internal sealed class SourceTexture
{
    /// <summary>The serialized Texture2D <c>m_Name</c> (the input file name without extension).</summary>
    public required string Name { get; init; }

    /// <summary>The Unity texture object kind this source maps to (default: plain 2D).</summary>
    public TextureKind Kind { get; init; } = TextureKind.Texture2D;

    /// <summary>
    /// Per-kind surface count: array layers for <see cref="TextureKind.Texture2DArray"/>,
    /// depth slices for <see cref="TextureKind.Texture3D"/>, and the number of
    /// cubemaps for <see cref="TextureKind.CubemapArray"/>. Unused (1) for plain 2D
    /// and single cubemaps. <see cref="Data"/> holds every surface's mip chain
    /// concatenated in Unity/DDS order (cube faces +X,-X,+Y,-Y,+Z,-Z; array slices
    /// in index order; 3D mips outer with depth slices within each mip).
    /// </summary>
    public int Layers { get; init; } = 1;

    /// <summary>
    /// The key the texture is registered under in the AssetBundle container (the
    /// "addressable name"). Defaults to <see cref="Name"/>; the CLI can set it to a
    /// lowercased GameData-relative path (e.g.
    /// <c>parallax_stockplanettextures/bop/plugindata/bop_color.dds</c>) to mirror
    /// the EditorExtensions bundler. The KSPTextureLoader index resolves a texture
    /// by the last path component without extension, so either form works.
    /// </summary>
    public string AddressableName { get; set; } = "";
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int MipCount { get; init; }

    /// <summary>The classic Unity <c>TextureFormat</c> enum value to serialize.</summary>
    public required UnityTextureFormat Format { get; init; }

    /// <summary>
    /// 0 = linear, 1 = sRGB. Stored in the Texture2D's <c>m_ColorSpace</c>. The
    /// KSPTextureLoader CPU path ignores this, but Unity's own loader honours it.
    /// </summary>
    public required int ColorSpace { get; init; }

    /// <summary>Raw mip-chain bytes (largest mip first), copied verbatim into the resS.</summary>
    public required byte[] Data { get; init; }

    /// <summary>The original file the texture was decoded from (for diagnostics).</summary>
    public required string SourcePath { get; init; }
}

/// <summary>
/// The reason a source file was skipped rather than added to the bundle.
/// </summary>
internal enum SkipReason
{
    /// <summary>A format with no classic Unity <c>TextureFormat</c> equivalent (e.g. BC2/DXT3).</summary>
    UnsupportedFormat,

    /// <summary>The file could not be parsed as the claimed format.</summary>
    Invalid,
}

internal sealed class SkippedTexture
{
    public required string SourcePath { get; init; }
    public required SkipReason Reason { get; init; }
    public required string Detail { get; init; }
}
