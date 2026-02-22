using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using KSPTextureLoader.Utils;
using Unity.Profiling;
using UnityEngine;

namespace KSPTextureLoader;

public class CPUTextureHandle : CustomYieldInstruction, IDisposable, ISetException, ICompleteHandler
{
    private static readonly ProfilerMarker CompleteMarker = new("CPUTextureHandle.Complete");

    public int RefCount { get; private set; } = 1;
    public string Path { get; private set; }
    public string AssetBundle { get; private set; }

    private CPUTexture2D texture;
    private ExceptionDispatchInfo exception;
    internal ICompleteHandler completeHandler;
    internal IEnumerator coroutine;

    public bool IsComplete => coroutine is null;
    public bool IsError => exception is not null;

    public event Action<CPUTextureHandle> OnCompleted;
    public event Action<CPUTextureHandle, Exception> OnError;

    public override bool keepWaiting => !IsComplete;

    internal CPUTextureHandle(string path)
    {
        Path = path;
    }

    internal CPUTextureHandle(string path, ExceptionDispatchInfo ex)
        : this(path)
    {
        exception = ex;
    }

    /// <summary>
    /// Create a <see cref="CPUTextureHandle"/> from an existing texture.
    /// </summary>
    /// <param name="path">A label to give this handle, this is only for debugging purposes.</param>
    /// <param name="texture">The <see cref="CPUTexture2D"/> to use.</param>
    ///
    /// <remarks>
    /// Be aware that this method takes ownership of <paramref name="texture"/>. Do not use it with
    /// a <see cref="CPUTexture2D"/> that is already owned by another <see cref="CPUTextureHandle"/>.
    /// </remarks>
    public CPUTextureHandle(string path, CPUTexture2D texture)
        : this(path)
    {
        this.texture = texture;
    }

    /// <summary>
    /// Get the CPU texture for this texture handle. Will block if the texture has
    /// not loaded yet and will throw an exception if the texture failed to load.
    /// </summary>
    ///
    /// <remarks>
    /// You will need to keep this texture handle around or else the texture will
    /// be leaked when the last handle is disposed of.
    /// </remarks>
    public CPUTexture2D GetTexture()
    {
        if (!IsComplete)
            WaitUntilComplete();

        exception?.Throw();
        return texture;
    }

    /// <summary>
    /// Block until this texture has been loaded.
    /// </summary>
    public void WaitUntilComplete()
    {
        if (coroutine is null)
            return;

        if (TextureLoader.LastSceneSwitchFrame == Time.frameCount)
            throw new InvalidOperationException(
                "Blocking on a texture handle while a scene is pending is not permitted."
            );

        using var scope = CompleteMarker.Auto();

        while (!Step()) { }
    }

    /// <summary>
    /// Execute one step of the internal coroutine, if the current blocker has
    /// completed.
    /// </summary>
    /// <returns>true if complete</returns>
    internal bool Tick()
    {
        if (completeHandler is not null)
        {
            if (!completeHandler.IsComplete)
                return false;
        }

        if (coroutine is null)
            return true;

        return !coroutine.MoveNext();
    }

    /// <summary>
    /// Execute one step of the internal coroutine, blocking if necessary.
    /// </summary>
    /// <returns>true if complete</returns>
    internal bool Step()
    {
        completeHandler?.WaitUntilComplete();

        if (coroutine is null)
            return true;
        return !coroutine.MoveNext();
    }

    internal CPUCompleteHandlerGuard WithCompleteHandler(ICompleteHandler handler)
    {
        completeHandler = handler;
        return new CPUCompleteHandlerGuard(this);
    }

    /// <summary>
    /// Get a new handle for the same texture and increase the reference count.
    /// </summary>
    public CPUTextureHandle Acquire()
    {
        RefCount += 1;
        return this;
    }

    /// <summary>
    /// Decrease the reference count of this texture handle. If the reference
    /// count is decreased to zero then the texture will be released.
    /// </summary>
    public void Dispose()
    {
        RefCount -= 1;
        if (RefCount < 0)
        {
            Debug.LogError(
                $"CPUTextureHandle for texture at {Path} has been disposed of too many times!"
            );
        }

        if (RefCount != 0)
            return;

        texture = null;
    }

    internal void SetTexture(CPUTexture2D tex, string assetBundle = null)
    {
        texture = tex;
        AssetBundle = assetBundle;
        coroutine = null;
        completeHandler = null;

        try
        {
            OnCompleted?.Invoke(this);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[KSPTextureLoader] OnCompleted event threw an exception");
            Debug.LogException(ex);
        }
    }

    void ISetException.SetException(ExceptionDispatchInfo ex)
    {
        exception = ex;
        coroutine = null;
        completeHandler = null;

        try
        {
            OnError?.Invoke(this, ex.SourceException);
        }
        catch (Exception e)
        {
            Debug.LogError($"[KSPTextureLoader] OnError event threw an exception");
            Debug.LogException(e);
        }
    }

    public override string ToString()
    {
        return $"{Path} (RefCount: {RefCount})";
    }

    /// <summary>
    /// Efficiently wait for multiple CPU texture handles to complete.
    /// </summary>
    /// <param name="handles"></param>
    /// <returns>An enumerator that yields handles in order in which they completed.</returns>
    ///
    /// <remarks>
    /// <para>
    /// When synchronously waiting for multiple textures to complete it is easy
    /// for progress to get serialized. You wait for one texture after another
    /// to complete and they only get the chance to kick off new work once you
    /// block on them.
    /// </para>
    ///
    /// <para>
    /// This method takes care of ensuring that all handles within can make progress
    /// while you are waiting for them to complete.
    /// </para>
    /// </remarks>
    public static IEnumerable<KeyValuePair<int, CPUTextureHandle>> WaitBatch(
        IEnumerable<CPUTextureHandle> handles
    )
    {
        var pairs = handles.Select((handle, index) => KeyValuePair.Create(index, handle)).ToArray();
        int count = pairs.Length;

        while (true)
        {
            int i = 0;
            int j = 0;
            for (; i < count; ++i)
            {
                var pair = pairs[i];
                if (pair.Value.Tick())
                {
                    yield return pair;
                    continue;
                }

                pairs[j++] = pair;
            }

            if (i != j)
            {
                count = j;
                continue;
            }

            if (count == 0)
                break;

            // Only block on the very first handle, afterwards we will go back
            // and tick every handle again.
            pairs[0].Value.Step();
        }

        yield break;
    }
}
