using Steamworks;

namespace KSPTextureLoader.Utils;

internal static class MathUtil
{
    internal static int CountTrailingZeros(byte x)
    {
        if (x == 0)
            return 8;

        int n = 0;
        // csharpier-ignore-start
        if ((x & 0x0F) == 0) { n += 4; x >>= 4; }
        if ((x & 0x03) == 0) { n += 2; x >>= 2; }
        if ((x & 0x01) == 0) { n += 1; }
        // csharpier-ignore-end

        return n;
    }

    // csharpier-ignore-start
    static readonly int[] MultiplyDeBruijnBitPosition =
    [
        0, 9, 1, 10, 13, 21, 2, 29, 11, 14, 16, 18, 22, 25, 3, 30,
        8, 12, 20, 28, 15, 17, 24, 7, 19, 27, 23, 6, 26, 5, 4, 31
    ];
    // csharpier-ignore-end

    internal static int FloorLog2(uint x)
    {
        // Round down to the next lowest power of 2
        x |= x >> 1;
        x |= x >> 2;
        x |= x >> 4;
        x |= x >> 8;
        x |= x >> 16;

        return MultiplyDeBruijnBitPosition[(int)(x * 0x07C4ACDDU) >> 27];
    }
}
