using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Threading;

namespace KSPTextureLoader.Async;

internal class BlockingQueue<T>
{
    readonly object mutex = new();
    readonly ConcurrentQueue<T> queue = new();

    public void Add(T value)
    {
        lock (mutex)
        {
            queue.Enqueue(value);
            Monitor.Pulse(mutex);
        }
    }

    public bool TryTake(out T value) => queue.TryDequeue(out value);

    public T Take()
    {
        if (TryTake(out var value))
            return value;

        lock (mutex)
        {
            while (true)
            {
                if (TryTake(out value))
                    return value;

                Monitor.Wait(mutex);
            }
        }
    }
}
