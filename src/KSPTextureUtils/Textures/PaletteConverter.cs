namespace KSPTextureUtils.Textures;

/// <summary>
/// Converts a Kopernicus palette image (a 16- or 256-entry RGBA32 palette plus
/// 4bpp/8bpp indices) into a flat <see cref="UnityTextureFormat"/> texture. The
/// smallest format that represents every palette colour without loss is chosen:
/// <see cref="UnityTextureFormat.RGB565"/> (opaque), then
/// <see cref="UnityTextureFormat.RGBA4444"/>, else
/// <see cref="UnityTextureFormat.RGBA32"/>.
/// </summary>
///
/// <remarks>
/// KSPTextureLoader decodes these formats with <c>n/15</c>, <c>n/31</c> and
/// <c>n/63</c>, which equal the integer bit-replication expansions used in the
/// "fits" tests below — so a colour that passes the test round-trips exactly.
/// </remarks>
internal static class PaletteConverter
{
    /// <summary>An 8-bit value is exact in 4 bits iff its nibbles match (v = n·17).</summary>
    static bool Fits4(byte v) => (v >> 4) == (v & 0xF);

    /// <summary>Exact in 5 bits iff replicating the top 5 bits reproduces it.</summary>
    static bool Fits5(byte v)
    {
        int n = v >> 3;
        return ((n << 3) | (n >> 2)) == v;
    }

    /// <summary>Exact in 6 bits iff replicating the top 6 bits reproduces it.</summary>
    static bool Fits6(byte v)
    {
        int n = v >> 2;
        return ((n << 2) | (n >> 4)) == v;
    }

    /// <summary>
    /// Decode the palette image and convert it to the smallest lossless format,
    /// returning that format and the encoded pixel bytes. Only palette colours that
    /// are actually referenced by <paramref name="indices"/> influence the choice —
    /// unused palette slots (often zero-filled) are ignored.
    /// </summary>
    public static (UnityTextureFormat format, byte[] data) Convert(
        ReadOnlySpan<byte> palette,
        int entries,
        ReadOnlySpan<byte> indices,
        int width,
        int height,
        bool fourBit
    )
    {
        int pixelCount = width * height;

        var used = new bool[entries];
        for (int p = 0; p < pixelCount; p++)
            used[IndexAt(indices, p, fourBit)] = true;

        var format = ChooseFormat(palette, entries, used);
        var data = Encode(format, palette, entries, indices, width, height, fourBit);
        return (format, data);
    }

    static UnityTextureFormat ChooseFormat(ReadOnlySpan<byte> palette, int entries, bool[] used)
    {
        bool all565 = true;
        bool all4444 = true;
        for (int i = 0; i < entries; i++)
        {
            if (!used[i])
                continue;

            byte r = palette[i * 4 + 0];
            byte g = palette[i * 4 + 1];
            byte b = palette[i * 4 + 2];
            byte a = palette[i * 4 + 3];

            // RGB565 has no alpha, so it only applies to fully-opaque palettes.
            if (!(a == 255 && Fits5(r) && Fits6(g) && Fits5(b)))
                all565 = false;
            if (!(Fits4(r) && Fits4(g) && Fits4(b) && Fits4(a)))
                all4444 = false;
            if (!all565 && !all4444)
                break;
        }

        if (all565)
            return UnityTextureFormat.RGB565;
        if (all4444)
            return UnityTextureFormat.RGBA4444;
        return UnityTextureFormat.RGBA32;
    }

    /// <summary>
    /// Expand the palette indices into <paramref name="format"/> pixel bytes, in the
    /// same row order as the source (no flip). 16-bit formats are written
    /// little-endian, matching how the loader reinterprets the bytes as
    /// <c>ushort</c> on the (little-endian) target.
    /// </summary>
    static byte[] Encode(
        UnityTextureFormat format,
        ReadOnlySpan<byte> palette,
        int entries,
        ReadOnlySpan<byte> indices,
        int width,
        int height,
        bool fourBit
    )
    {
        int pixelCount = width * height;

        if (format == UnityTextureFormat.RGBA32)
        {
            var outp = new byte[pixelCount * 4];
            for (int p = 0; p < pixelCount; p++)
            {
                int s = IndexAt(indices, p, fourBit) * 4;
                int d = p * 4;
                outp[d + 0] = palette[s + 0];
                outp[d + 1] = palette[s + 1];
                outp[d + 2] = palette[s + 2];
                outp[d + 3] = palette[s + 3];
            }
            return outp;
        }

        // Precompute the 16-bit encoding of each palette entry once.
        var enc = new ushort[entries];
        for (int i = 0; i < entries; i++)
        {
            byte r = palette[i * 4 + 0];
            byte g = palette[i * 4 + 1];
            byte b = palette[i * 4 + 2];
            byte a = palette[i * 4 + 3];
            enc[i] =
                format == UnityTextureFormat.RGB565
                    ? (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3))
                    : (ushort)(((r >> 4) << 12) | ((g >> 4) << 8) | ((b >> 4) << 4) | (a >> 4));
        }

        var output = new byte[pixelCount * 2];
        for (int p = 0; p < pixelCount; p++)
        {
            ushort u = enc[IndexAt(indices, p, fourBit)];
            output[p * 2 + 0] = (byte)(u & 0xFF);
            output[p * 2 + 1] = (byte)(u >> 8);
        }
        return output;
    }

    /// <summary>
    /// The palette index of pixel <paramref name="pixel"/>. For 4bpp, even pixels
    /// use the low nibble and odd pixels the high nibble — the order the loader's
    /// <c>KopernicusPalette4</c> decoder uses.
    /// </summary>
    static int IndexAt(ReadOnlySpan<byte> indices, int pixel, bool fourBit)
    {
        if (!fourBit)
            return indices[pixel];
        byte packed = indices[pixel >> 1];
        return (packed >> (4 * (pixel & 1))) & 0xF;
    }
}
