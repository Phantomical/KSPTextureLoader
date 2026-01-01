using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace KSPTextureLoader.Utils;

internal static class NativeArrayExt
{
    internal static unsafe void CopyRangeFrom<T>(
        this NativeArray<T> array,
        int offset,
        T[] source,
        int count
    )
        where T : unmanaged
    {
        if ((uint)offset >= (uint)array.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        if (offset + count > array.Length)
            throw new IndexOutOfRangeException(
                $"Out of bounds array copy. Attempted to copy from {offset} to {offset + count} but the array has a length of {array.Length}"
            );

        fixed (T* src = source)
        {
            UnsafeUtility.MemCpy((T*)array.GetUnsafePtr() + offset, src, sizeof(T) * count);
        }
    }
}
