using KSPTextureLoader.Utils;
using Unity.Burst.Intrinsics;
using UnityEngine;
using static Unity.Burst.Intrinsics.X86;

namespace KSPTextureLoader.CPU.Block;

/// <summary>
/// SIMD-accelerated DXT1 (BC1) block decompression.
///
/// A DXT1 block is a single 64-bit little-endian value laid out like this
/// | Bit Range | Width | Field
/// |  0 .. 15  |  16   | c0 (RGB565)
/// | 16 .. 31  |  16   | c1 (RGB565)
/// | 32 .. 63  |  32   | 16 x 2-bit palette indices (texel i at bit 32 + i*2, row-major)
///
/// DXT1 decoding works like this:
///   - The two endpoints c0 and c1 are RGB565, each storing b(5)/g(6)/r(5) from its low
///     bit. Comparing them as 16-bit values selects the palette mode: c0 &gt; c1 is 4-color,
///     otherwise 3-color.
///   - Palette entries 0 and 1 are the endpoints themselves. In 4-color mode entries 2 and 3
///     are the 2/3 and 1/3 lerps of the endpoints and every texel is opaque. In 3-color mode
///     entry 2 is the endpoint midpoint and entry 3 is transparent black (rgba = 0).
///   - Each texel's 2-bit index selects one of the four palette entries.
/// </summary>
internal static class DXT1
{
    /// <summary>
    /// Decodes a single pixel (0..15, row-major) of a DXT1 block.
    /// </summary>
    internal static unsafe Color DecodePixel(ulong block, int pixelIndex)
    {
        Color* palette = stackalloc Color[4];
        BuildPalette(block, palette);

        // Pixel i's 2-bit index lives at bit 32 + i*2 (row-major, 8 bits per row).
        int index = (int)((block >> (32 + pixelIndex * 2)) & 0x3);
        return palette[index];
    }

    /// <summary>
    /// Decodes a single pixel (0..15, row-major) of a DXT1 block as a <see cref="Color32"/>.
    /// </summary>
    internal static Color32 DecodePixel32(ulong block, int pixelIndex) =>
        DecodePixel(block, pixelIndex);

    /// <summary>
    /// Decodes an entire DXT1 block into 16 Color values in row-major order
    /// (4 rows of 4 pixels).
    /// </summary>
    internal static unsafe FixedArray16<Color> DecodeBlock(ulong block)
    {
        FixedArray16<Color> output = default;

        Color* palette = stackalloc Color[4];
        BuildPalette(block, palette);

        uint idxBits = (uint)(block >> 32);

        if (Bmi2.IsBmi2Supported)
        {
            // Scatter the 16 packed 2-bit indices into 16 byte lanes: each byte's low
            // two bits receive one index (mask 0x03 per byte deposits 2 bits per byte).
            const ulong SpreadMask = 0x0303030303030303UL;
            ulong lo = Bmi2.pdep_u64(idxBits & 0xFFFFUL, SpreadMask); // pixels 0..7
            ulong hi = Bmi2.pdep_u64((idxBits >> 16) & 0xFFFFUL, SpreadMask); // pixels 8..15

            for (int i = 0; i < 8; i++)
                output[i] = palette[(int)((lo >> (i * 8)) & 0xFF)];
            for (int i = 0; i < 8; i++)
                output[8 + i] = palette[(int)((hi >> (i * 8)) & 0xFF)];
        }
        else
        {
            uint indices = idxBits;
            for (int i = 0; i < 16; i++)
            {
                output[i] = palette[(int)(indices & 0x3)];
                indices >>= 2;
            }
        }

        return output;
    }

