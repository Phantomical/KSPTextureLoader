using System.Runtime.CompilerServices;
using Unity.Burst;

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
}
