using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Unity.Burst;
using UnityEngine;

namespace KSPTextureLoader.Utils;

internal static class BurstUtil
{
    /// <summary>
    /// True when the enclosing method is executing as Burst-compiled native code, false when it is
    /// running as plain managed IL. Burst strips the <c>Check</c> call (it is <see
    /// cref="BurstDiscardAttribute"/>), so only the managed path clears the flag.
    /// </summary>
    public static bool IsBurstCompiled
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            [BurstDiscard]
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void Check(ref bool result) => result = false;

            bool compiled = true;
            Check(ref compiled);
            return compiled;
        }
    }

    static volatile bool jobsAllowed;

    /// <summary>
    /// Whether it is safe to schedule Unity jobs or compile Burst function pointers.
    /// </summary>
    ///
    /// <remarks>
    /// Launching jobs before KSPBurst has had a chance to run the compiler will
    /// result in either a stall while it forces the compiler to complete
    /// immediately or burst being permanently broken. To avoid this we run any
    /// loading jobs through the managed threadpool.
    /// 
    /// This gets set to true in PSystemSpawn.
    /// </remarks>
    public static bool JobsAllowed
    {
        get => jobsAllowed;
        internal set => jobsAllowed = value;
    }

    /// <summary>
    /// Run a batched parallel-for body without the job system, splitting it
    /// across the .NET threadpool. Used as the fallback while
    /// <see cref="JobsAllowed"/> is false.
    /// </summary>
    internal static void RunParallelBatches(int length, int batchSize, Action<int, int> body)
    {
        if (length <= 0)
            return;
        if (batchSize <= 0)
            batchSize = 1;

        int batches = (length + batchSize - 1) / batchSize;
        if (batches == 1)
        {
            body(0, length);
            return;
        }

        Parallel.For(
            0,
            batches,
            batch =>
            {
                int start = batch * batchSize;
                body(start, Math.Min(batchSize, length - start));
            }
        );
    }
}

/// <summary>
/// Sets <see cref="BurstUtil.JobsAllowed"/> when PSystemSpawn loads.
/// </summary>
[KSPAddon(KSPAddon.Startup.PSystemSpawn, once: true)]
internal class BurstJobsEnabler : MonoBehaviour
{
    void Awake()
    {
        BurstUtil.JobsAllowed = true;
        Destroy(gameObject);
    }
}
