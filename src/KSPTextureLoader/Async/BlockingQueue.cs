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

    public BlockingQueue()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(1000);

                lock (mutex)
                {
                    Monitor.Pulse(mutex);
                }
            }
        });
    }

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
            while (true)
            {
                if (TryDequeue(out value))
                    return value;

                Monitor.Wait(mutex);
            }
        }
    }
}
