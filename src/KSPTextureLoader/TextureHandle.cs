using System;
using System.Collections;
using System.Runtime.ExceptionServices;
using KSPTextureLoader.Utils;
using Unity.Profiling;
using UnityEngine;
using DebuggerDisplayAttribute = System.Diagnostics.DebuggerDisplayAttribute;

namespace KSPTextureLoader;

internal class TextureHandleImpl : IDisposable, ISetException, ICompleteHandler
{
    private static readonly ProfilerMarker CompleteMarker = new("TextureHandle.Complete");

    private readonly bool isReadable;
    internal int RefCount { get; private set; } = 1;
    internal string Path { get; private set; }
    internal string AssetBundle { get; private set; }

    private Texture texture;
    private ExceptionDispatchInfo exception;
    internal ICompleteHandler completeHandler;
    internal IEnumerator coroutine;

    public bool IsComplete => coroutine is null;
    public bool IsError => exception is not null;
    internal bool IsReadable => texture?.isReadable ?? isReadable;

    internal event Action<TextureHandle> OnCompleted;
    internal event Action<TextureHandle, Exception> OnError;

    internal TextureHandleImpl(string path, bool unreadable)
    {
        Path = path;
        isReadable = !unreadable;
    }

    internal TextureHandleImpl(string path, ExceptionDispatchInfo ex)
        : this(path, false)
    {
        exception = ex;
    }

    /// <summary>
    /// Get the texture for this texture handle. Will block if the texture has
    /// not loaded yet and will throw an exception if the texture failed to load.
    /// </summary>
    ///
    /// <remarks>
    /// You will need to keep this texture handle around or else the texture will
    /// be either destroyed or leaked when the last handle is disposed of.
    /// </remarks>
    public Texture GetTexture()
    {
        if (!IsComplete)
            WaitUntilComplete();

        exception?.Throw();
        return texture;
    }

    /// <summary>
    /// Take ownership of the texture referred to by this texture handle.
    /// Consumes the current texture handle.
    /// </summary>
    ///
    /// <remarks>
    /// If the handle has only one reference then this will remove the texture
    /// from the handle and return it. Otherwise, a copy of the texture will be
    /// made and the copy will be returned.
    /// </remarks>
    public Texture TakeTexture()
    {
        using var guard = this;

        var texture = GetTexture();
        if (RefCount == 1)
            this.texture = null;
        else
            texture = Texture.Instantiate<Texture>(texture);

        return texture;
    }

    /// <summary>
    /// Block until this texture has been loaded.
    /// </summary>
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

    internal CompleteHandlerGuard WithCompleteHandler(ICompleteHandler handler)
    {
        completeHandler = handler;
        return new CompleteHandlerGuard(this);
    }

    /// <summary>
    /// Get a new handle for the same texture and increase the reference count.
    /// </summary>
    /// <returns></returns>
    public TextureHandleImpl Acquire()
    {
        RefCount += 1;
        return this;
    }

    /// <summary>
    /// Decrese the reference count of this texture handle. If the reference
    /// count is decreased to zero then the texture will be destroyed.
    /// </summary>
    public void Dispose()
    {
        RefCount -= 1;
        if (RefCount < 0)
        {
            Debug.LogError(
                $"TextureHandle for texture at {Path} has been disposed of too many times!"
            );
        }

        if (RefCount != 0)
            return;

        var key = TextureLoader.CanonicalizeResourcePath(Path);
        TextureLoader.Instance.textures.Remove(key);

        if (texture != null)
            UnityEngine.Object.Destroy(texture);
        texture = null;
    }

    internal void SetTexture<T>(Texture tex, TextureLoadOptions options, string assetBundle = null)
        where T : Texture
    {
        texture = TextureLoader.ConvertTexture<T>(tex, options);
        texture.name = Path;
        AssetBundle = assetBundle;
        coroutine = null;
        completeHandler = null;

        try
        {
            OnCompleted(new TextureHandle(this));
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
            OnError(new TextureHandle(this), ex.SourceException);
        }
        catch (Exception e)
        {
            Debug.LogError($"[KSPTextureLoader] OnError event threw an exception");
            Debug.LogException(e);
        }
    }
}

/// <summary>
/// A shared reference to a texture.
/// </summary>
///
/// <remarks>
/// You get one of these by calling <see cref="TextureLoader.LoadTexture(string, TextureLoadOptions)"/>.
/// It will start off in a pending state and then will become complete at a
/// later time (unless cached, in which case it will be ready immediately).
///
/// You can get the loaded texture by calling <see cref="GetTexture"/>. If the
/// texture has not finished loading yet then this will block until loading is
/// complete. If loading fails then calling it will throw an exception. Make
/// sure to take this into account in your loading routine.
/// </remarks>
[DebuggerDisplay("{Path} (RefCount: {RefCount})")]
public class TextureHandle : CustomYieldInstruction, IDisposable
{
    internal readonly TextureHandleImpl handle;

    /// <summary>
    /// The current reference count of this texture handle.
    /// </summary>
    public int RefCount => handle.RefCount;

