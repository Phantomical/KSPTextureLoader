using System;
using System.Collections;
using System.Runtime.ExceptionServices;
using Unity.Profiling;
using UnityEngine;

namespace KSPTextureLoader;

/// <summary>
/// A reference counted handle to a loaded asset bundle.
/// </summary>
public class AssetBundleHandle
    : CustomYieldInstruction,
        IDisposable,
        ISetException,
        ICompleteHandler
{
    private static readonly ProfilerMarker CompleteMarker = new("AssetBundleHandle.Complete");

    public int RefCount { get; private set; } = 1;
    private AssetBundle bundle;
    private ExceptionDispatchInfo exception;
    internal ICompleteHandler completeHandler;
    internal IEnumerator coroutine;

    /// <summary>
    /// The path that this asset bundle was loaded from within GameData.
    /// </summary>
    public string Path { get; private set; }
    public bool IsComplete => coroutine is null;
    public bool IsError => exception is not null;

    public override bool keepWaiting => !IsComplete;

    internal AssetBundleHandle(string path) => Path = path;

    internal AssetBundleHandle(string path, AssetBundle bundle)
    {
        if (bundle is null)
            throw new ArgumentNullException(nameof(bundle));

        this.Path = path;
        this.bundle = bundle;
    }

    /// <summary>
    /// Get the asset bundle for this handle. If the asset bundle is still being
    /// loaded then this will block until loading completes.
    /// </summary>
    /// <returns></returns>
    public AssetBundle GetBundle()
    {
        if (!IsComplete)
            WaitUntilComplete();

        exception?.Throw();
        return bundle;
    }

    /// <summary>
    /// Increase the reference count and get a new handle to the same asset
    /// bundle.
    /// </summary>
    /// <returns></returns>
    public AssetBundleHandle Acquire()
    {
        RefCount += 1;
        return this;
    }

    /// <summary>
    /// Decrease the reference count.
    /// </summary>
    public void Dispose()
    {
        RefCount -= 1;
        if (RefCount < 0)
        {
            Debug.LogError(
                $"AssetBundleHandle for asset bundle at {Path} has been disposed of too many times!"
            );
        }
    }

    public void WaitUntilComplete()
    {
        if (coroutine is null)
            return;

        using var scope = CompleteMarker.Auto();

        while (true)
        {
            completeHandler?.WaitUntilComplete();

            // When blocking on an asset bundle request unity will take the time
            // to run other coroutines, which could set this to null.
            // This means that we need to do the null check after.
            if (coroutine is null)
                break;
            if (!coroutine.MoveNext())
                break;
        }
    }

    internal void SetBundle(AssetBundle bundle)
    {
        this.bundle = bundle;
        this.coroutine = null;
        this.completeHandler = null;
    }

    void ISetException.SetException(ExceptionDispatchInfo ex)
    {
        Debug.LogError($"[KSPTextureLoader] Failed to load asset bundle {Path}");
        Debug.LogException(ex.SourceException);

        this.exception = ex;
        this.coroutine = null;
        this.completeHandler = null;
    }
}
