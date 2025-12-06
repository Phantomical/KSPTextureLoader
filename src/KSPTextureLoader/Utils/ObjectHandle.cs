using System;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace KSPTextureLoader.Utils;

internal struct ObjectHandle<T>(T value) : IDisposable
    where T : class
{
    GCHandle handle = GCHandle.Alloc(value);

    public T Target => (T)handle.Target;

    public void Dispose() => handle.Free();

    public void Dispose(JobHandle job)
    {
        new DisposeJob { handle = this }.Schedule(job);
    }

    struct DisposeJob : IJob
    {
        public ObjectHandle<T> handle;

        public void Execute() => handle.Dispose();
    }
}
