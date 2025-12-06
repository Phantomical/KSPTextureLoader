using System;
using Unity.IO.LowLevel.Unsafe;
using Unity.Jobs;

namespace KSPTextureLoader.Utils;

internal class SafeReadHandleGuard(ReadHandle handle) : IDisposable
{
    public JobHandle JobHandle = handle.JobHandle;
    public ReadHandle Handle = handle;

    public ReadStatus Status => Handle.Status;

    public void Dispose()
    {
        if (!Handle.IsValid())
            return;
        if (!JobHandle.IsCompleted)
            JobHandle.Complete();
        Handle.Dispose();
    }
}
