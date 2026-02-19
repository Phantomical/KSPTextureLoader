using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Profiling;
using UnityEngine;

namespace KSPTextureLoader;

[KSPAddon(KSPAddon.Startup.Instantly, once: true)]
public partial class TextureLoader : MonoBehaviour
{
    internal static TextureLoader Instance { get; private set; }

    private static readonly Type[] SupportedTextureTypes =
    [
        typeof(Texture),
        typeof(Texture2D),
        typeof(Texture2DArray),
        typeof(Texture3D),
        typeof(Cubemap),
        typeof(CubemapArray),
    ];

    void Awake()
    {
        if (Instance != null)
        {
            DestroyImmediate(this);
            throw new InvalidOperationException(
                "You cannot create a TextureLoader when one already exists"
            );
        }

        DontDestroyOnLoad(this);
        Instance = this;
    }

    void OnDestroy()
    {
        Debug.LogError(
            $"[KSPTextureLoader] TextureLoader was destroyed! This should never happen."
        );
        Instance = null;
    }

    /// <summary>
    /// Load an asset bundle from a path within GameData.
    /// </summary>
    /// <param name="path">The path to the asset bundle within GameData</param>
    /// <param name="sync">Should this asset bundle be loaded synchronously?</param>
    /// <returns></returns>
    ///
    /// <remarks>
    /// Once an asset bundle is loaded it cannot be loaded again until it is
    /// explicitly loaded. If you are explicitly loading asset bundles that may
    /// be used to load textures (in your mod or another mod) then you should
    /// use this function to actually load them.
    ///
    /// This will also let you share the loaded asset bundle cache with other
    /// mods which may happen to load the same aset bundle.
    /// </remarks>
    public static AssetBundleHandle LoadAssetBundle(string path, bool sync = true) =>
        Instance.LoadAssetBundleImpl(path, sync);

    /// <summary>
    /// Load a texture from disk or from an asset bundle.
    /// </summary>
    /// <typeparam name="T">A specific texture type to load.</typeparam>
    /// <param name="path">The path to load the texture from on disk or in asset bundles.</param>
    /// <param name="options">Additional options configuring how the texture gets loaded.</param>
    ///
    /// <remarks>
    /// Depending on the type you pick for <typeparamref name="T"/> there are several
    /// texture conversions that can happen:
    ///
    /// <list type="bullet">
    ///   <item>
    ///     <see cref="Cubemap"/>: can be converted from a <see cref="Texture2D"/> with the faces
    ///     packed horizontally in a cross configuration.
    ///   </item>
    ///   <item>
    ///     <see cref="Texture2DArray"/>: can be converted from a <see cref="Texture2D"/>. This
    ///     will result in an array texture with depth 1.
    ///   </item>
    /// </list>
    ///
    /// If you are passing the texture directly into a shader property then usually you should be
    /// able to get away with leaving <typeparamref name="T"/> as <see cref="Texture"/>.
    /// </remarks>
    public static TextureHandle<T> LoadTexture<T>(string path, TextureLoadOptions options)
        where T : Texture
    {
        if (!SupportedTextureTypes.Contains(typeof(T)))
            throw new NotSupportedException($"Cannot load a texture of type {typeof(T).Name}");

        return Instance.LoadTextureImpl<T>(path, options);
    }

    /// <summary>
    /// Load a texture from disk or from an asset bundle.
    /// </summary>
    /// <typeparam name="T">A specific texture type to load.</typeparam>
    /// <param name="path">The path to load the texture from on disk or in asset bundles.</param>
    ///
    /// <remarks>
    /// Depending on the type you pick for <typeparamref name="T"/> there are several
    /// texture conversions that can happen:
    ///
    /// <list type="bullet">
    ///   <item>
    ///     <see cref="Cubemap"/>: can be converted from a <see cref="Texture2D"/> with the faces
    ///     packed horizontally in a cross configuration.
    ///   </item>
    ///   <item>
    ///     <see cref="Texture2DArray"/>: can be converted from a <see cref="Texture2D"/>. This
    ///     will result in an array texture with depth 1.
    ///   </item>
    /// </list>
    ///
    /// If you are passing the texture directly into a shader property then usually you should be
    /// able to get away with leaving <typeparamref name="T"/> as <see cref="Texture"/>.
    /// </remarks>
    public static TextureHandle<T> LoadTexture<T>(string path)
        where T : Texture => LoadTexture<T>(path, new());

    /// <summary>
    /// Check whether a texture exists.
    /// </summary>
    /// <param name="path">A path to load the texture from on disk or in asset bundles.</param>
    /// <param name="options">Additional options configuring how the texture gets loaded.</param>
    /// <returns>Whether a texture with that name exists on disk or in an asset bundle.</returns>
    ///
    /// <remarks>
    /// With implicit bundles it can be rather difficult to tell whether a
    /// texture actually exists to be loaded. This method is meant to bridge
    /// that gap. It does not actually load the texture, but it will load asset
    /// bundles that might contain the texture.
    ///
    /// Note that just because the texture exists that doesn't mean that it
    /// will load successfully. Don't use this to check just before loading
    /// the texture. Instead you should just try to load the texture.
    ///
    /// This is meant for load-time checks so that you can display a warning to
    /// the user if their install is incorrect.
    /// </remarks>
    public static bool TextureExists(string path, TextureLoadOptions options) =>
        Instance.DoTextureExists(path, options);

