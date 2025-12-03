namespace AsyncTextureLoad;

/// <summary>
/// Config options for AsyncTextureLoad internals.
/// </summary>
public static class Config
{
    /// <summary>
    /// How many frames should we hold on to asset bundles for before they are
    /// unloaded.
    /// </summary>
    ///
    /// <remarks>
    /// You want this to be set so that it is longer than the time it takes to
    /// load all assets from asset bundles. When an asset bundle is unloaded it
    /// needs to sync with the loading thread which can take a while.
    /// </remarks>
    public static int AssetBundleUnloadDelay = 30;
}