    /// <summary>
    /// Decodes an entire DXT1 block into 16 <see cref="Color32"/> values in row-major
    /// order (4 rows of 4 pixels).
    /// </summary>
    internal static unsafe FixedArray16<Color32> DecodeBlock32(ulong block)
    {
        FixedArray16<Color32> output = default;

        uint idxBits = (uint)(block >> 32);

        if (Avx2.IsAvx2Supported)
        {
            // Build the four palette entries straight to packed Color32. A Color32 is 32
            // bits, so the whole 4-entry palette fits in the low four 32-bit lanes of a
            // v256. Each pixel's 2-bit index (0..3) selects one lane, so a single
            // mm256_permutevar8x32_epi32 gathers eight pixels at once — BC4's vpermps trick,
            // but selecting whole Color32 entries instead of float channels.
            v256 pal = Avx.mm256_castsi128_si256(BuildPalette32(block));

            // Scatter the packed 2-bit indices into one per byte (BMI2 is implied by AVX2),
            // then widen to int32 lanes to use as the permute selector.
            const ulong SpreadMask = 0x0303030303030303UL;
            ulong loBytes = Bmi2.pdep_u64(idxBits & 0xFFFFUL, SpreadMask); // pixels 0..7
            ulong hiBytes = Bmi2.pdep_u64((idxBits >> 16) & 0xFFFFUL, SpreadMask); // pixels 8..15

            v256 idxLo = Avx2.mm256_cvtepu8_epi32(Sse2.cvtsi64x_si128((long)loBytes));
            v256 idxHi = Avx2.mm256_cvtepu8_epi32(Sse2.cvtsi64x_si128((long)hiBytes));

            Color32* dst = (Color32*)&output;
            Avx.mm256_storeu_si256(dst + 0, Avx2.mm256_permutevar8x32_epi32(pal, idxLo));
            Avx.mm256_storeu_si256(dst + 8, Avx2.mm256_permutevar8x32_epi32(pal, idxHi));
        }
        else
        {
            // Fallback: decode the float palette and convert each selected entry with
            // Unity's implicit Color -> Color32.
            Color* palette = stackalloc Color[4];
            BuildPalette(block, palette);

            uint indices = idxBits;
            for (int i = 0; i < 16; i++)
            {
                output[i] = palette[(int)(indices & 0x3)];
                indices >>= 2;
            }
        }

        return output;
    }

    /// <summary>
    /// Builds the block's 4-entry palette as packed <see cref="Color32"/> (16 bytes, entries
    /// 0..3 low to high) for the Color32 path. Decodes the [0,1] float palette with
    /// <see cref="BuildPalette"/> (Burst inlines it and promotes the stackalloc round-trip to
    /// registers), then scales to [0,255], rounds, and narrows to bytes. Every value is in [0,1]
    /// by construction — endpoints are 5/6/5 fractions and the blended entries are convex
    /// combinations — so Unity's Clamp01 is a no-op and the conversion is just
    /// round-to-nearest-even of x*255. Applying the ×255 after the decode/blend (rather than
    /// folding it into the 5/6/5 scale) keeps the rounding bit-exact to Unity's
    /// <c>Color -&gt; Color32</c>; folding it in would round some 3-color midpoints off by 1/255.
    /// </summary>
    private static unsafe v128 BuildPalette32(ulong block)
    {
        // Burst requires the IsSupported guard in the same method as the intrinsics; the only
        // caller already gates on AVX2, so the fallback is never reached at runtime.
        if (Avx2.IsAvx2Supported)
        {
            Color* palette = stackalloc Color[4];
            BuildPalette(block, palette);

            // Scale [0,1] -> [0,255], round each channel to int32 (round-to-nearest-even), then
            // narrow int32 -> uint16 -> uint8. The 256-bit packs work per 128-bit lane, so
            // entries interleave to [e0,e2 | e1,e3]; a final vpermd (32-bit-lane gather) pulls
            // e0,e1,e2,e3 into the low four lanes.
            v256 s255 = Avx.mm256_set1_ps(255f);
            v256 i01 = Avx.mm256_cvtps_epi32(
                Avx.mm256_mul_ps(Avx.mm256_loadu_ps((float*)&palette[0]), s255)
            ); // entries 0,1
            v256 i23 = Avx.mm256_cvtps_epi32(
                Avx.mm256_mul_ps(Avx.mm256_loadu_ps((float*)&palette[2]), s255)
            ); // entries 2,3
            v256 words = Avx2.mm256_packus_epi32(i01, i23); // uint16: [e0,e2 | e1,e3]
            v256 bytes = Avx2.mm256_packus_epi16(words, words); // uint8: [e0,e2,.. | e1,e3,..]
            v256 order = new(0, 4, 1, 5, 0, 0, 0, 0);
            return Avx.mm256_castsi256_si128(Avx2.mm256_permutevar8x32_epi32(bytes, order));
        }

        return default;
    }

