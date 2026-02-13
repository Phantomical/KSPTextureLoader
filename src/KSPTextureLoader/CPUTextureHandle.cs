using System;
using System.Collections;
using System.Runtime.ExceptionServices;

namespace KSPTextureLoader;

public class CPUTextureHandle : IDisposable, ISetException, ICompleteHandler
{
    internal int RefCount { get; private set; } = 1;
    internal string Path { get; private set; }
    internal string AssetBundle { get; private set; }

    private CPUTexture2D texture;
    private ExceptionDispatchInfo exception;
    internal ICompleteHandler completeHandler;
    internal IEnumerator coroutine;

    public bool IsComplete => coroutine is null;
    public bool IsError => exception is not null;

    internal event Action<TextureHandle> OnCompleted;
    internal event Action<TextureHandle, Exception> OnError;

    internal CPUTextureHandle(string path)
    {
        Path = path;
    }

    internal CPUTextureHandle(string path, ExceptionDispatchInfo ex)
        : this(path)
    {
        exception = ex;
    }
}
