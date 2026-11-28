using KSPTextureLoader.Utils;
#if KSP_RETOOLED
using Unity.Jobs;
#endif

namespace KSPTextureLoader.Burst;

internal static class JobExtensions
{
    internal static void ExecuteManaged<T>(this T job, int arrayLength, int minIndicesPerJob)
        where T : struct, IJobParallelForBatch =>
        BurstUtil.RunParallelBatches(
            arrayLength,
            minIndicesPerJob,
            (start, count) => job.Execute(start, count)
        );
}
