using System;
using System.Runtime.CompilerServices;

namespace KSPTextureLoader.Utils;

#pragma warning disable CS8500 // This takes the address of, gets the size of, or declares a pointer to a managed type

internal struct FixedArray16<T>
{
    T m00;
    T m01;
    T m02;
    T m03;
    T m04;
    T m05;
    T m06;
    T m07;
    T m08;
    T m09;
    T m10;
    T m11;
    T m12;
    T m13;
    T m14;
    T m15;

    public readonly int Length => 16;

    public unsafe ref T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Length)
                ThrowOutOfBoundsException(index);

            fixed (T* items = &m00)
                return ref items[index];
        }
    }

    public void Clear()
    {
        this = default;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static T ThrowOutOfBoundsException(int index) =>
        throw new IndexOutOfRangeException($"index {index} is out of range");
}
