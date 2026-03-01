using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace KSPTextureLoader.Async;

/// <summary>
/// A very simple blocking queue using a concurrent queue and a monitor to
/// do the actual synchronization.
/// </summary>
/// <typeparam name="T"></typeparam>
internal class BlockingQueue<T>
{
    readonly object mutex = new();
    readonly ConcurrentQueue<T> queue = new();

    /// <summary>
    /// Enqueue a new item.
    /// </summary>
    /// <param name="value"></param>
    public void Enqueue(T value)
    {
        lock (mutex)
        {
            queue.Enqueue(value);
            Monitor.Pulse(mutex);
        }
    }

    /// <summary>
    /// Dequeue an element from the queue if there is one available.
    /// Does not block.
    /// </summary>
    public bool TryDequeue(out T value) => queue.TryDequeue(out value);

    /// <summary>
    /// Dequeue an element from the queue, blocking until one is available.
    /// </summary>
    /// <returns></returns>
    public T Dequeue()
    {
        if (TryDequeue(out var value))
            return value;

        lock (mutex)
        {
            int count = 0;
            bool reported = false;
            while (true)
            {
                if (TryDequeue(out value))
                    return value;

                if (count > 30 && !reported)
                {
                    Report.DumpDeadlockReport();
                    reported = true;
                }

                if (count > 120)
                {
                    reported = false;
                    count = 0;
                }

                Monitor.Wait(mutex, 1000);
                count += 1;
            }
        }
    }
}
