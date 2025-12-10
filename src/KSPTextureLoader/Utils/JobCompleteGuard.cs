using System;
using Unity.Jobs;

namespace KSPTextureLoader.Utils;

internal class JobCompleteGuard(JobHandle handle) : IDisposable
{
    public JobHandle JobHandle = handle;

    public void Dispose()
    {
        if (!JobHandle.IsCompleted)
            JobHandle.Complete();
    }
}
