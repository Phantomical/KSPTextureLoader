using System;
using System.Runtime.ExceptionServices;
using Unity.IO.LowLevel.Unsafe;

namespace KSPTextureLoader;

internal interface IFileReadStatus : IDisposable
{
    void ThrowIfError();
}

internal class ReadHandleStatus(ReadHandle handle) : IFileReadStatus
{
    public void ThrowIfError()
    {
        if (handle.Status != ReadStatus.Complete)
            throw new Exception("Failed to read texture data from file");
    }

    public void Dispose()
    {
        if (!handle.IsValid())
            return;
        if (handle.Status == ReadStatus.InProgress)
            handle.JobHandle.Complete();

        handle.Dispose();
    }
}

internal class SavedExceptionStatus : IFileReadStatus
{
    public ExceptionDispatchInfo exception;

    public void ThrowIfError()
    {
        exception?.Throw();
    }

    public void Dispose() { }
}
