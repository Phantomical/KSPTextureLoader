namespace KSPTextureUtils.Textures;

/// <summary>
/// Quantises a flat RGBA32 image into a Kopernicus palette texture — the inverse of
/// <see cref="PaletteConverter"/>. The conversion is lossless: it only succeeds when
/// the image has at most <see cref="MaxColors"/> distinct colours, and every colour
/// is stored verbatim in an RGBA palette. The smallest palette that holds every
/// colour is chosen — a 16-entry (4bpp) palette when there are 16 or fewer distinct
/// colours, otherwise a 256-entry (8bpp) palette. The result is written as a
/// paletted DDS that <see cref="DdsReader"/> (and the in-game loader) reads back.
/// </summary>
internal static class PaletteBuilder
{
    /// <summary>Largest palette a Kopernicus palette texture can hold (an 8bpp index).</summary>
    public const int MaxColors = 256;

    /// <summary>
    /// Collect an RGBA32 image's distinct colours in first-seen order together with
    /// each pixel's palette index. Returns null when the image has more than
    /// <see cref="MaxColors"/> distinct colours (too many to palettise losslessly).
    /// Colours are packed as <c>0xAABBGGRR</c> (red in the low byte).
    /// </summary>
    public static (uint[] colors, int[] indices)? Quantize(ReadOnlySpan<byte> rgba, int pixelCount)
    {
        var colorToIndex = new Dictionary<uint, int>();
        var colors = new List<uint>();
        var indices = new int[pixelCount];

        for (int p = 0; p < pixelCount; p++)
        {
            uint c = (uint)(
                rgba[p * 4 + 0]
                | (rgba[p * 4 + 1] << 8)
                | (rgba[p * 4 + 2] << 16)
                | (rgba[p * 4 + 3] << 24)
            );
            if (!colorToIndex.TryGetValue(c, out int idx))
            {
                if (colors.Count == MaxColors)
                    return null;
                idx = colors.Count;
                colorToIndex[c] = idx;
                colors.Add(c);
            }
            indices[p] = idx;
        }

        return (colors.ToArray(), indices);
    }

    /// <summary>
    /// Snap a colour (packed <c>0xAABBGGRR</c>) to the nearest RGB565-representable
    /// value: red and blue to 5 bits, green to 6, alpha forced opaque. Each truncated
    /// channel is expanded back to 8 bits by bit replication — the same expansion
    /// <see cref="PaletteConverter"/> and the loader use — so the result survives an
    /// RGB565 encode/decode unchanged. Kopernicus re-encodes palette maps as RGB565 to
    /// save VRAM when they are requested; snapping here keeps a generated map losslessly
    /// RGB565-compatible so that conversion is a no-op instead of a lossy or skipped one.
    /// </summary>
    public static uint SnapToRgb565(uint color)
    {
        int r5 = (int)(color & 0xFF) >> 3;
        int g6 = (int)((color >> 8) & 0xFF) >> 2;
        int b5 = (int)((color >> 16) & 0xFF) >> 3;

        uint r = (uint)((r5 << 3) | (r5 >> 2));
        uint g = (uint)((g6 << 2) | (g6 >> 4));
        uint b = (uint)((b5 << 3) | (b5 >> 2));

        return r | (g << 8) | (b << 16) | 0xFF000000u;
    }

