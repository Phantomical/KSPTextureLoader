using System;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;

namespace KSPTextureLoader.Burst;

internal interface IJobParallelForBatch
{
    void Execute(int start, int count);
}

internal static class IJobParallelForBatchExtensions
{
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

        internal static readonly IntPtr jobReflectionData = JobsUtility.CreateJobReflectionData(
            typeof(T),
            JobType.ParallelFor,
            new ExecuteJobFunction(Execute)
        );

        public static void Execute(
            ref T jobData,
            IntPtr additionalPtr,
            IntPtr bufferRangePatchData,
            ref JobRanges ranges,
            int jobIndex
        )
        {
            int beginIndex;
            int endIndex;
            while (
                JobsUtility.GetWorkStealingRange(ref ranges, jobIndex, out beginIndex, out endIndex)
            )
            {
                jobData.Execute(beginIndex, endIndex - beginIndex);
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
        var parameters = new JobsUtility.JobScheduleParameters(
            UnsafeUtility.AddressOf(ref job),
            JobStruct<T>.jobReflectionData,
            dependsOn,
            ScheduleMode.Batched
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
}
