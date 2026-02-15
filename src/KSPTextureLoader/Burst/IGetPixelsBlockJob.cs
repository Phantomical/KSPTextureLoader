using System;
using KSPTextureLoader.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

namespace KSPTextureLoader.Burst;

[JobProducerType(typeof(IGetPixelsBlockJobExtensions.JobStruct<>))]
interface IGetPixelsBlockJobInner
{
    FixedArray16<Color> DecodeBlock(int blockIdx);
}

[JobProducerType(typeof(IGetPixelsBlockJobExtensions.JobStruct32<>))]
interface IGetPixelsBlockJob : IGetPixelsBlockJobInner { }

internal static class IGetPixelsBlockJobExtensions
{
    internal struct JobStruct<T>
        where T : struct, IGetPixelsBlockJob
    {
        public T data;

        [WriteOnly]
        [NativeDisableParallelForRestriction]
        public NativeArray<Color> pixels;

        public int blocksPerRow;
        public int width;
        public int height;

        void DoExecute(ref JobRanges ranges, int jobIndex)
        {
            while (
                JobsUtility.GetWorkStealingRange(ref ranges, jobIndex, out var begin, out var end)
            )
            {
                for (int blockIndex = begin; blockIndex < end; ++blockIndex)
                {
                    var block = data.DecodeBlock(blockIndex);

                    int blockX = blockIndex % blocksPerRow;
                    int blockY = blockIndex / blocksPerRow;
                    int baseX = blockX * 4;
                    int baseY = blockY * 4;

                    for (int row = 0; row < 4; row++)
                    {
                        int py = baseY + row;
                        if (py >= height)
                            break;

                        for (int col = 0; col < 4; col++)
                        {
                            int px = baseX + col;
                            if (px >= width)
                                break;

                            pixels[py * width + px] = block[row * 4 + col];
                        }
                    }
                }
            }
        }

        internal static void Execute(
            ref JobStruct<T> jobData,
            IntPtr additionalPtr,
            IntPtr bufferRangePatchData,
            ref JobRanges ranges,
            int jobIndex
        )
        {
            jobData.DoExecute(ref ranges, jobIndex);
        }

        delegate void ExecuteJobFunction(
            ref JobStruct<T> data,
            IntPtr additionalPtr,
            IntPtr bufferRangePatchData,
            ref JobRanges ranges,
            int jobIndex
        );

        internal static readonly IntPtr JobReflectionData = JobsUtility.CreateJobReflectionData(
            typeof(JobStruct<T>),
            typeof(T),
            JobType.ParallelFor,
            new ExecuteJobFunction(Execute)
        );
    }

    internal struct JobStruct32<T>
        where T : struct, IGetPixelsBlockJob
    {
        public T data;

        [WriteOnly]
        [NativeDisableParallelForRestriction]
        public NativeArray<Color32> pixels;

        public int blocksPerRow;
        public int width;
        public int height;

        void DoExecute(ref JobRanges ranges, int jobIndex)
        {
            while (
                JobsUtility.GetWorkStealingRange(ref ranges, jobIndex, out var begin, out var end)
            )
            {
                for (int blockIndex = begin; blockIndex < end; ++blockIndex)
                {
                    var block = data.DecodeBlock(blockIndex);

                    int blockX = blockIndex % blocksPerRow;
                    int blockY = blockIndex / blocksPerRow;
                    int baseX = blockX * 4;
                    int baseY = blockY * 4;

                    for (int row = 0; row < 4; row++)
                    {
                        int py = baseY + row;
                        if (py >= height)
                            break;

                        for (int col = 0; col < 4; col++)
                        {
                            int px = baseX + col;
                            if (px >= width)
                                break;

                            pixels[py * width + px] = block[row * 4 + col];
                        }
                    }
                }
            }
        }

        internal static void Execute(
            ref JobStruct32<T> jobData,
            IntPtr additionalPtr,
            IntPtr bufferRangePatchData,
            ref JobRanges ranges,
            int jobIndex
        )
        {
            jobData.DoExecute(ref ranges, jobIndex);
        }

        delegate void ExecuteJobFunction(
            ref JobStruct32<T> data,
            IntPtr additionalPtr,
            IntPtr bufferRangePatchData,
            ref JobRanges ranges,
            int jobIndex
        );

        internal static readonly IntPtr JobReflectionData = JobsUtility.CreateJobReflectionData(
            typeof(JobStruct32<T>),
            typeof(T),
            JobType.ParallelFor,
            new ExecuteJobFunction(Execute)
        );
    }

    public static unsafe JobHandle Schedule<T>(
        this T job,
        int blocksPerRow,
        int width,
        int height,
        NativeArray<Color> pixels
    )
        where T : struct, IGetPixelsBlockJob
    {
        var wrap = new JobStruct<T>
        {
            data = job,
            blocksPerRow = blocksPerRow,
            width = width,
            height = height,
            pixels = pixels,
        };
        var parameters = new JobsUtility.JobScheduleParameters(
            UnsafeUtility.AddressOf(ref wrap),
            JobStruct<T>.JobReflectionData,
            default,
            ScheduleMode.Batched
        );

        return JobsUtility.ScheduleParallelFor(ref parameters, pixels.Length, 256);
    }

    public static unsafe JobHandle Schedule<T>(
        this T job,
        int blocksPerRow,
        int width,
        int height,
        NativeArray<Color32> pixels
    )
        where T : struct, IGetPixelsBlockJob
    {
        var wrap = new JobStruct32<T>
        {
            data = job,
            blocksPerRow = blocksPerRow,
            width = width,
            height = height,
            pixels = pixels,
        };
        var parameters = new JobsUtility.JobScheduleParameters(
            UnsafeUtility.AddressOf(ref wrap),
            JobStruct32<T>.JobReflectionData,
            default,
            ScheduleMode.Batched
        );

        return JobsUtility.ScheduleParallelFor(ref parameters, pixels.Length, 256);
    }
}
