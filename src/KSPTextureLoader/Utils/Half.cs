using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KSPTextureLoader.Utils;

internal readonly struct Half(ushort value)
{
    readonly ushort value = value;

    public static implicit operator float(Half x)
    {
        ushort h = x.value;
        int sign = (h >> 15) & 1;
        int exp = (h >> 10) & 0x1F;
        int mantissa = h & 0x3FF;

        float signf = sign == 1 ? -1f : 1f;

        switch (exp)
        {
            case 0:
                if (mantissa == 0)
                    return 0f;

                const float zeroExpMult = (1f / 1024f) * (1f / 16384f);
                return mantissa * zeroExpMult * signf;

            case 31:
                if (mantissa == 0)
                    return float.PositiveInfinity * signf;
                return float.NaN;

            default:
                int bits = (sign << 31) | ((exp + 112) << 23) | (mantissa << 13);
                return BitCast(bits);
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct IntFloat
    {
        [FieldOffset(0)]
        public int Int;

        [FieldOffset(0)]
        public float Float;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float BitCast(int v) => new IntFloat { Int = v }.Float;
}
