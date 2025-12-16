using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using KSPTextureLoader.Format;
using KSPTextureLoader.Utils;
using Unity.IO.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace KSPTextureLoader;

public partial class TextureLoader
{
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
                        $"[KSPTextureLoader] Asset {handle.Path} exists in asset bundle {assetBundles[i]} but could not be read as a texture."
                    );
                    continue;
                }

                abHandle.AddLoadedTexture(handle.Acquire());
                handle.SetTexture<T>(asset, options, abHandle.Path);
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
            foreach (var item in PNGLoader.LoadPNGOrJPEG<T>(handle, options))
                yield return item;
        }
        else if (extension == ".dds")
        {
            foreach (var item in DDSLoader.LoadDDSTexture<T>(handle, options))
                yield return item;
        }
        else
        {
            throw new Exception($"{extension} files are not supported");
        }
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

    internal static bool Texture2DShouldBeReadable<T>(TextureLoadOptions options)
    {
        if (!options.Unreadable)
            return true;

        if (SystemInfo.copyTextureSupport.HasFlag(CopyTextureSupport.DifferentTypes))
            return false;

        if (typeof(T) == typeof(Cubemap))
            return true;

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
        }

        throw new NotSupportedException(
            $"Cannot convert a texture of type {src.GetType().Name} to a texture of type {typeof(T).Name}"
        );
    }

    private static unsafe ReadHandle LaunchRead(string path, ReadCommand command) =>
        AsyncReadManager.Read(path, &command, 1);
}
