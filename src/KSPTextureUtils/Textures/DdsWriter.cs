using System.Buffers.Binary;

namespace KSPTextureUtils.Textures;

/// <summary>
/// Writes a <see cref="UnityTextureFormat"/> texture (raw mip-chain bytes) to a DDS
/// file — the inverse of <see cref="DdsReader"/>. Block-compressed and standard
/// uncompressed formats are written with a matching header so they round-trip
/// through the reader; the packed 16-bit palette formats (RGB565/RGBA4444) are
/// expanded losslessly to RGBA32, which every DDS tool understands.
/// </summary>
internal static class DdsWriter
{
    const uint DDSD_CAPS = 0x1;
    const uint DDSD_HEIGHT = 0x2;
    const uint DDSD_WIDTH = 0x4;
    const uint DDSD_PIXELFORMAT = 0x1000;
    const uint DDSD_MIPMAPCOUNT = 0x20000;
    const uint DDSD_LINEARSIZE = 0x80000;
    const uint DDSD_PITCH = 0x8;

    const uint DDPF_FOURCC = 0x4;

    const uint DDSCAPS_COMPLEX = 0x8;
    const uint DDSCAPS_TEXTURE = 0x1000;
    const uint DDSCAPS_MIPMAP = 0x400000;

    const uint DDSD_DEPTH = 0x800000;

    // DDS_HEADER.dwCaps2 flags: the "is a cubemap" bit plus the six per-face bits,
    // which every face must set for a complete cube, and the volume bit for 3D.
    const uint DDSCAPS2_CUBEMAP = 0x200;
    const uint DDSCAPS2_CUBEMAP_ALLFACES = 0xFC00;
    const uint DDSCAPS2_VOLUME = 0x200000;

    // DDS_HEADER_DXT10 fields
    const uint DDS_DIMENSION_TEXTURE2D = 3;
    const uint DDS_DIMENSION_TEXTURE3D = 4;
    const uint DDS_RESOURCE_MISC_TEXTURECUBE = 0x4;

    const uint Dx10 = 0x30315844; // "DX10"

    /// <summary>
    /// Write a texture's raw pixel bytes as a DDS of the given shape — the inverse of
    /// <see cref="DdsReader"/>. <paramref name="layers"/> carries the same per-kind
    /// count as <see cref="SourceTexture.Layers"/>: depth slices for a 3D texture,
    /// array length for a 2D array, and cube count for a cubemap array.
    ///
    /// <para>DDS and Unity agree on the payload layout for every shape — a cubemap is
    /// six complete mip chains (+X,-X,+Y,-Y,+Z,-Z), an array is one chain per slice,
    /// and a volume interleaves its slice stack per mip — so the bytes are always
    /// copied verbatim and only the header differs.</para>
    /// </summary>
    public static byte[] Write(
        UnityTextureFormat format,
        int width,
        int height,
        int mipCount,
        ReadOnlySpan<byte> data,
        TextureKind kind = TextureKind.Texture2D,
        int layers = 1
    )
    {
        // The packed palette formats have no clean DDS equivalent; expand to RGBA32.
        if (format is UnityTextureFormat.RGB565 or UnityTextureFormat.RGBA4444)
        {
            byte[] rgba = Expand16ToRgba32(format, data);
            return Write(UnityTextureFormat.RGBA32, width, height, mipCount, rgba, kind, layers);
        }

        bool cubemap = kind is TextureKind.Cubemap or TextureKind.CubemapArray;
        bool volume = kind == TextureKind.Texture3D;
        // A DX10 arraySize counts whole cubes for a cubemap array, not faces.
        uint arraySize = kind switch
        {
            TextureKind.Texture2DArray or TextureKind.CubemapArray => (uint)Math.Max(1, layers),
            _ => 1u,
        };
        int depth = volume ? Math.Max(1, layers) : 0;

        var (legacyFourCC, dxgi) = MapFormat(format);
        // A legacy header has nowhere to put an array size, so anything with more
        // than one slice has to go out with a DX10 header even when its format has
        // a perfectly good FourCC (DXT1/DXT5).
        if (arraySize > 1)
            legacyFourCC = null;
        bool compressed = TextureFormatInfo.IsBlockCompressed(format);
        long topMip = TextureFormatInfo.MipSize(format, width, height);

        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);

        w.Write(0x20534444u); // "DDS "
        w.Write(124); // dwSize
        uint flags = DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT;
        flags |= compressed ? DDSD_LINEARSIZE : DDSD_PITCH;
        if (mipCount > 1)
            flags |= DDSD_MIPMAPCOUNT;
        if (volume)
            flags |= DDSD_DEPTH;
        w.Write(flags);
        w.Write(height);
        w.Write(width);
        w.Write(
            (int)(compressed ? topMip : (long)width * TextureFormatInfo.BlockOrPixelSize(format))
        ); // pitch or linear size
        w.Write(depth);
        w.Write(mipCount);
        for (int i = 0; i < 11; i++)
            w.Write(0); // reserved1

