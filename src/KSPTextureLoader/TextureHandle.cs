using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using KSPTextureLoader.Utils;
using Unity.Profiling;
using UnityEngine;

namespace KSPTextureLoader;

internal class TextureHandleImpl : IDisposable, ISetException, ICompleteHandler
{
    internal struct ExternalMarker;

    private static readonly ProfilerMarker CompleteMarker = new("TextureHandle.Complete");

    internal static readonly EventData<TextureHandleImpl> HandleCreated = new(
        "TextureHandle.Created"
    );
    internal static readonly EventData<TextureHandleImpl> HandleDestroyed = new(
        "TextureHandle.Destroyed"
    );

    private readonly bool isReadable;
    private readonly bool isExternal;
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
        HandleCreated.Fire(this);
    }

    internal TextureHandleImpl(string path, ExceptionDispatchInfo ex)
        : this(path, false) // HandleCreated fires in delegated ctor
    {
        exception = ex;
    }

    internal TextureHandleImpl(Texture texture)
    {
        if (texture == null)
            throw new ArgumentNullException(nameof(texture));

        Path = texture.name;
        isReadable = texture.isReadable;
        this.texture = texture;
        HandleCreated.Fire(this);
    }

    internal TextureHandleImpl(Texture texture, ExternalMarker _)
    {
        if (texture == null)
            throw new ArgumentNullException(nameof(texture));

        Path = texture.name;
        isReadable = texture.isReadable;
        isExternal = true;
        this.texture = texture;
        HandleCreated.Fire(this);
    }

    // If the texture gets leaked
    ~TextureHandleImpl()
    {
        if (texture is null)
            return;

        _ = AsyncUtil.LaunchMainThreadTask(() =>
        {
            if (TextureLoader.Instance is null)
                Destroy();
            else
                TextureLoader.Instance.QueueForDestroy(this);
        });
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

        if (RefCount == 1 && AssetBundle is null && !isExternal)
            this.texture = null;
        else
            texture = TextureUtils.CloneTexture(texture);

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

        if (isExternal)
        {
            texture = null;
            return;
        }

        if (!IsError && texture == null)
        {
            // Destroy the handle immediately if its texture has been taken.
            Destroy();
        }
        else if (TextureLoader.Instance is null)
        {
            // If for some reason the Instance is null then just destroy immediately.
            Destroy();
        }
        else
        {
            // The destroy queue will clear out the textures at the end of the frame.
            TextureLoader.Instance.QueueForDestroy(this);
        }
    }

    internal uint Destroy(bool immediate = false)
    {
        HandleDestroyed.Fire(this);

        var instance = TextureLoader.Instance;
        if (instance is not null)
        {
            var key = TextureLoader.CanonicalizeResourcePath(Path);
            if (
                instance.textures.TryGetValue(key, out var weak)
                && (!weak.TryGetTarget(out var handle) || ReferenceEquals(this, handle))
            )
            {
                instance.textures.Remove(key);
            }
        }

        if (texture == null)
            return 0;

        if (Config.Instance.DebugMode >= DebugLevel.Debug)
            Debug.Log($"[KSPTextureLoader] Unloading texture {Path}");

        uint size = texture.isReadable ? TextureUtils.GetTextureSizeInMemory(texture) : 0;

        if (immediate)
            Texture.DestroyImmediate(texture);
        else
            Texture.Destroy(texture);

        texture = null;
        GC.SuppressFinalize(this);
        return size;
    }

    public override string ToString()
    {
        if (isExternal)
            return Path;
        else
            return $"{Path} (RefCount: {RefCount})";
    }

    /// <summary>
    /// Efficiently wait for multiple texture handles to complete.
    /// </summary>
    /// <param name="handles"></param>
    /// <returns>An enumerator that yields textures in order in which they completed.</returns>
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
    public static IEnumerable<KeyValuePair<int, TextureHandle>> WaitBatch(
        IEnumerable<TextureHandle> handles
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
                if (pair.Value.handle.Tick())
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
            pairs[0].Value.handle.Step();
        }

        yield break;
    }

    /// <summary>
    /// Efficiently wait for multiple texture handles to complete.
    /// </summary>
    /// <param name="handles"></param>
    /// <returns>An enumerator that yields textures in order in which they completed.</returns>
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
    public static IEnumerable<KeyValuePair<int, TextureHandle<T>>> WaitBatch<T>(
        IEnumerable<TextureHandle<T>> handles
    )
        where T : Texture
    {
        var generic = handles.Select(handle => (TextureHandle)handle);
        foreach (var (index, handle) in WaitBatch(generic))
            yield return KeyValuePair.Create(index, (TextureHandle<T>)handle);
    }

    internal void SetTexture<T>(
        Texture tex,
        TextureLoadOptions options,
        TextureConvertOptions setOptions = default
    )
        where T : Texture
    {
        tex.name = Path;
        tex = TextureLoader.ConvertForHandle<T>(tex, ref options, setOptions);

        texture = tex;
        AssetBundle = setOptions.AssetBundle?.Path;
        coroutine = null;
        completeHandler = null;

        try
        {
            OnCompleted?.Invoke(new TextureHandle(this));
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
            OnError?.Invoke(new TextureHandle(this), ex.SourceException);
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
    /// Create a new <see cref="TextureHandle{T}"/> that takes ownership over an existing texture.
    /// </summary>
    public static TextureHandle<T> CreateOwningHandle<T>(T texture)
        where T : Texture
    {
        var handle = new TextureHandleImpl(texture);
        return new TextureHandle<T>(handle);
    }

    /// <summary>
    /// Create a <see cref="TextureHandle{T}"/> that wraps a texture that is owned externally.
    /// </summary>
    ///
    /// <remarks>
    /// The handle returned by this function will not destroy the texture when its reference count
    /// hits zero, so it is safe to use for GameDatabase textures or built-in unity textures.
    /// </remarks>
    public static TextureHandle<T> CreateExternalHandle<T>(T texture)
        where T : Texture
    {
        var handle = new TextureHandleImpl(texture, new TextureHandleImpl.ExternalMarker());
        return new TextureHandle<T>(handle);
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

    /// <summary>
    /// Run a single iteration of the internal load coroutine, if it is not
    /// waiting on an operation to complete.
    /// </summary>
    /// <returns>true if the handle is ready</returns>
    ///
    /// <remarks>
    /// This allows you to make forward progress in a sync context even if you
    /// don't necessarily want to block on the handle at this moment.
    /// </remarks>
    public bool Tick() => handle.Tick();

    public override string ToString() => handle.ToString();
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
