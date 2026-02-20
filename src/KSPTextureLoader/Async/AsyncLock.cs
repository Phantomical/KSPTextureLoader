using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KSPTextureLoader.Async;

internal sealed class AsyncLock
{
    static Func<AsyncLock, LockGuard> CtorFunc = null;

    readonly Queue<TaskCompletionSource<LockGuard>> waiters = [];
    bool locked = false;

    static AsyncLock()
    {
        LockGuard.InternalInit();
    }

    public ValueTask<LockGuard> Lock()
    {
        lock (waiters)
        {
            if (!locked)
            {
                locked = true;
                return new(CtorFunc(this));
            }

            var tcs = new TaskCompletionSource<LockGuard>();
            waiters.Enqueue(tcs);
            return new(tcs.Task);
        }
    }

    public LockGuard? TryLock()
    {
        lock (waiters)
        {
            if (!locked)
            {
                locked = true;
                return CtorFunc(this);
            }

            return null;
        }
    }

    TaskCompletionSource<LockGuard> DoUnlock()
    {
        lock (waiters)
        {
            if (!locked)
                throw new InvalidOperationException(
                    "Attempted to unlock an AsyncLock that was not locked"
                );

            if (waiters.TryDequeue(out var task))
                return task;

            locked = false;
        }

        return null;
    }

    public void UnlockUnchecked()
    {
        while (true)
        {
            var task = DoUnlock();
            if (task is null)
                break;

            if (task.TrySetResult(CtorFunc(this)))
                break;
        }
    }

    ~AsyncLock()
    {
        foreach (var tcs in waiters)
            tcs.TrySetCanceled();
    }

    public struct LockGuard : IDisposable
    {
        public AsyncLock Lock { get; private set; }

        static LockGuard()
        {
            CtorFunc = asyncLock => new(asyncLock);
        }

        internal static void InternalInit() { }

        LockGuard(AsyncLock lck) => Lock = lck;

        public void Dispose()
        {
            Lock?.UnlockUnchecked();
            Lock = null;
        }
    }
}
