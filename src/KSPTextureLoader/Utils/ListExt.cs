using System.Collections.Generic;

namespace KSPTextureLoader.Utils;

internal static class ListExt
{
    public static bool TryPop<T>(this List<T> list, out T item)
    {
        if (list.Count == 0)
        {
            item = default;
            return false;
        }

        item = list[list.Count - 1];
        list.RemoveAt(list.Count - 1);
        return true;
    }
}
