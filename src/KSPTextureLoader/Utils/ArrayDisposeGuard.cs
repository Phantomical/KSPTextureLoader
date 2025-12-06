using System;

namespace KSPTextureLoader.Utils;

internal struct ArrayDisposeGuard<T>(T[] array) : IDisposable
    where T : IDisposable
{
    public readonly void Dispose()
    {
        foreach (var item in array)
            item?.Dispose();
    }
}
