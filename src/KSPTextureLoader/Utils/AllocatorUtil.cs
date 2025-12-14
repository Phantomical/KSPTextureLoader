using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Profiling;

namespace KSPTextureLoader.Utils;

/// <summary>
/// Utilities for creating and disposing of <see cref="NativeArray{T}"/>s using
/// <see cref="Marshal.AllocHGlobal(int)"/>.
/// </summary>
internal static unsafe class AllocatorUtil
{
    static readonly ProfilerMarker AllocMarker = new("Marshal.AllocHGlobal");
    static readonly ProfilerMarker FreeMarker = new("Marshal.FreeHGlobal");

    internal const Allocator HGlobal = (Allocator)32;

    internal static NativeArray<T> CreateNativeArrayHGlobal<T>(
        int length,
        NativeArrayOptions options = NativeArrayOptions.ClearMemory
    )
        where T : unmanaged
    {
        using var scope = AllocMarker.Auto();
        var ptr = (void*)Marshal.AllocHGlobal(sizeof(T) * length);
        if (ptr is null)
            throw new OutOfMemoryException("failed to allocate an array using AllocHGlobal");

        var array = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(
            ptr,
            length,
            HGlobal
        );

        if (options == NativeArrayOptions.ClearMemory)
            UnsafeUtility.MemClear(ptr, sizeof(T) * length);

        return array;
    }

    struct DisposeJob<T>(NativeArray<T> array) : IJob
        where T : unmanaged
    {
        public NativeArray<T> array = array;

        public void Execute()
        {
            array.DisposeExt();
        }
    }

    internal static void DisposeExt<T>(this ref NativeArray<T> array)
        where T : unmanaged
    {
        if (array.m_AllocatorLabel == HGlobal)
        {
            using var scope = FreeMarker.Auto();
            Marshal.FreeHGlobal((IntPtr)array.GetUnsafePtr());
        }
        else
            array.Dispose();

        array = default;
    }

    internal static void DisposeExt<T>(this ref NativeArray<T> array, JobHandle dependsOn)
        where T : unmanaged
    {
        new DisposeJob<T>(array).Schedule(dependsOn);
        array = default;
    }
}
