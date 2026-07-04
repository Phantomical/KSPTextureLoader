using System.IO;

namespace KSPTextureLoader.Format.Bundle;

/// <summary>
/// Decompressor for the LZ4 block format used by UnityFS bundle blocks
/// (compression types 2 = LZ4 and 3 = LZ4HC; both decode identically).
/// </summary>
internal static class Lz4
{
    /// <summary>
    /// Decompress a single LZ4 block.
    /// </summary>
    /// <param name="src">Pointer to the compressed input.</param>
    /// <param name="srcLength">Length of the compressed input in bytes.</param>
    /// <param name="dst">Pointer to the output buffer.</param>
    /// <param name="dstLength">Size of the output buffer in bytes.</param>
    /// <returns>The number of bytes written to <paramref name="dst"/>.</returns>
    public static unsafe int Decompress(byte* src, int srcLength, byte* dst, int dstLength)
    {
        byte* sEnd = src + srcLength;
        byte* dEnd = dst + dstLength;
        byte* s = src;
        byte* d = dst;

        while (s < sEnd)
        {
            int token = *s++;

            // Literal run.
            int literalLength = token >> 4;
            if (literalLength == 0xF)
            {
                byte b;
                do
                {
                    if (s >= sEnd)
                        throw new InvalidDataException("LZ4: truncated literal length");
                    b = *s++;
                    literalLength += b;
                } while (b == 0xFF);
            }

            if (literalLength > sEnd - s)
                throw new InvalidDataException("LZ4: literal run exceeds input");
            if (literalLength > dEnd - d)
                throw new InvalidDataException("LZ4: literal run exceeds output");

            for (int i = 0; i < literalLength; ++i)
                d[i] = s[i];
            s += literalLength;
            d += literalLength;

            // The last sequence in a block contains only literals.
            if (s >= sEnd)
                break;

            // Match copy. The offset is a little-endian 16-bit value.
            if (sEnd - s < 2)
                throw new InvalidDataException("LZ4: truncated match offset");
            int offset = s[0] | (s[1] << 8);
            s += 2;

            if (offset == 0)
                throw new InvalidDataException("LZ4: invalid zero match offset");

            int matchLength = token & 0xF;
            if (matchLength == 0xF)
            {
                byte b;
                do
                {
                    if (s >= sEnd)
                        throw new InvalidDataException("LZ4: truncated match length");
                    b = *s++;
                    matchLength += b;
                } while (b == 0xFF);
            }
            matchLength += 4; // minimum match length

            byte* match = d - offset;
            if (match < dst)
                throw new InvalidDataException("LZ4: match offset points before output");
            if (matchLength > dEnd - d)
                throw new InvalidDataException("LZ4: match run exceeds output");

            // The match may overlap the output cursor, so this must be copied
            // one byte at a time rather than with a bulk memcpy.
            for (int i = 0; i < matchLength; ++i)
                *d++ = *match++;
        }

        return (int)(d - dst);
    }
}
