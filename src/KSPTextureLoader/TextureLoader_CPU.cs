using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using KSPTextureLoader.Format;
using KSPTextureLoader.Utils;
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

    readonly Dictionary<string, ProfilerMarker> LoadCPUTextureMarkerCache = [];

    private IEnumerator DoLoadCPUTexture(
        CPUTextureHandle handle,
        TextureLoadOptions options,
        MaybeList<string> assetBundles
    )
    {
        using var guard = handle.Acquire();

        if (!LoadCPUTextureMarkerCache.TryGetValue(handle.Path, out var marker))
        {
            marker = new ProfilerMarker($"LoadCPUTexture: {handle.Path}");
            LoadCPUTextureMarkerCache.Add(handle.Path, marker);
        }

        IEnumerator<object> coroutine;
        using (CompletionContext.Enter(handle))
            coroutine = ExceptionUtils.CatchExceptions(
                handle,
                DoLoadCPUTextureInner(handle, options, assetBundles)
            );

        using var _guard = coroutine;

        while (true)
        {
            using (var scope = marker.Auto())
            using (CompletionContext.Enter(handle))
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
        MaybeList<string> assetBundles
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

                if (!bundle.Contains(assetPath))
                    continue;

                // Run off the loader thread: the index build and pixel read go
                // through VfsReader, which expects to be called on a background thread.
                var task = Task.Run(() =>
                    LoadFromAssetBundleAsync(abHandle, handle.Path, assetPath)
                );
                using (handle.WithCompleteHandler(new TaskCompleteHandler(task)))
                    yield return new WaitUntil(() => task.IsCompleted);

                var texture = task.GetResultUnwrapped();
                if (texture is not null)
                {
                    handle.SetTexture(texture, abHandle.Path);
                    yield break;
                }

                using var texhandle = LoadTexture<Texture2D>(handle.Path, options);
                if (!texhandle.IsComplete)
                    yield return texhandle;

                handle.SetTexture(CPUTexture2D.Create(texhandle.Acquire()), texhandle.AssetBundle);
                yield break;
            }
        }

        var extension = Path.GetExtension(handle.Path);
        var diskPath = Path.Combine(PathUtil.GameDataDir, handle.Path);

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
                handle.SetTexture(task.GetResultUnwrapped());
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

    /// <summary>
    /// Load the data for a CPU texture by reading its data directly from the asset bundle.
    /// </summary>
    /// <param name="handle"></param>
    /// <param name="path"></param>
    /// <param name="assetPath"></param>
    /// <returns></returns>
    static async Task<CPUTexture2D> LoadFromAssetBundleAsync(
        AssetBundleHandle handle,
        string path,
        string assetPath
    )
    {
        try
        {
            var index = await handle.GetIndexAsync();
            if (!index.Contains(assetPath))
                return null;

            return await index.LoadTextureAsync(assetPath);
        }
        catch (Exception e)
        {
            if (Config.Instance.DebugMode > DebugLevel.Debug)
                return null;

            await AsyncUtil.LaunchMainThreadTask(() =>
            {
                Debug.Log(
                    $"[KSPTextureLoader] Failed to directly load CPU texture {path} from asset bundle {handle.Path}: {e}"
                );
            });
            return null;
        }
    }
}
