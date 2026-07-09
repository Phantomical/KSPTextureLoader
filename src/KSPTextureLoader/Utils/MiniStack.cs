using System;
using System.Collections.Generic;

namespace KSPTextureLoader.Utils;

internal struct MiniStack<T>
    where T : class
{
    object data;

    public void Push(T value)
    {
        switch (data)
        {
            case null:
                data = value;
                break;

            case T top:
                var nstack = new Stack<T>(2);
                nstack.Push(top);
                nstack.Push(value);
                data = nstack;
                break;

            case Stack<T> stack:
                stack.Push(value);
                break;

            default:
                throw new InvalidOperationException("MiniStack was in an invalid state");
        }
    }

    public bool TryPop(out T value)
    {
        switch (data)
        {
            case null:
                value = null;
                return false;

            case T top:
                value = top;
                data = null;
                return true;

            case Stack<T> stack:
                return stack.TryPop(out value);

            default:
                throw new InvalidOperationException("MiniStack was in an invalid state");
        }
    }

    public T Pop()
    {
        if (!TryPop(out T value))
            throw new InvalidOperationException("Attempted to Pop on an empty stack");
        return value;
    }

    public readonly bool TryPeek(out T value)
    {
        switch (data)
        {
            case null:
                value = null;
                return false;

            case T top:
                value = top;
                return true;

            case Stack<T> stack:
                return stack.TryPeek(out value);

            default:
                throw new InvalidOperationException("MiniStack was in an invalid state");
        }
    }

    public void Clear()
    {
        data = null;
    }
}
