using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.Profiling;
using UnityEngine;

namespace KSPTextureLoader;

public partial class TextureLoader
{
    internal readonly Dictionary<string, AssetBundleHandle> assetBundles = new(
        StringComparer.InvariantCultureIgnoreCase
    );

    AssetBundleHandle LoadAssetBundleImpl(string path, bool sync)
    {
        var key = CanonicalizeResourcePath(path);
        if (assetBundles.TryGetValue(key, out var handle))
            return handle.Acquire();

        handle = new AssetBundleHandle(path);
        assetBundles.Add(key, handle);

        var coroutine = DoLoadAssetBundle(handle, sync);
        handle.coroutine = coroutine;

        StartCoroutine(AssetBundleCleanup(handle));
        StartCoroutine(coroutine);
        return handle;
    }

    IEnumerator DoLoadAssetBundle(AssetBundleHandle handle, bool sync)
    {
        // Ensure that the asset bundle handle stays alive while we are loading it.
        using var guard = handle.Acquire();
        var marker = new ProfilerMarker($"LoadAssetBundle: {handle.Path}");
        using var coroutine = ExceptionUtils.CatchExceptions(
            handle,
            DoLoadAssetBundleInner(handle, sync)
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

    IEnumerator DoLoadAssetBundleInner(AssetBundleHandle handle, bool sync)
    {
        var path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", handle.Path);
        var request = AssetBundle.LoadFromFileAsync(path);

        if (!sync)
        {
            handle.completeHandler = new AssetBundleCompleteHandler(request);
            yield return request;
            handle.completeHandler = null;
        }

        var bundle = request.assetBundle;
        if (bundle == null)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("No asset bundle found at path");

            throw new Exception("Asset bundle failed to load");
        }

        handle.SetBundle(bundle);
    }

    IEnumerator AssetBundleCleanup(AssetBundleHandle handle)
    {
        yield return handle;

        int delayCount = 0;
        while (true)
        {
            if (handle.RefCount > 0)
            {
                delayCount = 0;
                yield return null;
                continue;
            }

            delayCount += 1;

            if (delayCount >= Config.Instance.AssetBundleUnloadDelay)
                break;
        }

        assetBundles.Remove(handle.Path);

        if (handle.IsError)
            yield break;
        var bundle = handle.GetBundle();
        bundle.Unload(false);
    }

    internal static string CanonicalizeResourcePath(string path)
    {
        return path.Replace('\\', '/');
    }
}
