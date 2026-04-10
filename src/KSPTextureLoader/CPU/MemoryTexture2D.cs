using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using KSPTextureLoader.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;

namespace KSPTextureLoader.CPU;

internal static unsafe class MemoryTexture2D
{
    internal readonly struct Factory(void* data, Allocator allocator)
        : CPUTexture2D.ICPUTexture2DFactory
    {
        public CPUTexture2D CreateTexture2D<T>(T texture)
            where T : ICPUTexture2D
        {
            if (allocator == AllocatorUtil.HGlobal)
                return new HGlobalMemoryTexture2D<T>(data, texture);

            return new MemoryTexture2D<T>(data, allocator, texture);
        }

        public CPUTexture2D CreateFallback(TextureFormat format)
        {
            if (allocator == AllocatorUtil.HGlobal)
                Marshal.FreeHGlobal((IntPtr)data);
            else
                UnsafeUtility.Free(data, allocator);

            throw new NotSupportedException(
                $"Unsupported texture format for memory-mapped CPU texture: {format}"
            );
        }
    }
}

internal sealed unsafe class MemoryTexture2D<TTexture> : CPUTexture2D<TTexture>
    where TTexture : ICPUTexture2D
{
    void* data;
    Allocator allocator;

    internal MemoryTexture2D(void* data, Allocator allocator, TTexture texture)
        : base(texture)
    {
        this.data = data;
        this.allocator = allocator;
    }

    ~MemoryTexture2D()
    {
        if (data is null)
            return;

        UnsafeUtility.Free(data, allocator);
    }

    public override void Dispose()
    {
        base.Dispose();
        GC.SuppressFinalize(this);

        UnsafeUtility.Free(data, allocator);
        data = null;
        allocator = Allocator.Invalid;
    }
}
