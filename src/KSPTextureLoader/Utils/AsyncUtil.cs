using System;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Contexts;
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

    #region LaunchMainThreadTask

    public static Task LaunchMainThreadTask(Func<Task> func)
    {
        return LaunchMainThreadTask<Empty>(async () =>
        {
            await func();
            return default;
        });
    }

    public static Task<T> LaunchMainThreadTask<T>(Func<Task<T>> func)
    {
        var tcs = new TaskCompletionSource<T>();

        TextureLoader.Context.Submit(
            state =>
            {
                try
                {
                    var func = (Func<Task<T>>)state;
                    var task = func();

                    task.ContinueWith(task =>
                    {
                        try
                        {
                            tcs.SetResult(task.Result);
                        }
                        catch (Exception e)
                        {
                            tcs.TrySetException(e);
                        }
                    });
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

    public static Task LaunchMainThreadTask(Action func)
    {
        return LaunchMainThreadTask(() =>
        {
            func();
            return default(Empty);
        });
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
        if (handle.IsCompleted)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource<Empty>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var job = new NotifyJob(tcs);
        job.Schedule(handle);
        JobHandle.ScheduleBatchedJobs();

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