    /// <summary>
    /// Decode the top mip of an uncompressed source texture to tightly-packed RGBA32
    /// pixels, the form <see cref="Quantize"/> expects. Compressed, float and
    /// multi-channel-16 formats have no place as a palette source and are rejected
    /// with a message rather than decoded. Only a plain 2D texture is accepted.
    /// </summary>
    public static (byte[]? rgba, string? error) DecodeTopMipToRgba32(SourceTexture tex)
    {
        if (tex.Kind != TextureKind.Texture2D)
            return (null, $"palette source must be a 2D texture, not a {tex.Kind}");

        int count = tex.Width * tex.Height;
        var data = tex.Data;
        var outp = new byte[count * 4];

        switch (tex.Format)
        {
            case UnityTextureFormat.RGBA32:
                Array.Copy(data, 0, outp, 0, count * 4);
                break;
            case UnityTextureFormat.ARGB32:
                for (int p = 0; p < count; p++)
                    Store(outp, p, data[p * 4 + 1], data[p * 4 + 2], data[p * 4 + 3], data[p * 4]);
                break;
            case UnityTextureFormat.BGRA32:
                for (int p = 0; p < count; p++)
                    Store(outp, p, data[p * 4 + 2], data[p * 4 + 1], data[p * 4], data[p * 4 + 3]);
                break;
            case UnityTextureFormat.RGB24:
                for (int p = 0; p < count; p++)
                    Store(outp, p, data[p * 3], data[p * 3 + 1], data[p * 3 + 2], 255);
                break;
            case UnityTextureFormat.RGB565:
                for (int p = 0; p < count; p++)
                {
                    ushort u = (ushort)(data[p * 2] | (data[p * 2 + 1] << 8));
                    int r = (u >> 11) & 0x1F,
                        g = (u >> 5) & 0x3F,
                        b = u & 0x1F;
                    Store(
                        outp,
                        p,
                        (byte)((r << 3) | (r >> 2)),
                        (byte)((g << 2) | (g >> 4)),
                        (byte)((b << 3) | (b >> 2)),
                        255
                    );
                }
                break;
            case UnityTextureFormat.RGBA4444:
                for (int p = 0; p < count; p++)
                {
                    ushort u = (ushort)(data[p * 2] | (data[p * 2 + 1] << 8));
                    Store(
                        outp,
                        p,
                        (byte)(((u >> 12) & 0xF) * 17),
                        (byte)(((u >> 8) & 0xF) * 17),
                        (byte)(((u >> 4) & 0xF) * 17),
                        (byte)((u & 0xF) * 17)
                    );
                }
                break;
            case UnityTextureFormat.R8:
                for (int p = 0; p < count; p++)
                    Store(outp, p, data[p], 0, 0, 255);
                break;
            case UnityTextureFormat.Alpha8:
                for (int p = 0; p < count; p++)
                    Store(outp, p, 0, 0, 0, data[p]);
                break;
            default:
                return (
                    null,
                    $"source format {tex.Format} is not supported as a palette input "
                        + "(provide an uncompressed RGB/RGBA image)"
                );
        }

        return (outp, null);
    }

    static void Store(byte[] outp, int pixel, byte r, byte g, byte b, byte a)
    {
        int d = pixel * 4;
        outp[d + 0] = r;
        outp[d + 1] = g;
        outp[d + 2] = b;
        outp[d + 3] = a;
    }

    /// <summary>
    /// Encode a quantised image as a Kopernicus palette DDS: a 128-byte DDS header
    /// declaring a 4- or 8-bit paletted surface, followed by <c>entries</c> RGBA
    /// palette colours (unused slots zero-filled) and the packed pixel indices. The
    /// 4bpp packing stores even pixels in the low nibble and odd pixels in the high
    /// nibble, matching <see cref="PaletteConverter"/> / the loader's decoder.
    /// </summary>
    public static byte[] WriteDds(
        int width,
        int height,
        bool fourBit,
        ReadOnlySpan<uint> colors,
        ReadOnlySpan<int> indices
    )
    {
        int entries = fourBit ? 16 : 256;
        int pixelCount = width * height;

        var palette = new byte[entries * 4];
        for (int i = 0; i < colors.Length; i++)
        {
            uint c = colors[i];
            palette[i * 4 + 0] = (byte)(c & 0xFF); // R
            palette[i * 4 + 1] = (byte)((c >> 8) & 0xFF); // G
            palette[i * 4 + 2] = (byte)((c >> 16) & 0xFF); // B
            palette[i * 4 + 3] = (byte)((c >> 24) & 0xFF); // A
        }

        byte[] packed;
        if (fourBit)
        {
            packed = new byte[(pixelCount + 1) / 2];
            for (int p = 0; p < pixelCount; p++)
                packed[p >> 1] |= (byte)((indices[p] & 0xF) << (4 * (p & 1)));
        }
        else
        {
            packed = new byte[pixelCount];
            for (int p = 0; p < pixelCount; p++)
                packed[p] = (byte)indices[p];
        }

        return BuildDds(width, height, fourBit, palette, packed);
    }

    /// <summary>
    /// Assemble the raw paletted-DDS bytes: the 128-byte header (a DDS_PIXELFORMAT
    /// with no flags and a 4/8 bit count, which the reader recognises as a palette),
    /// then the RGBA palette, then the packed indices.
    /// </summary>
    public static byte[] BuildDds(
        int width,
        int height,
        bool fourBit,
        byte[] palette,
        byte[] indices
    )
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0x20534444u); // "DDS "
        w.Write(124); // dwSize
        w.Write(0x1007); // CAPS | HEIGHT | WIDTH | PIXELFORMAT
        w.Write(height);
        w.Write(width);
        w.Write(0); // pitch
        w.Write(0); // depth
        w.Write(0); // mipCount
        for (int i = 0; i < 11; i++)
            w.Write(0); // reserved1
        // DDS_PIXELFORMAT
        w.Write(32); // dwSize
        w.Write(0); // dwFlags (none -> palette)
        w.Write(0); // fourCC
        w.Write(fourBit ? 4 : 8); // rgbBitCount
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0); // masks
        // caps
        w.Write(0x1000); // TEXTURE
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(palette);
        w.Write(indices);
        return ms.ToArray();
    }
}
