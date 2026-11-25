using System;
using UnityEngine;

namespace KSPTextureLoader.CPU;

internal static class MemoryTexture2D
{
    internal readonly struct Factory<TStorage>(TStorage storage) : CPUTexture2D.ICPUTexture2DFactory
        where TStorage : struct, IDisposable
    {
        public CPUTexture2D CreateTexture2D<T>(T texture)
            where T : ICPUTexture2D => new MemoryTexture2D<T, TStorage>(texture, storage);

        public CPUTexture2D CreateFallback(TextureFormat format) =>
            throw new NotSupportedException($"Unsupported format for memory-backed CPU texture: {format}");
    }
}

internal sealed class MemoryTexture2D<TTexture, TStorage>(TTexture texture, TStorage storage)
    : CPUTexture2D<TTexture>(texture)
    where TTexture : ICPUTexture2D
    where TStorage : struct, IDisposable
{
    TStorage? backing = storage;

    ~MemoryTexture2D() => backing?.Dispose();

    public override void Dispose()
    {
        base.Dispose();
        backing?.Dispose();
        backing = null;
        GC.SuppressFinalize(this);
    }
}
