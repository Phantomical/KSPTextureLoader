using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections;

namespace KSPTextureLoader.CPU;

internal static class CPUTextureHelper
{
    internal const float Byte2Float = 1f / 255f;
    internal const float UShort2Float = 1f / 65535f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Clamp(int x, int min, int max)
    {
        if (x < min)
            return min;
        if (x > max)
            return max;
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PixelIndex(int x, int y, int w, int h)
    {
        return Clamp(y, 0, h - 1) * w + Clamp(x, 0, w - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MipWidth(int baseWidth, int mipLevel) => Math.Max(1, baseWidth >> mipLevel);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MipHeight(int baseHeight, int mipLevel) =>
        Math.Max(1, baseHeight >> mipLevel);

    internal static int UncompressedMipOffset(
        int width,
        int height,
        int mipLevel,
        int bytesPerPixel
    )
    {
        int offset = 0;
        for (int i = 0; i < mipLevel; i++)
            offset += MipWidth(width, i) * MipHeight(height, i) * bytesPerPixel;
        return offset;
    }

    internal static int BlockCompressedMipOffset(
        int width,
        int height,
        int mipLevel,
        int blockSizeBytes
    )
    {
        int offset = 0;
        for (int i = 0; i < mipLevel; i++)
        {
            int mw = MipWidth(width, i);
            int mh = MipHeight(height, i);
            int blocksX = Math.Max(1, (mw + 3) / 4);
            int blocksY = Math.Max(1, (mh + 3) / 4);
            offset += blocksX * blocksY * blockSizeBytes;
        }
        return offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort ReadUInt16(NativeArray<byte> data, int offset)
    {
        return (ushort)(data[offset] | (data[offset + 1] << 8));
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
    internal static float ReadSingle(NativeArray<byte> data, int offset)
    {
        int bits =
            data[offset]
            | (data[offset + 1] << 8)
            | (data[offset + 2] << 16)
            | (data[offset + 3] << 24);
        return new IntFloat { Int = bits }.Float;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float ReadHalf(NativeArray<byte> data, int offset)
    {
        return HalfToFloat((ushort)(data[offset] | (data[offset + 1] << 8)));
    }

    internal static float HalfToFloat(ushort h)
    {
        int sign = (h >> 15) & 1;
        int exp = (h >> 10) & 0x1F;
        int mantissa = h & 0x3FF;

        if (exp == 0)
        {
            if (mantissa == 0)
                return 0f;
            float f = mantissa * (1f / 1024f) * (1f / 16384f);
            return sign == 1 ? -f : f;
        }

        if (exp == 31)
        {
            return mantissa == 0
                ? (sign == 1 ? float.NegativeInfinity : float.PositiveInfinity)
                : float.NaN;
        }

        int floatBits = (sign << 31) | ((exp + 112) << 23) | (mantissa << 13);
        return new IntFloat { Int = floatBits }.Float;
    }
}
