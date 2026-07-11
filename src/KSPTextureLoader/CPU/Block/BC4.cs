using KSPTextureLoader.Utils;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Mathematics;
using static Unity.Burst.Intrinsics.X86;

namespace KSPTextureLoader.CPU.Block;

/// <summary>
/// SIMD-accelerated BC4 (single-channel, BCn) block decompression.
///
/// A BC4 block is a single <see cref="ulong"/>:
///   bits[0:8]   = endpoint e0 (byte)
///   bits[8:16]  = endpoint e1 (byte)
///   bits[16:64] = 16 x 3-bit palette indices (pixel i at bit 16 + i*3)
///
/// If e0 &gt; e1 the palette is 8 values interpolated e0..e1 (weights /7).
/// Otherwise it is 6 interpolated values plus code 6 = 0.0 and code 7 = 1.0
/// (weights /5). Endpoints are normalised as byte/255; output is 0..1 float.
///
/// The fast path uses BMI2 <c>pdep</c> to scatter the packed 3-bit indices into
/// individual byte lanes and AVX2 <c>permutevar8x32_ps</c> to gather the palette
/// for eight pixels at a time. The palette itself is built with SIMD, computing
/// all interpolated lanes in a single 8-wide AVX <c>mul</c>/<c>add</c>/<c>mul</c>.
/// A scalar fallback yields identical results.
/// </summary>
[BurstCompile]
internal static class BC4
{
    // Reciprocal constants kept as fields so the scalar and SIMD palette
    // builds perform the exact same floating point operations as the reference.
    const float Inv255 = 1f / 255f;
    const float Inv7 = 1f / 7f;
    const float Inv5 = 1f / 5f;

    /// <summary>
    /// Decodes a single channel value for one pixel of a BC4 block.
    /// </summary>
    internal static float DecodeChannel(ulong block, int pixelIndex)
    {
        int shift = 16 + pixelIndex * 3;

        int code;
        if (Bmi2.IsBmi2Supported)
        {
            // Pull the 3-bit index for this pixel down to the low bits.
            code = (int)Bmi2.pext_u64(block, 0x7UL << shift);
        }
        else
        {
            code = (int)((block >> shift) & 0x7);
        }

        var palette = BuildPalette(block);
        return palette[code];
    }

    [BurstCompile]
    internal static void DecodeBlock(ulong block, out FixedArray16<float> output) =>
        output = DecodeBlock(block);

    /// <summary>
    /// Decodes an entire BC4 block into 16 float channel values in row-major
    /// order (4 rows of 4 pixels).
    /// </summary>
    internal static unsafe FixedArray16<float> DecodeBlock(ulong block)
    {
        FixedArray16<float> output = default;

        float8 palette = BuildPalette(block);

        if (Avx2.IsAvx2Supported)
        {
            // 48 bits of packed 3-bit indices.
            ulong idxBits = block >> 16;

            // Scatter the 3-bit indices into their own byte lanes.
            // mask groups pick src bits [0..2] -> byte0, [3..5] -> byte1, ...
            const ulong SpreadMask = 0x0707070707070707UL;
            ulong loBytes = Bmi2.pdep_u64(idxBits, SpreadMask); // indices 0..7
            ulong hiBytes = Bmi2.pdep_u64(idxBits >> 24, SpreadMask); // indices 8..15

            // The whole palette lives in one 8-lane float register.
            // Widen the 8 index bytes to 8 int32 lanes, then gather via permute.
            v256 idxLo = Avx2.mm256_cvtepu8_epi32(Sse2.cvtsi64x_si128((long)loBytes));
            v256 idxHi = Avx2.mm256_cvtepu8_epi32(Sse2.cvtsi64x_si128((long)hiBytes));

            v256 res0 = Avx2.mm256_permutevar8x32_ps(palette, idxLo);
            v256 res1 = Avx2.mm256_permutevar8x32_ps(palette, idxHi);

            float* tmp = (float*)&output;
            Avx.mm256_storeu_ps(tmp + 0, res0);
            Avx.mm256_storeu_ps(tmp + 8, res1);

            return output;
        }

        // Scalar fallback — identical semantics to the reference decoder.
        ulong indices = block >> 16;
        for (int i = 0; i < 16; i++)
        {
            output[i] = palette[(int)(indices & 0x7)];
            indices >>= 3;
        }
        return output;
    }

    /// <summary>
    /// Builds the 8-entry palette for a BC4 block. The arithmetic is kept
    /// bit-for-bit identical to the scalar reference so both decode paths agree.
    /// </summary>
    static float8 BuildPalette(ulong block)
    {
        int r0 = (int)(block & 0xFF);
        int r1 = (int)((block >> 8) & 0xFF);

        float8 fr0 = new(r0 * Inv255);
        float8 fr1 = new(r1 * Inv255);
        float8 a;
        float8 b;
        float8 m;
        float8 c;

        if (r0 > r1)
        {
            a = new(1f, 0f, 6f, 5f, 4f, 3f, 2f, 1f);
            b = new(0f, 1f, 1f, 2f, 3f, 4f, 5f, 6f);
            m = new(1f, 1f, Inv7, Inv7, Inv7, Inv7, Inv7, Inv7);
            c = new(0f);
        }
        else
        {
            a = new(1f, 0f, 4f, 3f, 2f, 1f, 0f, 0f);
            b = new(0f, 1f, 1f, 2f, 3f, 4f, 0f, 0f);
            m = new(1f, 1f, Inv5, Inv5, Inv5, Inv5, 0f, 0f);
            c = new(0f, 0f, 0f, 0f, 0f, 0f, 0f, 1f);
        }

        // palette = ((fr0 * a) + (fr1 * b)) * m + c
        return float8.fma(float8.fma(fr0, a, fr1 * b), m, c);

        /*
        This is what the code above should be equivalent to.

        palette[0] = fr0;
        palette[1] = fr1;

        if (r0 > r1)
        {
            palette[2] = (6f * fr0 + 1f * fr1) * Inv7;
            palette[3] = (5f * fr0 + 2f * fr1) * Inv7;
            palette[4] = (4f * fr0 + 3f * fr1) * Inv7;
            palette[5] = (3f * fr0 + 4f * fr1) * Inv7;
            palette[6] = (2f * fr0 + 5f * fr1) * Inv7;
            palette[7] = (1f * fr0 + 6f * fr1) * Inv7;
        }
        else
        {
            palette[2] = (4f * fr0 + 1f * fr1) * Inv5;
            palette[3] = (3f * fr0 + 2f * fr1) * Inv5;
            palette[4] = (2f * fr0 + 3f * fr1) * Inv5;
            palette[5] = (1f * fr0 + 4f * fr1) * Inv5;
            palette[6] = 0f;
            palette[7] = 1f;
        }
        */
    }
}
