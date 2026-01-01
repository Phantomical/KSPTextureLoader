using System;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;

namespace KSPTextureLoader.Utils;

/// <summary>
/// Utilities for creating and disposing of <see cref="NativeArray{T}"/>s using
/// <see cref="Marshal.AllocHGlobal(int)"/>.
/// </summary>
internal static unsafe class AllocatorUtil
{
    static readonly ProfilerMarker AllocMarker = new("Marshal.AllocHGlobal");
    static readonly ProfilerMarker FreeMarker = new("Marshal.FreeHGlobal");

    private const long MB = 1024 * 1024;

    internal const Allocator HGlobal = (Allocator)32;
    private static long allocMem = 0;
    internal static long AllocMem => Interlocked.Read(ref allocMem);
    internal static bool IsAboveWatermark
    {
        get
        {
            var watermark = Config.Instance.MaxTextureLoadMemory * MB;
            if (watermark == 0)
                return false;

            return (ulong)AllocMem > Config.Instance.MaxTextureLoadMemory * MB;
        }
    }

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
        Interlocked.Add(ref allocMem, length);

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
            Interlocked.Add(ref allocMem, -array.Length);
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

    internal static WaitUntil WaitUntilMemoryBelowWatermark() =>
        WaitUntilMemoryBelowWatermark(Config.Instance.MaxTextureLoadMemory * MB);

    internal static WaitUntil WaitUntilMemoryBelowWatermark(ulong watermark) =>
        new(() => watermark <= 0 || AllocMem <= 0 || (ulong)AllocMem < watermark);
}
