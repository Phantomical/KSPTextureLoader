using System;
using Unity.Profiling;

namespace KSPTextureLoader.Utils;

internal readonly struct ScopeSuspendGuard : IDisposable
{
    readonly ProfilerMarker.AutoScope scope;

    public ScopeSuspendGuard(ProfilerMarker.AutoScope scope)
    {
        this.scope = scope;
        ProfilerMarker.Internal_End(scope.m_Ptr);
    }

    public void Dispose()
    {
        ProfilerMarker.Internal_Begin(scope.m_Ptr);
    }
}
