namespace KSPTextureLoader;

/// <summary>
/// A hint to the loader indicating if and when it intends to block on the
/// the texture being loaded.
/// </summary>
///
/// <remarks>
/// This is used to optimize how much main thread overhead will be used while
/// loading the texture. With more asynchronous loads more work can be pushed
/// to background jobs.
/// </remarks>
public enum TextureLoadHint
{
    /// <summary>
    /// Indicates that the caller intends to load the textures fully asynchronously
    /// and is ok with waiting extra frames to do so.
    /// </summary>
    Asynchronous,

    /// <summary>
    /// Indicates that the caller intends to load up a bunch of textures at once
    /// asynchronously.
    /// </summary>
    ///
    /// <remarks>
    /// This is the default. The main difference between it and <see cref="Asynchronous"/>
    /// is that loads any asset bundles synchronously so that texture loads can start
    /// immediately and not have to wait until later in the frame.
    /// </remarks>
    BatchAsynchronous,

    /// <summary>
    /// The caller intends to queue up a bunch of texture loads and then block on
    /// their completion.
    /// </summary>
    ///
    /// <remarks>
    /// This mode allows you to overlap a bunch of different texture loads for
    /// somewhat better performance. It will also change the implementation of
    /// some of the internals so that they can actually be waited on in a blocking
    /// fashion.
    /// </remarks>
    BatchSynchronous,

    /// <summary>
    /// The caller intends to load a single texture and immediately block on it.
    /// </summary>
    Synchronous,
}

internal static class TextureLoadHintExtensions
{
    public static bool LoadAssetBundlesAsync(this TextureLoadHint hint) =>
        hint <= TextureLoadHint.Asynchronous;

    public static bool LoadAssetsAsync(this TextureLoadHint hint) =>
        hint <= TextureLoadHint.BatchSynchronous;
}

public struct TextureLoadOptions()
{
    /// <summary>
    /// A list of asset bundles to attempt to load the texture from.
    /// </summary>
    public string[] AssetBundles { get; set; }

    /// <summary>
    /// Is this texture a linear texture or a sRGB texture?
    /// </summary>
    ///
    /// <remarks>
    /// This option is only used when loading loose textures off disk. For asset
    /// bundles these properties will be set in the editor before building.
    /// If not set then DDS textures will usually default to linear, unless they
    /// explicitly have a DX10 sRGB format.
    /// </remarks>
    public bool? Linear { get; set; } = null;

    /// <summary>
    /// Should this texture be marked as unreadable after import.
    /// </summary>
    ///
    /// <remarks>
    /// This has no effect on asset bundles.
    /// </remarks>
    public bool Unreadable { get; set; } = true;

    /// <summary>
    /// Allow textures to be implicitly converted to the requested texture type.
    /// </summary>
    public bool AllowImplicitConversions { get; set; } = true;

    /// <summary>
    /// A hint to the loader so that it can optimize loading patterns for the
    /// best results.
    /// </summary>
    public TextureLoadHint Hint { get; set; } = TextureLoadHint.BatchAsynchronous;
}
