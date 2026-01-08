using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using KSPTextureLoader.Utils;
using Unity.Profiling;
using UnityEngine;

namespace KSPTextureLoader;

partial class TextureLoader
{
    private static readonly ProfilerMarker DestroyUnusedTexturesMarker = new(
        "TextureLoader.ImmediatelyDestroyUnusedTextures"
    );
    private static readonly ProfilerMarker UnloadUnusedAssetBundlesMarker = new(
        "TextureLoader.UnloadUnusedAssetBundles"
    );
    private static readonly ProfilerMarker DestroyTexturesMarker = new(
        "TextureLoader.DestroyTextures"
    );

    private readonly List<TextureHandleImpl> destroyQueue = [];
    private Coroutine gcCoroutine = null;

    internal void QueueForDestroy(TextureHandleImpl handle)
    {
        destroyQueue.Add(handle);
        gcCoroutine ??= StartCoroutine(GcCoroutine());
    }

    IEnumerator GcCoroutine()
    {
        using var guard = new ClearCoroutineGuard(this);
        yield return null;

        DestroyTextures();
    }

    void DoImmediateDestroyUnusedTextures()
    {
        using var scope = DestroyUnusedTexturesMarker.Auto();

        UnloadAllAssetBundles();
        DestroyTextures(true);
    }

    void UnloadAllAssetBundles()
    {
        using var scope = UnloadUnusedAssetBundlesMarker.Auto();
        List<Exception> exceptions = null;

        foreach (var (key, bundle) in assetBundles)
        {
            if (bundle.RefCount != 0)
                continue;

            try
            {
                assetBundles.Remove(key);
                bundle.Destroy();
            }
            catch (Exception ex)
            {
                exceptions ??= [];
                exceptions.Add(ex);
            }
        }

        if (exceptions is not null)
        {
            if (exceptions.Count == 1)
                throw exceptions[0];
        }
    }

    void DestroyTextures(bool immediate = false)
    {
        const uint MAX_PER_FRAME = 128 * 1024 * 1024;

        if (destroyQueue.Count == 0)
            return;

        using var scope = DestroyTexturesMarker.Auto();

        uint unloadedBytes = 0;
        while (destroyQueue.TryPop(out var handle))
        {
            // A handle can be resurrected in the same frame as its reference
            // count went to zero.
            if (handle.RefCount > 0)
                continue;

            unloadedBytes += handle.Destroy(immediate);

            // Freeing large allocations is actually quite slow, limit ourselves to a certain
            // amount of memory per frame in order to spread the slowness across multiple frames.
            if (!immediate && unloadedBytes >= MAX_PER_FRAME)
                break;
        }
    }

    readonly struct ClearCoroutineGuard(TextureLoader loader) : IDisposable
    {
        public void Dispose() => loader.gcCoroutine = null;
    }
}
