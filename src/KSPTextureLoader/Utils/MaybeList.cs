using System;
using System.Collections;
using System.Collections.Generic;

namespace KSPTextureLoader.Utils;

internal struct MaybeList<T> : IEnumerable<T>
{
    private static readonly List<T> Empty = [];

    List<T> list;

    public readonly int Count => list?.Count ?? 0;
    public readonly T this[int index]
    {
        get
        {
            var list = this.list ?? Empty;
            return list[index];
        }
        set
        {
            var list = this.list ?? Empty;
            list[index] = value;
        }
    }

    public MaybeList(int capacity)
    {
        if (capacity == 0)
            return;

        list = new(capacity);
    }

    public void Add(T value)
    {
        list ??= [];
        list.Add(value);
    }

    public readonly List<T>.Enumerator GetEnumerator() => (list ?? Empty).GetEnumerator();

    readonly IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    readonly IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
