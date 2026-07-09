using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace KSPTextureLoader.Utils;

internal static class AsyncUtil
{
    struct Empty;

    static bool IsMainThread => TextureLoader.Context.IsMainThread;

    #region LaunchMainThreadTask

    abstract class MainThreadTask<T>()
        : TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        // We use AsyncLocal to preserve some state throughout the call chain, so
        // we need to carry the ExecutionContext into dependent calls.
        readonly ExecutionContext executionContext = ExecutionContext.Capture();

        // However, we want tasks run on the main thread to use the loader
        // synchronization context, so we overwrite that in the execution context.
        SynchronizationContext syncContext;

        static readonly ContextCallback RunExecute = static state =>
        {
            var task = (MainThreadTask<T>)state;

            SynchronizationContext.SetSynchronizationContext(task.syncContext);
            task.Execute();
        };

        protected abstract void Execute();

        public static void Callback(object state)
        {
            var task = (MainThreadTask<T>)state;

            try
            {
                if (task.executionContext is null)
                {
                    task.Execute();
                }
                else
                {
                    task.syncContext = SynchronizationContext.Current;
                    ExecutionContext.Run(task.executionContext, RunExecute, task);
                }
            }
            catch (Exception e)
            {
                task.TrySetException(e);
            }
        }

        public void Submit()
        {
            TextureLoader.Context.Submit(Callback, this);
        }
    }

    sealed class FuncTaskTask(Func<Task> func) : MainThreadTask<Empty>
    {
        readonly Func<Task> func = func;

        protected override void Execute()
        {
            func().ContinueWith(Continue, this);
        }

        static void Continue(Task task, object state)
        {
            var ft = (FuncTaskTask)state;

            try
            {
                task.GetAwaiter().GetResult();
                ft.SetResult(default);
            }
            catch (Exception e)
            {
                ft.TrySetException(e);
            }
        }
    }

    public static Task LaunchMainThreadTask(Func<Task> func)
    {
        var task = new FuncTaskTask(func);
        task.Submit();
        return task.Task;
    }

    sealed class FuncTaskTTask<T>(Func<Task<T>> func) : MainThreadTask<T>
    {
        readonly Func<Task<T>> func = func;

        protected override void Execute()
        {
            func().ContinueWith(Continue, this);
        }

        static void Continue(Task<T> task, object state)
        {
            var ft = (FuncTaskTTask<T>)state;

            try
            {
                ft.SetResult(task.Result);
            }
            catch (Exception e)
            {
                ft.TrySetException(e);
            }
        }
    }

    public static Task<T> LaunchMainThreadTask<T>(Func<Task<T>> func)
    {
        var task = new FuncTaskTTask<T>(func);
        task.Submit();
        return task.Task;
    }

    sealed class ActionTask(Action func) : MainThreadTask<Empty>
    {
        readonly Action func = func;

        protected override void Execute()
        {
            func();
            TrySetResult(default);
        }
    }

    public static Task LaunchMainThreadTask(Action func)
    {
        var task = new ActionTask(func);
        task.Submit();
        return task.Task;
    }

    sealed class FuncTask<T>(Func<T> func) : MainThreadTask<T>
    {
        readonly Func<T> func = func;

        protected override void Execute()
        {
            TrySetResult(func());
        }
    }

    public static Task<T> LaunchMainThreadTask<T>(Func<T> func)
    {
        var task = new FuncTask<T>(func);
        task.Submit();
        return task.Task;
    }
    #endregion

    #region WaitForJob
    struct NotifyJob(TaskCompletionSource<Empty> tcs) : IJob
    {
        ObjectHandle<TaskCompletionSource<Empty>> tcs = new(tcs);

        public void Execute()
        {
            using var _guard = tcs;
            tcs.Target.TrySetResult(default);
        }
    }

    public static Task WaitFor(JobHandle handle)
    {
        TaskCompletionSource<Empty> tcs;
        if (IsMainThread)
        {
            if (handle.IsCompleted)
                return Task.CompletedTask;

            tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = new NotifyJob(tcs);
            job.Schedule(handle);
            JobHandle.ScheduleBatchedJobs();
        }
        else
        {
            tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            LaunchMainThreadTask(() =>
            {
                if (handle.IsCompleted)
                {
                    tcs.TrySetResult(default);
                    return;
                }

                var job = new NotifyJob(tcs);
                job.Schedule(handle);
                JobHandle.ScheduleBatchedJobs();
            });
        }

        return tcs.Task;
    }
    #endregion

    #region WaitForAssetBundle
    class AssetBundleCompletionSource : TaskCompletionSource<AssetBundle>, ICompleteHandler
    {
        readonly AssetBundleCreateRequest request;
        readonly ICompletionContext context;

        public bool IsComplete => Task.IsCompleted;

        public AssetBundleCompletionSource(
            AssetBundleCreateRequest request,
            ICompletionContext context
        )
            : base(TaskCreationOptions.RunContinuationsAsynchronously)
        {
            this.request = request;
            this.context = context;
            request.completed += OnCompleted;
        }

        public void WaitUntilComplete()
        {
            if (IsComplete)
                return;

            request.priority = 100;
            OnCompleted(request);
        }

        void OnCompleted(AsyncOperation _)
        {
            try
            {
                var bundle = request.assetBundle;
                if (bundle == null)
                    throw new Exception("Asset bundle failed to load");

                SetResult(bundle);
            }
            catch (Exception e)
            {
                TrySetException(e);
            }

            // A null context means the wait was launched without a completion
            // context (e.g. TextureBundleLoader.CreateAsync); nobody can block on
            // it, so there is nothing to unregister.
            context?.MarkCompleted(this);
        }
    }

    /// <summary>
    /// Wait for an asset bundle to finish loading.
    /// </summary>
    /// <param name="handle"></param>
    /// <returns></returns>
    public static Task<AssetBundle> WaitFor(AssetBundleCreateRequest handle)
    {
        if (!IsMainThread)
            return LaunchMainThreadTask(() => WaitFor(handle));

        var context = CompletionContext.Current;
        var tcs = new AssetBundleCompletionSource(handle, context);
        // Without a completion context the operation can only complete via the
        // player loop; there is nothing to force it through under a blocking wait.
        context?.MarkBlockedOn(tcs);

        return tcs.Task;
    }
    #endregion

    #region WaitForAsset
    class AssetCompletionSource<T> : TaskCompletionSource<T>, ICompleteHandler
        where T : UnityEngine.Object
    {
        readonly AssetBundleRequest request;
        readonly ICompletionContext context;

        public bool IsComplete => Task.IsCompleted;

        public AssetCompletionSource(AssetBundleRequest request, ICompletionContext context)
            : base(TaskCreationOptions.RunContinuationsAsynchronously)
        {
            this.request = request;
            this.context = context;
            request.completed += OnCompleted;
        }

        public void WaitUntilComplete()
        {
            if (IsComplete)
                return;

            request.priority = 100;
            OnCompleted(request);
        }

        void OnCompleted(AsyncOperation _)
        {
            try
            {
                var asset = (T)request.asset;
                if (asset == null)
                    throw new Exception("Asset failed to load from asset bundle");

                SetResult(asset);
            }
            catch (Exception e)
            {
                TrySetException(e);
            }

            // A null context means the wait was launched without a completion
            // context (e.g. TextureBundleLoader.CreateAsync); nobody can block on
            // it, so there is nothing to unregister.
            context?.MarkCompleted(this);
        }
    }

    /// <summary>
    /// Wait for an asset bundle to finish loading.
    /// </summary>
    /// <param name="handle"></param>
    /// <returns></returns>
    public static Task<T> WaitFor<T>(AssetBundleRequest handle)
        where T : UnityEngine.Object
    {
        if (!IsMainThread)
            return LaunchMainThreadTask(() => WaitFor<T>(handle));

        var context = CompletionContext.Current;
        var tcs = new AssetCompletionSource<T>(handle, context);
        // Without a completion context the operation can only complete via the
        // player loop; there is nothing to force it through under a blocking wait.
        context?.MarkBlockedOn(tcs);

        return tcs.Task;
    }
    #endregion

    #region RunOnGraphicsThread
    /// <summary>
    /// Runs a function on the graphics thread and returns a task that completes
    /// when it finishes.
    /// </summary>
    public static unsafe Task<T> RunOnGraphicsThread<T>(Func<T> func)
    {
        GraphicsCommands ??= new() { name = "KSPTextureLoader Tasks" };

        var tcs = new GraphicsTaskCompletionSource<T>(func);
        var data = (ObjectHandle<IGraphicsWorkItem>*)
            UnsafeUtility.Malloc(
                sizeof(ObjectHandle<IGraphicsWorkItem>),
                UnsafeUtility.AlignOf<ObjectHandle<IGraphicsWorkItem>>(),
                Allocator.TempJob
            );

        *data = new(tcs);

        try
        {
            GraphicsCommands.Clear();
            GraphicsCommands.IssuePluginEventAndData(ExecuteTaskPtr, 0, (IntPtr)data);
            Graphics.ExecuteCommandBuffer(GraphicsCommands);
        }
        catch
        {
            data->Dispose();
            UnsafeUtility.Free(data, Allocator.TempJob);
            throw;
        }

        return tcs.Task;
    }

    /// <summary>
    /// Runs a function on the graphics thread and returns a task that completes
    /// when it finishes.
    /// </summary>
    public static Task RunOnGraphicsThread(Action func) =>
        RunOnGraphicsThread(() =>
        {
            func();
            return default(Empty);
        });

    static CommandBuffer GraphicsCommands;
    static readonly unsafe IntPtr ExecuteTaskPtr = Marshal.GetFunctionPointerForDelegate(
        ExecuteTask
    );

    interface IGraphicsWorkItem
    {
        void Execute();
    }

    sealed class GraphicsTaskCompletionSource<T>(Func<T> func)
        : TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously),
            IGraphicsWorkItem
    {
        readonly Func<T> func = func;

        public void Execute()
        {
            try
            {
                TrySetResult(func());
            }
            catch (Exception e)
            {
                TrySetException(e);
            }
        }
    }

    static unsafe void ExecuteTask(uint eventID, ObjectHandle<IGraphicsWorkItem>* data)
    {
        using var handle = *data;
        UnsafeUtility.Free(data, Allocator.TempJob);
        handle.Target.Execute();
    }
    #endregion
}
