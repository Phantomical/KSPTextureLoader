using System.Threading.Tasks;
using Unity.Jobs;

namespace KSPTextureLoader.Utils;

internal static class AsyncUtil
{
    struct Empty;

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
}