        // DDS_PIXELFORMAT
        w.Write(32); // dwSize
        w.Write(DDPF_FOURCC); // always FourCC (legacy or DX10)
        w.Write(legacyFourCC ?? Dx10);
        w.Write(0); // rgbBitCount
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0); // masks

        uint caps = DDSCAPS_TEXTURE;
        if (mipCount > 1)
            caps |= DDSCAPS_MIPMAP | DDSCAPS_COMPLEX;
        if (cubemap || volume || arraySize > 1)
            caps |= DDSCAPS_COMPLEX;
        w.Write(caps);
        uint caps2 = 0;
        if (cubemap)
            caps2 = DDSCAPS2_CUBEMAP | DDSCAPS2_CUBEMAP_ALLFACES;
        else if (volume)
            caps2 = DDSCAPS2_VOLUME;
        w.Write(caps2);
        w.Write(0); // caps3
        w.Write(0); // caps4
        w.Write(0); // reserved2

        if (legacyFourCC is null)
        {
            // DDS_HEADER_DXT10
            w.Write(dxgi);
            // A cube is six 2D surfaces, so only a true volume is TEXTURE3D.
            w.Write(volume ? DDS_DIMENSION_TEXTURE3D : DDS_DIMENSION_TEXTURE2D);
            w.Write(cubemap ? DDS_RESOURCE_MISC_TEXTURECUBE : 0u); // miscFlag
            w.Write(arraySize);
            w.Write(0); // miscFlags2
        }

        w.Write(data);
        return ms.ToArray();
    }

    /// <summary>Returns (legacyFourCC, dxgiFormat). A non-null FourCC means no DX10 header.</summary>
    static (uint? legacyFourCC, uint dxgi) MapFormat(UnityTextureFormat format) =>
        format switch
        {
            // These two prefer the legacy FourCC, but still carry a DXGI value for
            // when an array forces the DX10 header (see the arraySize check above).
            UnityTextureFormat.DXT1 => (FourCC('D', 'X', 'T', '1'), 71u), // BC1_UNORM
            UnityTextureFormat.DXT5 => (FourCC('D', 'X', 'T', '5'), 77u), // BC3_UNORM
            UnityTextureFormat.BC4 => (null, 80u), // BC4_UNORM
            UnityTextureFormat.BC5 => (null, 83u), // BC5_UNORM
            UnityTextureFormat.BC6H => (null, 95u), // BC6H_UF16
            UnityTextureFormat.BC7 => (null, 98u), // BC7_UNORM
            UnityTextureFormat.RGBA32 => (null, 28u), // R8G8B8A8_UNORM
            UnityTextureFormat.BGRA32 => (null, 87u), // B8G8R8A8_UNORM
            UnityTextureFormat.R8 => (null, 61u), // R8_UNORM
            UnityTextureFormat.R16 => (null, 56u), // R16_UNORM
            UnityTextureFormat.RG16 => (null, 49u), // R8G8_UNORM
            UnityTextureFormat.RHalf => (null, 54u), // R16_FLOAT
            UnityTextureFormat.RGHalf => (null, 34u), // R16G16_FLOAT
            UnityTextureFormat.RGBAHalf => (null, 10u), // R16G16B16A16_FLOAT
            UnityTextureFormat.RFloat => (null, 41u), // R32_FLOAT
            UnityTextureFormat.RGFloat => (null, 16u), // R32G32_FLOAT
            UnityTextureFormat.RGBAFloat => (null, 2u), // R32G32B32A32_FLOAT
            UnityTextureFormat.RGB9e5Float => (null, 67u), // R9G9B9E5_SHAREDEXP
            _ => throw new NotSupportedException($"cannot write {format} to DDS"),
        };

    public static bool CanWrite(UnityTextureFormat format)
    {
        if (format is UnityTextureFormat.RGB565 or UnityTextureFormat.RGBA4444)
            return true;
        try
        {
            MapFormat(format);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    static byte[] Expand16ToRgba32(UnityTextureFormat format, ReadOnlySpan<byte> data)
    {
        int pixels = data.Length / 2;
        var outp = new byte[pixels * 4];
        for (int p = 0; p < pixels; p++)
        {
            ushort u = (ushort)(data[p * 2] | (data[p * 2 + 1] << 8));
            byte r,
                g,
                b,
                a;
            if (format == UnityTextureFormat.RGB565)
            {
                r = (byte)Exp5((u >> 11) & 0x1F);
                g = (byte)Exp6((u >> 5) & 0x3F);
                b = (byte)Exp5(u & 0x1F);
                a = 255;
            }
            else // RGBA4444
            {
                r = (byte)Exp4((u >> 12) & 0xF);
                g = (byte)Exp4((u >> 8) & 0xF);
                b = (byte)Exp4((u >> 4) & 0xF);
                a = (byte)Exp4(u & 0xF);
            }
            outp[p * 4 + 0] = r;
            outp[p * 4 + 1] = g;
            outp[p * 4 + 2] = b;
            outp[p * 4 + 3] = a;
        }
        return outp;
    }

    static int Exp4(int n) => n * 17;

    static int Exp5(int n) => (n << 3) | (n >> 2);

    static int Exp6(int n) => (n << 2) | (n >> 4);

    static uint FourCC(char a, char b, char c, char d) =>
        (uint)a | (uint)b << 8 | (uint)c << 16 | (uint)d << 24;
}
