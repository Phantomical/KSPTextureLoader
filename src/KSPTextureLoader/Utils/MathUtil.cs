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
}
