using System;
using Unity.Collections;

namespace KSPTextureLoader.Utils;

internal class NativeArrayGuard<T>(NativeArray<T> array = default) : IDisposable
    where T : unmanaged
{
    public NativeArray<T> array = array;

    public void Dispose() => array.Dispose();
}
