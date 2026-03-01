using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KSPTextureLoader.Utils;

namespace KSPTextureLoader.Async;

internal class LoaderSynchronizationContext : SynchronizationContext
{
    static LoaderSynchronizationContext()
    {
        _ = AllocatorUtil.IsAboveWatermark;
    }

    readonly struct WorkItem(SendOrPostCallback cb, object state, ManualResetEventSlim evt = null)
    {
        readonly SendOrPostCallback cb = cb;
        readonly object state = state;
        readonly ManualResetEventSlim evt = evt;

        public void Invoke()
        {
            try
            {
                cb(state);
            }
            finally
            {
                evt?.Set();
            }
        }
    }

    readonly Queue<WorkItem> queue = [];
    readonly BlockingQueue<WorkItem> mailbox = new();

    readonly int mainThreadId = Thread.CurrentThread.ManagedThreadId;

    public override void Post(SendOrPostCallback d, object state)
    {
        if (Thread.CurrentThread.ManagedThreadId == mainThreadId)
            queue.Enqueue(new(d, state));
        else
            mailbox.Enqueue(new(d, state));
    }

    public override void Send(SendOrPostCallback d, object state)
    {
        if (Thread.CurrentThread.ManagedThreadId == mainThreadId)
        {
            Execute(new(d, state));
        }
        else
        {
            var evt = new ManualResetEventSlim();
            mailbox.Enqueue(new(d, state, evt));
            evt.Wait();
        }
    }

    public void Submit(SendOrPostCallback d, object state)
    {
        if (Thread.CurrentThread.ManagedThreadId == mainThreadId)
            Execute(new(d, state));
        else
            Post(d, state);
    }

    bool DrainMailbox()
    {
        bool progress = false;
        while (mailbox.TryDequeue(out var item))
        {
            queue.Enqueue(item);
            progress = true;
        }

        return progress;
    }

    void BlockMailbox()
    {
        queue.Enqueue(mailbox.Dequeue());
    }

    void Execute(WorkItem item)
    {
        var context = Current;
        Report.OuterContext = context;
        try
        {
            SetSynchronizationContext(this);
            item.Invoke();
        }
        finally
        {
            SetSynchronizationContext(context);
        }
    }

    public void Update()
    {
        DrainMailbox();

        while (true)
        {
            if (!queue.TryDequeue(out var item))
            {
                if (DrainMailbox())
                    continue;
                break;
            }

            Execute(item);
        }
    }

    public void UpdateIfOnMainThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != mainThreadId)
            return;

        Update();
    }

    public void WaitUntilComplete(Task task)
    {
        if (Thread.CurrentThread.ManagedThreadId != mainThreadId)
            throw new InvalidOperationException("Cannot block on tasks outside of the main thread");

        // Ensure that we get a callback when the task completes
        task.ContinueWith(
            (_, state) =>
            {
                var ctx = (LoaderSynchronizationContext)state;
                ctx.Post(static _ => { }, null);
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

        if (!task.IsCompleted)
        {
            while (true)
            {
                Update();

                if (task.IsCompleted)
                    break;

                BlockMailbox();
            }
        }
    }

    public void RunUntilComplete(Task task)
    {
        WaitUntilComplete(task);
        task.GetAwaiter().GetResult();
    }

    public T RunUntilComplete<T>(Task<T> task)
    {
        WaitUntilComplete(task);
        return task.Result;
    }

    public bool Tick(Task task)
    {
        Update();
        return task.IsCompleted;
    }
}
