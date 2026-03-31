using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace KSPTextureLoader.Utils;

public unsafe struct LargeNativeArray<T> : IDisposable
    where T : unmanaged
{
    T* data;
    long length;
    Allocator allocator;

    public readonly long Length => length;
    public readonly bool IsCreated => data is not null;
    public readonly Allocator Allocator => allocator;

    public ref T this[long index] => ref data[index];
    public ref T this[int index] => ref this[(long)index];

    public LargeNativeArray(
        long length,
        Allocator allocator,
        NativeArrayOptions options = NativeArrayOptions.ClearMemory
    )
    {
        Allocate(length, allocator, out this);
        if (options.HasFlag(NativeArrayOptions.ClearMemory))
            UnsafeUtility.MemClear(data, sizeof(T) * Length);
    }

    public LargeNativeArray(T* data, long length, Allocator allocator)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (data is null && length != 0)
            throw new ArgumentNullException(nameof(data));

        this.data = data;
        this.length = length;
        this.allocator = allocator;
    }

    static void Allocate(long length, Allocator allocator, out LargeNativeArray<T> array)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        long size = sizeof(T) * length;
        array = default;
        array.data = (T*)UnsafeUtility.Malloc(size, UnsafeUtility.AlignOf<T>(), allocator);
        array.length = length;
        array.allocator = allocator;

        if (array.data is null)
        {
            array = default;
            throw new OutOfMemoryException();
        }
    }

    public void Dispose()
    {
        UnsafeUtility.Free(data, allocator);
        this = default;
    }

    public readonly T* GetUnsafePtr() => data;

    public readonly LargeNativeArray<U> Reinterpret<U>()
        where U : unmanaged
    {
        long byteLength = length * sizeof(T);
        if (byteLength % sizeof(U) != 0)
            throw new InvalidOperationException(
                $"Cannot reinterpret LargeNativeArray<{typeof(T)}> (byte length {byteLength}) as LargeNativeArray<{typeof(U)}> (element size {sizeof(U)})"
            );

        return new LargeNativeArray<U>((U*)data, byteLength / sizeof(U), allocator);
    }

    public readonly LargeNativeArray<T> GetSubArray(long start, long length)
    {
        if ((ulong)start > (ulong)this.length)
            throw new ArgumentOutOfRangeException(nameof(start));
        if ((ulong)length > (ulong)(this.length - start))
            throw new ArgumentOutOfRangeException(nameof(length));

        return new LargeNativeArray<T>(data + start, length, Allocator.Invalid);
    }

    public readonly NativeArray<T> AsNativeArray()
    {
        if (length > int.MaxValue)
            throw new InvalidOperationException(
                $"LargeNativeArray is too large to convert to NativeArray (length {length})"
            );

        return NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(
            data,
            (int)length,
            Allocator.Invalid
        );
    }

    public static LargeNativeArray<T> FromNativeArray(NativeArray<T> array)
    {
        return new LargeNativeArray<T>(
            (T*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(array),
            array.Length,
            Allocator.Invalid
        );
    }

    public static implicit operator LargeNativeArray<T>(NativeArray<T> array) =>
        FromNativeArray(array);

    public readonly T[] ToArray()
    {
        if (length > int.MaxValue)
            throw new InvalidOperationException(
                $"LargeNativeArray is too large to convert to a managed array (length {length})"
            );

        var result = new T[(int)length];
        fixed (T* dst = result)
        {
            UnsafeUtility.MemCpy(dst, data, length * sizeof(T));
        }
        return result;
    }
}
