using System;
using System.Threading.Tasks;
using Unity.Collections;

namespace KSPTextureLoader.Utils;

internal readonly struct NativeArrayAsyncGuard<T>(NativeArray<T> array) : IDisposable
    where T : unmanaged
{
    readonly NativeArray<T> array = array;

    public void Dispose()
    {
        var buffer = array;
        _ = Task.Run(() => buffer.DisposeExt());
    }
}
