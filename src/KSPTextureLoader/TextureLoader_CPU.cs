using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using KSPTextureLoader.Async;
using KSPTextureLoader.Format;
using KSPTextureLoader.Utils;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;

namespace KSPTextureLoader;

public partial class TextureLoader
{
    internal static readonly Dictionary<string, WeakReference<CPUTextureHandle>> cpuTextures = new(
        StringComparer.OrdinalIgnoreCase
    );

    private CPUTextureHandle LoadCPUTextureImpl(string path, TextureLoadOptions options)
    {
        if (path is null)
            throw new ArgumentNullException(nameof(path), "path argument was null");

        var key = CanonicalizeResourcePath(path);
        if (
            cpuTextures.TryGetValue(key, out var weak)
            && weak.TryGetTarget(out var existing)
            && existing.RefCount > 0
        )
            return existing.Acquire();

        var handle = new CPUTextureHandle(path);
        cpuTextures[key] = new WeakReference<CPUTextureHandle>(handle);

        var assetBundles = GetAssetBundlesForKey(key, options.AssetBundles);
        var coroutine = DoLoadCPUTexture(handle, options, assetBundles);
        handle.coroutine = coroutine;
        StartCoroutine(coroutine);

        return handle;
    }

    private IEnumerator DoLoadCPUTexture(
        CPUTextureHandle handle,
        TextureLoadOptions options,
        List<string> assetBundles
    )
    {
        using var guard = handle.Acquire();
        var marker = new ProfilerMarker($"LoadCPUTexture: {handle.Path}");
        using var coroutine = ExceptionUtils.CatchExceptions(
            handle,
            DoLoadCPUTextureInner(handle, options, assetBundles)
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

    private IEnumerator DoLoadCPUTextureInner(
        CPUTextureHandle handle,
        TextureLoadOptions options,
        List<string> assetBundles
    )
    {
        if (Config.Instance.DebugMode >= DebugLevel.Debug)
            Debug.Log($"[KSPTextureLoader] Loading CPU texture {handle.Path}");

        options.Unreadable = false;

        List<Exception> assetBundleExceptions = null;
        if (assetBundles.Count != 0)
        {
            var bundles = new AssetBundleHandle[assetBundles.Count];
            using var bundlesGuard = new ArrayDisposeGuard<AssetBundleHandle>(bundles);

            for (int i = 0; i < assetBundles.Count; ++i)
                bundles[i] = LoadAssetBundle(
                    assetBundles[i],
                    sync: ShouldBeSync(options, TextureLoadHint.BatchAsynchronous)
                );

            var assetPath = CanonicalizeAssetPath(handle.Path);
            for (int i = 0; i < assetBundles.Count; ++i)
            {
                var abHandle = bundles[i];
                if (!abHandle.IsComplete)
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

                if (bundle.Contains(assetPath))
                {
                    using var texhandle = LoadTexture<Texture2D>(handle.Path, options);
                    if (!texhandle.IsComplete)
                        yield return texhandle;

                    handle.SetTexture(
                        CPUTexture2D.Create(texhandle.Acquire()),
                        texhandle.AssetBundle
                    );
                    yield break;
                }
            }
        }

        var extension = Path.GetExtension(handle.Path);
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

        if (extension == ".dds")
        {
            var task = AsyncUtil.LaunchMainThreadTask(() =>
                DDSLoader.LoadCPUTexture2D(diskPath, options)
            );

            using (handle.WithCompleteHandler(new TaskCompleteHandler(task)))
                yield return new WaitUntil(() => task.IsCompleted);

            try
            {
                handle.SetTexture(task.Result);
                yield break;
            }
            catch (NotSupportedException e)
            {
                // If the texture format is not supported then we fall back to
                // a regular texture load.

                if (Config.Instance.DebugMode == DebugLevel.Trace)
                {
                    Debug.Log(
                        $"[KSPTextureLoader] Could not memory map CPU texture {handle.Path}: {e}"
                    );
                }
            }
        }

        using var texHandle = LoadTexture<Texture2D>(handle.Path, options);
        if (!texHandle.IsComplete)
        {
            using (handle.WithCompleteHandler(texHandle.handle))
                yield return texHandle;
        }

        handle.SetTexture(CPUTexture2D.Create(texHandle.Acquire()), texHandle.AssetBundle);
    }
}
