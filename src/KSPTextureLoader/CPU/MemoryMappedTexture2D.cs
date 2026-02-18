using System;
using System.IO.MemoryMappedFiles;
using UnityEngine;

namespace KSPTextureLoader.CPU;

internal static class MemoryMappedTexture2D
{
    internal readonly struct Factory(
        MemoryMappedFile mmf,
        MemoryMappedViewAccessor accessor,
        TextureFormat format
    ) : CPUTexture2D.ICPUTexture2DFactory
    {
        public CPUTexture2D CreateTexture2D<T>(T texture)
            where T : ICPUTexture2D
        {
            return new MemoryMappedTexture2D<T>(mmf, accessor, texture);
        }

        public CPUTexture2D CreateFallback()
        {
            accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
            accessor?.Dispose();
            mmf?.Dispose();
            throw new NotSupportedException(
                $"Unsupported texture format for memory-mapped CPU texture: {format}"
            );
        }
    }
}

internal sealed class MemoryMappedTexture2D<TTexture> : CPUTexture2D<TTexture>
    where TTexture : ICPUTexture2D
{
    MemoryMappedFile mmf;
    MemoryMappedViewAccessor accessor;

    internal MemoryMappedTexture2D(
        MemoryMappedFile mmf,
        MemoryMappedViewAccessor accessor,
        TTexture texture
    )
        : base(texture)
    {
        this.mmf = mmf;
        this.accessor = accessor;
    }

    ~MemoryMappedTexture2D()
    {
        DoDispose();
    }

    public override void Dispose()
    {
        base.Dispose();
        DoDispose();
        GC.SuppressFinalize(this);
    }

    private void DoDispose()
    {
        accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
        accessor?.Dispose();
        mmf?.Dispose();

        accessor = null;
        mmf = null;
    }
}
