using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KSPTextureLoader.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Networking;

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

    // Use weak references so that textures that get leaked will at least get
    // cleaned up during a scene switch.
    internal readonly Dictionary<string, WeakReference<TextureHandleImpl>> textures = new(
        StringComparer.InvariantCultureIgnoreCase
    );

    private TextureHandle<T> LoadTextureImpl<T>(string path, TextureLoadOptions options)
        where T : Texture
    {
        TextureHandleImpl GetApplicableExistingHandle(string key)
        {
            if (!textures.TryGetValue(key, out var weakHandle))
                return null;
            if (!weakHandle.TryGetTarget(out var handle))
                return null;

            // If the caller doesn't want a readable texture then they'll be
            // happy regardless of whether the texture is readable or not.
            if (options.Unreadable)
                return handle;

            // The handle is in an error state, the texture being readable or not
            // won't make a difference here.
            if (handle.IsError)
                return handle;

            // If the texture is readable and the caller wants a readable texture
            // then everyone is happy.
            if (handle.IsReadable)
                return handle;

            // This texture is not what the caller wants, but it was loaded from
            // an asset bundle so we can't change anything by reloading it.
            if (handle.AssetBundle is not null)
                return handle;

            Debug.LogWarning($"[KSPTextureLoader] Reloading {path} to get readable texture");
            return null;
        }

        var key = CanonicalizeResourcePath(path);
        var handle = GetApplicableExistingHandle(key);
        if (handle is not null)
            return new TextureHandle<T>(handle).Acquire();

        handle = new TextureHandleImpl(path, options.Unreadable);
        textures[key] = new WeakReference<TextureHandleImpl>(handle);

        var assetBundles = GetAssetBundlesForKey(key, options.AssetBundles);
        var coroutine = DoLoadTexture<T>(handle, options, assetBundles);
        handle.coroutine = coroutine;
        StartCoroutine(coroutine);

        return new(handle);
    }

    private IEnumerator DoLoadTexture<T>(
        TextureHandleImpl handle,
        TextureLoadOptions options,
        List<string> assetBundles
    )
        where T : Texture
    {
        // Ensure that the texture handle doesn't get disposed of while we are still working on it.
        using var guard = handle.Acquire();
        var marker = new ProfilerMarker($"LoadTexture: {handle.Path}");
        using var coroutine = ExceptionUtils.CatchExceptions(
            handle,
            DoLoadTextureInner<T>(handle, options, assetBundles)
        );

        while (true)
        {
            using (var scope = marker.Auto())
            {
                if (!coroutine.MoveNext())
                    break;
            }

            yield return coroutine.Current;
        }
    }

    private IEnumerator DoLoadTextureInner<T>(
        TextureHandleImpl handle,
        TextureLoadOptions options,
        List<string> assetBundles
    )
        where T : Texture
    {
        List<Exception> assetBundleExceptions = null;
        if (assetBundles.Count != 0)
        {
            var bundles = new AssetBundleHandle[assetBundles.Count];
            using var bundlesGuard = new ArrayDisposeGuard<AssetBundleHandle>(bundles);

            for (int i = 0; i < assetBundles.Count; ++i)
                bundles[i] = LoadAssetBundle(
                    assetBundles[i],
                    sync: options.Hint < TextureLoadHint.BatchAsynchronous
                );

            var assetPath = CanonicalizeAssetPath(handle.Path);
            for (int i = 0; i < assetBundles.Count; ++i)
            {
                var abHandle = bundles[i];
                if (!abHandle.IsComplete && options.Hint < TextureLoadHint.BatchSynchronous)
                    yield return abHandle;

                AssetBundle bundle;
                try
                {
                    bundle = abHandle.GetBundle();
                }
                catch (Exception ex)
                {
                    assetBundleExceptions ??= [];
                    assetBundleExceptions.Add(ex);
                    continue;
                }

                if (!bundle.Contains(assetPath))
                    continue;

                Texture asset;
                if (options.Hint < TextureLoadHint.Synchronous)
                {
                    var assetreq = bundle.LoadAssetAsync<Texture>(assetPath);
                    if (!assetreq.isDone)
                        yield return assetreq;

                    asset = (Texture)assetreq.asset;
                }
                else
                {
                    // If there is only one texture loaded and it is going to be
                    // blocked on immediately then we might as well just load it
                    // synchronously.
                    //
                    // This is likely to be painfully expensive if there are any
                    // async loads happening in the background, since unity will
                    // wait for all pending loads to complete first.
                    asset = bundle.LoadAsset<Texture>(assetPath);
                }

                if (asset is null)
                {
                    Debug.LogWarning(
                        $"[KSPTextureLoader] Asset {handle.Path} exists in asset bundle {options.AssetBundles[i]} but was not a texture."
                    );
                    continue;
                }

                handle.SetTexture<T>(asset, options);
                yield break;
            }
        }

        var diskPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", handle.Path);
        if (!File.Exists(diskPath))
        {
            if (assetBundleExceptions is null)
                throw new FileNotFoundException(
                    "Texture not present on disk or in configured asset bundles"
                );

            throw new AggregateException(
                "Texture not present on disk or in configured asset bundles. Some asset bundles failed to load.",
                assetBundleExceptions
            );
        }

        var extension = Path.GetExtension(handle.Path);
        if (
            extension == ".png"
            // truecolor files appear to just be png files with a different name
            || extension == ".truecolor"
            || extension == ".jpg"
            || extension == ".jpeg"
        )
        {
            foreach (var item in LoadPNGOrJPEG<T>(handle, options))
                yield return item;
        }
        else if (extension == ".dds")
        {
            foreach (var item in LoadDDSTexture<T>(handle, options))
                yield return item;
        }
        else
        {
            throw new Exception($"{extension} files are not supported");
        }
    }

    IEnumerable<object> LoadPNGOrJPEG<T>(TextureHandleImpl handle, TextureLoadOptions options)
        where T : Texture
    {
        Texture2D texture;
        var diskPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", handle.Path);
        // Cubemap textures need to be converted, so they must be readable.
        var unreadable = typeof(T) != typeof(Cubemap) && options.Unreadable;

        if (options.Hint < TextureLoadHint.BatchSynchronous)
        {
            var url = new Uri(diskPath);
            using var request = UnityWebRequestTexture.GetTexture(url, unreadable);

            yield return request.SendWebRequest();

            // We cannot block on the completion of a web request. That just results
            // in an infinite hang. If the web request isn't complete then we fall
            // back to a synchronous read off disk.
            if (request.isDone)
            {
                if (request.isNetworkError || request.isHttpError)
                    throw new Exception($"Failed to load image: {request.error}");

                texture = DownloadHandlerTexture.GetContent(request);
                handle.SetTexture<T>(texture, options);
                yield break;
            }
        }

        var info = new FileInfo(diskPath);
        if (!info.Exists)
            throw new FileNotFoundException("file not found");

        var length = info.Length;
        if (length > int.MaxValue)
            throw new Exception(
                "image was too large to be loaded. Only images up to 2GB in size are supported."
            );

        var array = new byte[(int)length];
        ulong gchandle;

        ReadHandle readHandle;
        unsafe
        {
            var ptr = UnsafeUtility.PinGCArrayAndGetDataAddress(array, out gchandle);
            var command = new ReadCommand
            {
                Buffer = ptr,
                Offset = 0,
                Size = length,
            };
            readHandle = LaunchRead(diskPath, command);
        }

        using var gcHandleGuard = new GcHandleGuard(gchandle);
        using var readGuard = new SafeReadHandleGuard(readHandle);

        using (handle.WithCompleteHandler(new JobHandleCompleteHandler(readGuard.JobHandle)))
            yield return new WaitUntil(() => readGuard.JobHandle.IsCompleted);

        if (readHandle.Status != ReadStatus.Complete)
            throw new Exception("an error occurred while reading from the file");

        texture = new Texture2D(1, 1);
        texture.LoadImage(array, unreadable);
        handle.SetTexture<T>(texture, options);
    }

    private bool DoTextureExists(string path, TextureLoadOptions options)
    {
        var key = CanonicalizeResourcePath(path);
        if (textures.TryGetValue(key, out var weakHandle))
        {
            if (weakHandle.TryGetTarget(out _))
                return true;
        }

        var diskPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", path);
        if (File.Exists(diskPath))
            return true;

        var assetBundles = GetAssetBundlesForKey(key, options.AssetBundles);
        var assetPath = CanonicalizeAssetPath(path);
        foreach (var assetBundlePath in assetBundles)
        {
            using var handle = LoadAssetBundle(assetBundlePath, sync: true);

            try
            {
                var bundle = handle.GetBundle();
                if (bundle.Contains(assetPath))
                    return true;
            }
            catch
            {
                // The asset bundle loader will print out an error message, we
                // don't really need to do anything here.
                continue;
            }
        }

        return false;
    }

    internal static T ConvertTexture<T>(Texture src, TextureLoadOptions options)
        where T : Texture
    {
        if (src is T tex)
            return tex;

        if (options.AllowImplicitConversions)
        {
            if (src is Texture2D tex2d)
            {
                if (typeof(T) == typeof(Cubemap))
                    return (T)(Texture)TextureUtils.ConvertTexture2dToCubemap(tex2d);

                if (typeof(T) == typeof(Texture2DArray))
                    return (T)(Texture)TextureUtils.ConvertTexture2DToArray(tex2d);
            }

            if (src is Cubemap cubemap)
            {
                if (typeof(T) == typeof(CubemapArray))
                    return (T)(Texture)TextureUtils.ConvertCubemapToArray(cubemap);
            }
        }

        throw new NotSupportedException(
            $"Cannot convert a texture of type {src.GetType().Name} to a texture of type {typeof(T).Name}"
        );
    }

    private static unsafe ReadHandle LaunchRead(string path, ReadCommand command) =>
        AsyncReadManager.Read(path, &command, 1);

    private static List<string> GetAssetBundlesForKey(string key, string[] assetBundles)
    {
        var bundles = new List<string>(assetBundles?.Length ?? 0);
        if (assetBundles is not null)
        {
            foreach (var bundle in assetBundles)
            {
                if (bundle is not null)
                    bundles.Add(bundle);
            }
        }

        Config.Instance.GetImplicitBundlesForCanonicalPath(key, bundles);
        return bundles;
    }

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
