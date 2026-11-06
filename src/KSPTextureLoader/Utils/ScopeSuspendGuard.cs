using System;
using Unity.Profiling;
#if UNITY_6_7
using Unity.Profiling.LowLevel.Unsafe;
#endif

namespace KSPTextureLoader.Utils;

internal readonly struct ScopeSuspendGuard : IDisposable
{
    readonly ProfilerMarker.AutoScope scope;

    public ScopeSuspendGuard(ProfilerMarker.AutoScope scope)
    {
        this.scope = scope;
#if UNITY_6_7
        ProfilerUnsafeUtility.EndSample(scope.m_Ptr);
#else
        ProfilerMarker.Internal_End(scope.m_Ptr);
#endif
    }

    public void Dispose()
    {
#if UNITY_6_7
        ProfilerUnsafeUtility.BeginSample(scope.m_Ptr);
#else
        ProfilerMarker.Internal_Begin(scope.m_Ptr);
#endif
    }
}

internal static class AutoScopeExt
{
    public static ScopeSuspendGuard Suspend(this ProfilerMarker.AutoScope scope) => new(scope);
}
