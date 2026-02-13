using System;

namespace KSPTextureLoader.Utils;

internal struct CPUCompleteHandlerGuard(CPUTextureHandle handle) : IDisposable
{
    public void Dispose() => handle.completeHandler = null;
}
