using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KSPTextureLoader.Async;

internal sealed class AsyncConditionVariable(AsyncLock asyncLock)
{
    struct Empty;

    readonly AsyncLock asyncLock = asyncLock;
    readonly Queue<TaskCompletionSource<Empty>> waiters = [];

    /// <summary>
    /// Atomically unlocks the guard and blocks until the condvar is notified.
    /// </summary>
    /// <param name="guard"></param>
    /// <returns></returns>
    public Task Wait(AsyncLock.LockGuard guard)
    {
        if (!ReferenceEquals(asyncLock, guard.Lock))
            throw new InvalidOperationException(
                "Wait cannot be used with a guard for a different lock"
            );

        lock (waiters)
        {
            var tcs = new TaskCompletionSource<Empty>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            waiters.Enqueue(tcs);
            guard.Lock.UnlockUnchecked();
            return tcs.Task;
        }
    }

    /// <summary>
    /// Wake up a waiter and transfer the lock to them.
    /// </summary>
    /// <param name="guard"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public void Notify(AsyncLock.LockGuard guard)
    {
        if (!ReferenceEquals(asyncLock, guard.Lock))
        {
            guard.Dispose();
            throw new InvalidOperationException(
                "Notify called with a lock guard from a different lock"
            );
        }

        lock (waiters)
        {
            if (!waiters.TryDequeue(out var tcs))
            {
                guard.Dispose();
                return;
            }

            // Do not unlock the lock, it the guard transfers to a waiting task.
            tcs.SetResult(default);
        }
    }

    /// <summary>
    /// Notify a waiter, launches a background task if the lock is already locked.
    /// </summary>
    public async void Notify()
    {
        Notify(await asyncLock.Lock());
    }
}
