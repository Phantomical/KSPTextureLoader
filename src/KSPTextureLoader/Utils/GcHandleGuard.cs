using System;
using Unity.Collections.LowLevel.Unsafe;

namespace KSPTextureLoader.Utils;

internal struct GcHandleGuard(ulong gchandle) : IDisposable
{
    public void Dispose() => UnsafeUtility.ReleaseGCObject(gchandle);
}
