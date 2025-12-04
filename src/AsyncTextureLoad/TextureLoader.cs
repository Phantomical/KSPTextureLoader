using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using JetBrains.Annotations;
using Steamworks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Networking;

namespace AsyncTextureLoad;

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
    ///     packed horizontally in a 3x2 configuration.
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

    // Use weak references so that textures that get leaked will at least get
    // cleaned up during a scene switch.
    internal readonly Dictionary<string, WeakReference<TextureHandle>> textures = new(
        StringComparer.InvariantCultureIgnoreCase
    );

    private TextureHandle<T> LoadTextureImpl<T>(string path, TextureLoadOptions options)
        where T : Texture
    {
        TextureHandle handle;
        var key = CanonicalizeResourcePath(path);
        if (textures.TryGetValue(key, out var weakHandle))
        {
            if (weakHandle.TryGetTarget(out handle))
                return new TextureHandle<T>(handle);
        }

        handle = new TextureHandle(path);
        textures[key] = new WeakReference<TextureHandle>(handle);

        var coroutine = DoLoadTexture<T>(handle, options);
        handle.coroutine = coroutine;
        StartCoroutine(coroutine);

        return new(handle);
    }

    private IEnumerator DoLoadTexture<T>(TextureHandle handle, TextureLoadOptions options)
        where T : Texture
    {
        // Ensure that the texture handle doesn't get disposed of while we are still working on it.
        using var guard = handle.Acquire();
        var marker = new ProfilerMarker($"LoadTexture: {handle.Path}");
        using var coroutine = ExceptionUtils.CatchExceptions(
            handle,
            DoLoadTextureInner<T>(handle, options)
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

    private IEnumerator DoLoadTextureInner<T>(TextureHandle handle, TextureLoadOptions options)
        where T : Texture
    {
        if (options.AssetBundles is not null)
        {
            var bundles = new AssetBundleHandle[options.AssetBundles.Length];
            using var bundlesGuard = new ArrayDisposeGuard<AssetBundleHandle>(bundles);

            for (int i = 0; i < bundles.Length; ++i)
                bundles[i] = LoadAssetBundle(
                    options.AssetBundles[i],
                    sync: options.Hint < TextureLoadHint.BatchAsynchronous
                );

            var assetPath = CanonicalizeAssetPath(handle.Path);
            for (int i = 0; i < bundles.Length; ++i)
            {
                var abHandle = bundles[i];
                if (!abHandle.IsComplete && options.Hint < TextureLoadHint.BatchSynchronous)
                    yield return abHandle;
                var bundle = abHandle.GetBundle();

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
                        $"[AsyncTextureLoad] Asset {handle.Path} exists in asset bundle {options.AssetBundles[i]} but was not a texture."
                    );
                    continue;
                }

                handle.SetTexture<T>(asset, options);
                yield break;
            }
        }

        var extension = Path.GetExtension(handle.Path);
        if (extension == ".png" || extension == ".jpg" || extension == ".jpeg")
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

    IEnumerable<object> LoadPNGOrJPEG<T>(TextureHandle handle, TextureLoadOptions options)
        where T : Texture
    {
        var diskPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", handle.Path);

        // Cubemap textures need to be converted, so they must be readable.
        var unreadable = typeof(T) != typeof(Cubemap) && options.Unreadable;
        var url = new Uri(diskPath);
        using var request = UnityWebRequestTexture.GetTexture(url, unreadable);

        handle.completeHandler = new UnityWebRequestCompleteHandler(request);
        yield return request.SendWebRequest();

        if (request.isNetworkError || request.isHttpError)
            throw new Exception($"Failed to load image: {request.error}");

        var texture = DownloadHandlerTexture.GetContent(request);
        handle.SetTexture<T>(texture, options);
    }

    internal static T ConvertTexture<T>(Texture src, TextureLoadOptions options)
        where T : Texture
    {
        if (src is T tex)
            return tex;

        if (src is Texture2D tex2d)
        {
            if (typeof(T) == typeof(Cubemap))
                return (T)
                    (Texture)TextureUtils.ConvertTexture2dToCubemap(tex2d, options.Unreadable);

            if (typeof(T) == typeof(Texture2DArray))
                return (T)(Texture)TextureUtils.ConvertTexture2DToArray(tex2d);
        }

        if (src is Cubemap cubemap)
        {
            if (typeof(T) == typeof(CubemapArray))
                return (T)(Texture)TextureUtils.ConvertCubemapToArray(cubemap);
        }

        throw new NotSupportedException(
            $"Cannot convert a texture of type {src.GetType().Name} to a texture of type {typeof(T).Name}"
        );
    }

    private static unsafe ReadHandle LaunchRead(string path, ReadCommand command) =>
        AsyncReadManager.Read(path, &command, 1);

    private static string CanonicalizeAssetPath(string path)
    {
        return path.Replace('\\', '/').ToLowerInvariant();
    }

    private struct ArrayDisposeGuard<T>(T[] array) : IDisposable
        where T : IDisposable
    {
        public readonly void Dispose()
        {
            foreach (var item in array)
                item?.Dispose();
        }
    }

    private class SafeReadHandleGuard(ReadHandle handle) : IDisposable
    {
        public JobHandle JobHandle = handle.JobHandle;

        public void Dispose()
        {
            if (!handle.IsValid())
                return;
            if (!handle.JobHandle.IsCompleted)
                handle.JobHandle.Complete();
            handle.Dispose();
        }
    }
}
