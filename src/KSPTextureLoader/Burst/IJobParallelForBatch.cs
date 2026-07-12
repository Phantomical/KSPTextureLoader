using System;
using System.Runtime.InteropServices;
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
