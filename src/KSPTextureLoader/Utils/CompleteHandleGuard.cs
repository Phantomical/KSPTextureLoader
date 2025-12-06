using System;

namespace KSPTextureLoader.Utils;

internal struct CompleteHandlerGuard(TextureHandleImpl handle) : IDisposable
{
    public void Dispose() => handle.completeHandler = null;
}
