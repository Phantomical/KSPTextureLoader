using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Unity.Profiling;
using UnityEngine;

namespace KSPTextureLoader.CPU;

internal static unsafe class HGlobalMemoryTexture2D
{
    internal readonly struct Factory(void* data) : CPUTexture2D.ICPUTexture2DFactory
    {
        public CPUTexture2D CreateTexture2D<T>(T texture)
            where T : ICPUTexture2D
        {
            return new HGlobalMemoryTexture2D<T>(data, texture);
        }

        public CPUTexture2D CreateFallback(TextureFormat format)
        {
            Marshal.FreeHGlobal((IntPtr)data);
            throw new NotSupportedException(
                $"Unsupported texture format for memory-mapped CPU texture: {format}"
            );
        }
    }
}

internal sealed unsafe class HGlobalMemoryTexture2D<TTexture> : CPUTexture2D<TTexture>
    where TTexture : ICPUTexture2D
{
    static readonly ProfilerMarker FreeMarker = new("MemoryTexture2D.FreeHGlobal");

    void* data;

    internal HGlobalMemoryTexture2D(void* data, TTexture texture)
        : base(texture)
    {
        this.data = data;
    }

    ~HGlobalMemoryTexture2D()
    {
        if (data is not null)
            Marshal.FreeHGlobal((IntPtr)data);
    }

    public override void Dispose()
    {
        base.Dispose();

        if (data is not null)
        {
            var data = this.data;
            this.data = null;
            Task.Run(() =>
            {
                using var scope = FreeMarker.Auto();
                Marshal.FreeHGlobal((IntPtr)data);
            });
        }

        GC.SuppressFinalize(this);
    }
}
