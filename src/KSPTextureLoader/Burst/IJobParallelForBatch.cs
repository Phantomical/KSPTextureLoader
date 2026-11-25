using System;
using System.Runtime.InteropServices;
using KSPTextureLoader.Utils;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;

namespace KSPTextureLoader.Burst;

[JobProducerType(typeof(IJobParallelForBatchExtensions.JobStruct<>))]
internal interface IJobParallelForBatch
{
    void Execute(int start, int count);
}

internal static class IJobParallelForBatchExtensions
{
    [StructLayout(LayoutKind.Sequential, Size = 1)]
    internal struct JobStruct<T>
        where T : struct, IJobParallelForBatch
    {
        delegate void ExecuteJobFunction(
            ref T data,
            IntPtr additionalPtr,
            IntPtr bufferRangePatchData,
            ref JobRanges ranges,
            int jobIndex
        );

        internal static readonly IntPtr jobReflectionData;

        // An explicit static constructor removes beforefieldinit, so the runtime
        // must not create the reflection data until jobReflectionData is first
        // read. Creating it before PSystemSpawn would trip KSPBurst (see
        // BurstUtil.JobsAllowed).
        static JobStruct()
        {
            jobReflectionData = JobsUtility.CreateJobReflectionData(
                typeof(T),
#if !UNITY_6_7
                JobType.ParallelFor,
#endif
                new ExecuteJobFunction(Execute)
            );
        }

        public static void Execute(
            ref T jobData,
            IntPtr additionalPtr,
            IntPtr bufferRangePatchData,
            ref JobRanges ranges,
            int jobIndex
        )
        {
            while (
                JobsUtility.GetWorkStealingRange(ref ranges, jobIndex, out int begin, out int end)
            )
            {
                jobData.Execute(begin, end - begin);
            }
        }
    }

    public static unsafe JobHandle ScheduleBatch<T>(
        this T job,
        int arrayLength,
        int minIndicesPerJob,
        JobHandle dependsOn = default
    )
        where T : struct, IJobParallelForBatch
    {
        // Touching JobStruct<T> creates job reflection data, which KSPBurst
        // blocks on (or breaks on) until its compile is done. Like scheduling,
        // this path must be called on the main thread.
        if (!BurstUtil.JobsAllowed)
        {
            dependsOn.Complete();
            job.ExecuteManaged(arrayLength, minIndicesPerJob);
            return default;
        }

        var parameters = new JobsUtility.JobScheduleParameters(
            UnsafeUtility.AddressOf(ref job),
            JobStruct<T>.jobReflectionData,
            dependsOn,
#if UNITY_6_7
            ScheduleMode.Parallel
#else
            ScheduleMode.Batched
#endif
        );

        return JobsUtility.ScheduleParallelFor(ref parameters, arrayLength, minIndicesPerJob);
    }

    public static unsafe JobHandle RunBatch<T>(
        this T job,
        int arrayLength,
        int minIndicesPerJob,
        JobHandle dependsOn = default
    )
        where T : struct, IJobParallelForBatch
    {
        var parameters = new JobsUtility.JobScheduleParameters(
            UnsafeUtility.AddressOf(ref job),
            JobStruct<T>.jobReflectionData,
            dependsOn,
            ScheduleMode.Run
        );

        return JobsUtility.ScheduleParallelFor(ref parameters, arrayLength, minIndicesPerJob);
    }

    internal static void ExecuteManaged<T>(this T job, int arrayLength, int minIndicesPerJob)
        where T : struct, IJobParallelForBatch =>
        BurstUtil.RunParallelBatches(
            arrayLength,
            minIndicesPerJob,
            (start, count) => job.Execute(start, count)
        );
}
