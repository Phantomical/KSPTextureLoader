using System;
using System.Runtime.InteropServices;
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

    abstract class MainThreadTask<T>
    {
        public readonly TaskCompletionSource<T> tcs = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task<T> Task => tcs.Task;

        protected abstract void Execute();

        public static void Callback(object state)
        {
            var task = (MainThreadTask<T>)state;

            try
            {
                task.Execute();
            }
            catch (Exception e)
            {
                task.tcs.TrySetException(e);
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
            func().ContinueWith(Continue, this, TaskScheduler.FromCurrentSynchronizationContext());
        }

        static void Continue(Task task, object state)
        {
            var ft = (FuncTaskTask)state;

            try
            {
                task.GetAwaiter().GetResult();
                ft.tcs.SetResult(default);
            }
            catch (Exception e)
            {
                ft.tcs.TrySetException(e);
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
            func().ContinueWith(Continue, this, TaskScheduler.FromCurrentSynchronizationContext());
        }

        static void Continue(Task<T> task, object state)
        {
            var ft = (FuncTaskTTask<T>)state;

            try
            {
                ft.tcs.SetResult(task.Result);
            }
            catch (Exception e)
            {
                ft.tcs.TrySetException(e);
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
            tcs.TrySetResult(default);
        }
    }

    public static Task LaunchMainThreadTask(Action func)
    {
        var task = new ActionTask(func);
        task.Submit();
        return task.Task;
    }

    public static Task<T> LaunchMainThreadTask<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();

        TextureLoader.Context.Submit(
            state =>
            {
                try
                {
                    var func = (Func<T>)state;
                    tcs.SetResult(func());
                }
                catch (Exception e)
                {
                    tcs.TrySetException(e);
                }
            },
            func
        );

        return tcs.Task;
    }
    #endregion

    #region WaitForJob
    struct NotifyJob(TaskCompletionSource<Empty> tcs) : IJob
    {
        ObjectHandle<TaskCompletionSource<Empty>> tcs = new(tcs);

        public void Execute()
        {
            using (tcs)
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
