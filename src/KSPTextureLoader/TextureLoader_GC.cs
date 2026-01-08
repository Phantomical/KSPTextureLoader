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
    private static readonly WaitForEndOfFrame WaitForEndOfFrame = new();
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
    private readonly HashSet<TextureHandleImpl> destroyed = [];
    private Coroutine gcCoroutine = null;

    internal void QueueForDestroy(TextureHandleImpl handle)
    {
        destroyQueue.Add(handle);
        gcCoroutine ??= StartCoroutine(GcCoroutine());
    }

    IEnumerator GcCoroutine()
    {
        using var guard = new ClearCoroutineGuard(this);
        yield return WaitForEndOfFrame;

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
        if (destroyQueue.Count == 0)
            return;

        using var scope = DestroyTexturesMarker.Auto();

        try
        {
            while (destroyQueue.TryPop(out var handle))
            {
                // A handle can be resurrected in the same frame as its reference
                // count went to zero.
                if (handle.RefCount > 0)
                    continue;

                if (!destroyed.Add(handle))
                    continue;

                handle.Destroy(immediate);
            }
        }
        finally
        {
            destroyed.Clear();
        }
    }

    readonly struct ClearCoroutineGuard(TextureLoader loader) : IDisposable
    {
        public void Dispose() => loader.gcCoroutine = null;
    }
}
