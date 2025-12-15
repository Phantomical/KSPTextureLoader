using System;
using System.Collections.Generic;
using System.Linq;
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
}
