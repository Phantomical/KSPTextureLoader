using System;
using Unity.Collections;

namespace KSPTextureLoader.Utils;

public class NativeArrayGuard<T>(NativeArray<T> array = default) : IDisposable
    where T : unmanaged
{
    public NativeArray<T> array = array;

    public void Dispose() => array.Dispose();
}