    /// <summary>
    /// Check whether a texture exists.
    /// </summary>
    /// <param name="path">A path to load the texture from on disk or in asset bundles.</param>
    /// <returns>Whether a texture with that name exists on disk or in an asset bundle.</returns>
    ///
    /// <remarks>
    /// With implicit bundles it can be rather difficult to tell whether a
    /// texture actually exists to be loaded. This method is meant to bridge
    /// that gap. It does not actually load the texture, but it will load asset
    /// bundles that might contain the texture.
    ///
    /// Note that just because the texture exists that doesn't mean that it
    /// will load successfully. Don't use this to check just before loading
    /// the texture. Instead you should just try to load the texture.
    ///
    /// This is meant for load-time checks so that you can display a warning to
    /// the user if their install is incorrect.
    /// </remarks>
    public static bool TextureExists(string path) => TextureExists(path, new());

    /// <summary>
    /// Get a list of asset bundles that would be checked when loading a texture
    /// with the requested path.
    /// </summary>
    /// <param name="path">A path within GameData.</param>
    /// <returns></returns>
    public static string[] GetAssetBundlesForPath(string path)
    {
        var key = CanonicalizeResourcePath(path);
        var bundles = GetAssetBundlesForKey(key, []);
        return [.. bundles];
    }

    /// <summary>
    /// Load a CPU-only texture from disk or from an asset bundle.
    /// </summary>
    /// <param name="path">The path to load the texture from on disk or in asset bundles.</param>
    /// <param name="options">Additional options configuring how the texture gets loaded.</param>
    ///
    /// <remarks>
    /// <para>
    /// For DDS files loaded from disk (without asset bundle overrides), the file
    /// is memory-mapped read-only and wrapped with a format-specific decoder.
    /// This avoids copying the entire file into managed memory and does not
    /// upload the texture to the GPU.
    /// </para>
    ///
    /// <para>
    /// For all other cases (PNG/JPG, asset bundle textures, or unsupported DDS
    /// formats) the texture is loaded via <see cref="LoadTexture{T}(string, TextureLoadOptions)"/> as a
    /// readable <see cref="Texture2D"/> and then wrapped with
    /// <see cref="CPUTexture2D.Create(TextureHandle{Texture2D})"/>.
    /// </para>
    /// </remarks>
    public static CPUTextureHandle LoadCPUTexture(string path, TextureLoadOptions options) =>
        Instance.LoadCPUTextureImpl(path, options);

    /// <summary>
    /// Load a CPU-only texture from disk or from an asset bundle.
    /// </summary>
    /// <param name="path">The path to load the texture from on disk or in asset bundles.</param>
    ///
    /// <remarks>
    /// <para>
    /// For DDS files loaded from disk (without asset bundle overrides), the file
    /// is memory-mapped read-only and wrapped with a format-specific decoder.
    /// This avoids copying the entire file into managed memory and does not
    /// upload the texture to the GPU.
    /// </para>
    ///
    /// <para>
    /// For all other cases (PNG/JPG, asset bundle textures, or unsupported DDS
    /// formats) the texture is loaded via <see cref="LoadTexture{T}(string, TextureLoadOptions)"/> as a
    /// readable <see cref="Texture2D"/> and then wrapped with
    /// <see cref="CPUTexture2D.Create(TextureHandle{Texture2D})"/>.
    /// </para>
    /// </remarks>
    public static CPUTextureHandle LoadCPUTexture(string path) => LoadCPUTexture(path, new());

    /// <summary>
    /// Immediately unload all asset bundles and dispose of any textures whose
    /// reference count has hit zero but have not yet been destroyed.
    /// </summary>
    ///
    /// <remarks>
    /// <para>
    /// Normally, textures whose reference count hits zero are destroyed at the
    /// end of the frame. This means they are available if they end up being
    /// needed again during the frame, and also allows unity to be more efficient
    /// in how it destroys them.
    /// </para>
    ///
    /// <para>
    /// However, if you are loading a lot of textures that are only needed for a
    /// single frame then the memory used for these textures adds up. This
    /// provides an escape hatch where so you can reduce memory usage by
    /// immediately destroying these textures. Note that it is quite slow, so
    /// avoid calling it unless you explicitly need to.
    /// </para>
    /// </remarks>
    public static void ImmediatelyDestroyUnusedTextures() =>
        Instance?.DoImmediateDestroyUnusedTextures();

    private static string CanonicalizeAssetPath(string path)
    {
        return path.Replace('\\', '/').ToLowerInvariant();
    }

    // UniverseExplorerKSP isn't really usefully able to call TryGetTarget.
    // This property allows actually inspecting the live textures _way_ more easily.
    private Dictionary<string, TextureHandleImpl> DebugTextures
    {
        get
        {
            var debug = new Dictionary<string, TextureHandleImpl>(textures.Count);
            foreach (var (key, weak) in textures)
            {
                if (weak.TryGetTarget(out var handle))
                    debug.Add(key, handle);
            }
            return debug;
        }
    }

    private Dictionary<string, TextureHandleImpl> LoadedTextures
    {
        get
        {
            var debug = new Dictionary<string, TextureHandleImpl>(textures.Count);
            foreach (var (key, weak) in textures)
            {
                if (!weak.TryGetTarget(out var handle))
                    continue;

                if (!handle.IsComplete)
                    continue;

                debug.Add(key, handle);
            }
            return debug;
        }
    }

    private Dictionary<string, TextureHandleImpl> PendingTextures
    {
        get
        {
            var debug = new Dictionary<string, TextureHandleImpl>(textures.Count);
            foreach (var (key, weak) in textures)
            {
                if (!weak.TryGetTarget(out var handle))
                    continue;

                if (handle.IsComplete)
                    continue;

                debug.Add(key, handle);
            }
            return debug;
        }
    }
}