    /// <summary>
    /// The path that this texture was loaded from. This will either be a path
    /// on disk or an asset within an asset bundle, depending on whether
    /// <see cref="AssetBundle"/> is null or not.
    /// </summary>
    public string Path => handle.Path;

    /// <summary>
    /// The asset bundle that this texture was loaded from, or null if it was
    /// loaded from a texture file on disk.
    /// </summary>
    ///
    /// <remarks>
    /// This will always be null if the texture is currently loading or if the
    /// texture failed to load.
    /// </remarks>
    public string AssetBundle => handle.AssetBundle;

    /// <summary>
    /// Indicates whether the texture load has completed.
    /// </summary>
    public bool IsComplete => handle.IsComplete;

    /// <summary>
    /// Indicates whether the texture load completed with an error.
    /// </summary>
    public bool IsError => handle.IsError;

    /// <summary>
    /// An event that gets fired when the texture load completes successfully.
    /// </summary>
    public event Action<TextureHandle> OnCompleted
    {
        add => handle.OnCompleted += value;
        remove => handle.OnCompleted -= value;
    }

    /// <summary>
    /// An event that gets fired when the texture load completes with an error.
    /// </summary>
    public event Action<TextureHandle, Exception> OnError
    {
        add => handle.OnError += value;
        remove => handle.OnError -= value;
    }

    public override bool keepWaiting => !IsComplete;

    internal TextureHandle(TextureHandleImpl handle) => this.handle = handle;

    /// <summary>
    /// Get the texture for this texture handle. Will block if the texture has
    /// not loaded yet and will throw an exception if the texture failed to load.
    /// </summary>
    ///
    /// <remarks>
    /// You will need to keep this texture handle around or else the texture will
    /// be either destroyed or leaked when the last handle is disposed of.
    /// </remarks>
    public Texture GetTexture() => handle.GetTexture();

    /// <summary>
    /// Take ownership of the texture referred to by this texture handle.
    /// Consumes the current texture handle.
    /// </summary>
    ///
    /// <remarks>
    /// If the handle has only one reference then this will remove the texture
    /// from the handle and return it. Otherwise, a copy of the texture will be
    /// made and the copy will be returned.
    /// </remarks>
    public Texture TakeTexture() => handle.TakeTexture();

    /// <summary>
    /// Block until this texture has been loaded.
    /// </summary>
    public void WaitUntilComplete() => handle.WaitUntilComplete();

    /// <summary>
    /// Get a new handle for the same texture and increase the reference count.
    /// </summary>
    /// <returns></returns>
    public TextureHandle Acquire()
    {
        handle.Acquire();
        return this;
    }

    /// <summary>
    /// Decrese the reference count of this texture handle. If the reference
    /// count is decreased to zero then the texture will be destroyed.
    /// </summary>
    public void Dispose() => handle.Dispose();
}

/// <summary>
/// A shared reference to a specific type of texture.
/// </summary>
///
/// <remarks>
/// You get one of these by calling <see cref="TextureLoader.LoadTexture(string, TextureLoadOptions)"/>.
/// It will start off in a pending state and then will become complete at a
/// later time (unless cached, in which case it will be ready immediately).
///
/// You can get the loaded texture by calling <see cref="GetTexture"/>. If the
/// texture has not finished loading yet then this will block until loading is
/// complete. If loading fails then calling it will throw an exception. Make
/// sure to take this into account in your loading routine.
/// </remarks>
[DebuggerDisplay("{Path} (RefCount: {RefCount})")]
public sealed class TextureHandle<T> : TextureHandle
    where T : Texture
{
    internal TextureHandle(TextureHandleImpl handle)
        : base(handle) { }

    /// <summary>
    /// Get the texture for this texture handle. Will block if the texture has
    /// not loaded yet and will throw an exception if the texture failed to load.
    /// </summary>
    ///
    /// <remarks>
    /// You will need to keep this texture handle around or else the texture will
    /// be either destroyed or leaked when the last handle is disposed of.
    /// </remarks>
    public new T GetTexture()
    {
        var general = handle.GetTexture();
        if (general is not T texture)
            throw new InvalidCastException(
                $"Invalid cast: texture was loaded as type {general.GetType().Name} which cannot be casted to a texture of type {typeof(T).Name}"
            );

        return texture;
    }

    /// <summary>
    /// Take ownership of the texture referred to by this texture handle.
    /// Consumes the current texture handle.
    /// </summary>
    ///
    /// <remarks>
    /// If the handle has only one reference then this will remove the texture
    /// from the handle and return it. Otherwise, a copy of the texture will be
    /// made and the copy will be returned.
    /// </remarks>
    public new T TakeTexture()
    {
        var general = handle.TakeTexture();
        if (general is not T texture)
        {
            UnityEngine.Object.Destroy(general);
            throw new InvalidCastException(
                $"Invalid cast: texture was loaded as type {general.GetType().Name} which cannot be casted to a texture of type {typeof(T).Name}"
            );
        }

        return texture;
    }

    /// <summary>
    /// Get a new handle for the same texture and increase the reference count.
    /// </summary>
    /// <returns></returns>
    public new TextureHandle<T> Acquire()
    {
        handle.Acquire();
        return this;
    }
}