    /// <summary>
    /// Fills <paramref name="palette"/> (4 entries) with the block's decoded colors.
    /// </summary>
    private static unsafe void BuildPalette(ulong block, Color* palette)
    {
        const float Inv3 = 1f / 3f;

        int c0Raw = (int)(block & 0xFFFF);
        int c1Raw = (int)((block >> 16) & 0xFFFF);
        bool fourColor = c0Raw > c1Raw;

        if (Avx2.IsAvx2Supported)
        {
            // The block's low 32 bits are the two RGB565 endpoints laid end to end, and
            // each endpoint stores b(5)/g(6)/r(5) from its low bit, so the 32 bits read
            // (LSB first) b0,g0,r0,b1,g1,r1. A single pdep deposits those six fields into
            // byte lanes: c0's b/g/r into bytes 0/1/2 and c1's into bytes 4/5/6, and the
            // two alpha bytes (3 and 7) are OR'd to 1.
            ulong packed = Bmi2.pdep_u64(block, 0x001F3F1F_001F3F1FUL);
            packed |= 0x0100_0000_0100_0000UL; // alpha byte = 1 in each endpoint

            // Widen all eight bytes at once so a single float8 holds both endpoints:
            // lanes 0..3 = c0's (b,g,r,a), lanes 4..7 = c1's. mm256_permute_ps (0xC6 =
            // lanes 2,1,0,3 per half) swaps b<->r to (r,g,b,a); the scale maps 5/6/5 to
            // [0,1] (alpha stays 1.0 — its byte is 1 and its scale lane is 1).
            v256 bgra = Avx.mm256_cvtepi32_ps(
                Avx2.mm256_cvtepu8_epi32(Sse2.cvtsi64x_si128((long)packed))
            );
            float8 scale = new(1f / 31f, 1f / 63f, 1f / 31f, 1f, 1f / 31f, 1f / 63f, 1f / 31f, 1f);
            float8 e = (float8)Avx.mm256_permute_ps(bgra, 0xC6) * scale; // [e0 | e1]

            // Swapping the 128-bit halves gives [e1 | e0]; interpolating e against it
            // blends the low half toward e1 and the high half toward e0 in one shot.
            float8 swapped = Avx.mm256_permute2f128_ps(e, e, 0x01);

            float8 blended;
            if (fourColor)
            {
                // low = (2*e0 + e1)/3 = palette[2], high = (2*e1 + e0)/3 = palette[3]
                blended = float8.fma(new float8(2f), e, swapped) * Inv3;
            }
            else
            {
                // low = (e0 + e1)/2 = palette[2] (midpoint); high = transparent black (0)
                blended = (e + swapped) * 0.5f;
                blended = Avx.mm256_insertf128_ps(blended, default, 1);
            }

            // palette[0..3] is 64 bytes: e fills entries 0/1, blended fills 2/3.
            Avx.mm256_storeu_ps((float*)&palette[0], e);
            Avx.mm256_storeu_ps((float*)&palette[2], blended);
        }
        else
        {
            float r0 = ((c0Raw >> 11) & 0x1F) * (1f / 31f);
            float g0 = ((c0Raw >> 5) & 0x3F) * (1f / 63f);
            float b0 = (c0Raw & 0x1F) * (1f / 31f);

            float r1 = ((c1Raw >> 11) & 0x1F) * (1f / 31f);
            float g1 = ((c1Raw >> 5) & 0x3F) * (1f / 63f);
            float b1 = (c1Raw & 0x1F) * (1f / 31f);

            palette[0] = new Color(r0, g0, b0, 1f);
            palette[1] = new Color(r1, g1, b1, 1f);

            if (fourColor)
            {
                palette[2] = new Color(
                    (2f * r0 + r1) * (1f / 3f),
                    (2f * g0 + g1) * (1f / 3f),
                    (2f * b0 + b1) * (1f / 3f),
                    1f
                );
                palette[3] = new Color(
                    (r0 + 2f * r1) * (1f / 3f),
                    (g0 + 2f * g1) * (1f / 3f),
                    (b0 + 2f * b1) * (1f / 3f),
                    1f
                );
            }
            else
            {
                palette[2] = new Color((r0 + r1) * 0.5f, (g0 + g1) * 0.5f, (b0 + b1) * 0.5f, 1f);
                palette[3] = new Color(0f, 0f, 0f, 0f);
            }
        }
    }
}
