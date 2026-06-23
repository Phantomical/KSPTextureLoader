using System.Buffers.Binary;

namespace KSPTextureBundler.Textures;

/// <summary>
/// A minimal, self-contained DDS reader that maps a DDS file to the classic Unity
/// <see cref="UnityTextureFormat"/> and returns the raw mip-chain bytes verbatim.
/// Block-compressed and uncompressed DDS layouts already match Unity's expected
/// stream layout, so the pixel payload is copied without transformation.
/// </summary>
internal static class DdsReader
{
    const uint Magic = 0x20534444; // "DDS "
    const uint Dx10FourCC = 0x30315844; // "DX10"

    // DDS_PIXELFORMAT flags
    const uint DDPF_ALPHAPIXELS = 0x1;
    const uint DDPF_ALPHA = 0x2;
    const uint DDPF_FOURCC = 0x4;
    const uint DDPF_RGB = 0x40;
    const uint DDPF_LUMINANCE = 0x20000;

    // DDS_HEADER flags
    const uint DDSD_DEPTH = 0x800000;

    // DDS_HEADER.dwCaps2 flags
    const uint DDSCAPS2_CUBEMAP = 0x200;
    const uint DDSCAPS2_CUBEMAP_ALLFACES = 0xFC00; // all six face bits set
    const uint DDSCAPS2_VOLUME = 0x200000;

    // DDS_HEADER_DXT10 fields
    const uint DDS_DIMENSION_TEXTURE3D = 4; // D3D10_RESOURCE_DIMENSION_TEXTURE3D
    const uint DDS_RESOURCE_MISC_TEXTURECUBE = 0x4;

    static uint FourCC(char a, char b, char c, char d) =>
        (uint)a | (uint)b << 8 | (uint)c << 16 | (uint)d << 24;

    public static bool IsDds(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic;

    /// <summary>
    /// Decode <paramref name="path"/> as DDS. On success returns the texture and a
    /// null skip; on a recognised-but-excluded or unsupported file returns a skip
    /// and a null texture.
    /// </summary>
    public static (SourceTexture? texture, SkippedTexture? skip) Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string name = Path.GetFileNameWithoutExtension(path);

        SkippedTexture Skip(SkipReason reason, string detail) =>
            new()
            {
                SourcePath = path,
                Reason = reason,
                Detail = detail,
            };

        if (bytes.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic)
            return (null, Skip(SkipReason.Invalid, "not a DDS file (bad magic)"));

        var h = bytes.AsSpan(4, 124);
        uint dwSize = BinaryPrimitives.ReadUInt32LittleEndian(h);
        if (dwSize != 124)
            return (null, Skip(SkipReason.Invalid, $"bad DDS header size {dwSize}"));

        uint dwFlags = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
        int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[8..]);
        int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
        int depth = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
        int mipCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
        if (mipCount == 0)
            mipCount = 1;
        uint caps2 = BinaryPrimitives.ReadUInt32LittleEndian(h[108..]);

        // DDS_PIXELFORMAT starts at offset 72 within the 124-byte header.
        var pf = h[72..];
        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(pf[4..]);
        uint fourCC = BinaryPrimitives.ReadUInt32LittleEndian(pf[8..]);
        uint rgbBitCount = BinaryPrimitives.ReadUInt32LittleEndian(pf[12..]);
        uint rMask = BinaryPrimitives.ReadUInt32LittleEndian(pf[16..]);
        uint gMask = BinaryPrimitives.ReadUInt32LittleEndian(pf[20..]);
        uint bMask = BinaryPrimitives.ReadUInt32LittleEndian(pf[24..]);
        uint aMask = BinaryPrimitives.ReadUInt32LittleEndian(pf[28..]);

        int dataOffset = 128;
        bool isDx10 = (pfFlags & DDPF_FOURCC) != 0 && fourCC == Dx10FourCC;
        UnityTextureFormat? format;
        int colorSpace = 1; // default sRGB; refined for known linear/data formats

        // DDS_HEADER_DXT10 fields (defaults describe a single non-array 2D surface).
        uint dx10ResourceDimension = 0;
        uint dx10MiscFlag = 0;
        uint dx10ArraySize = 1;

        if (isDx10)
        {
            if (bytes.Length < 148)
                return (null, Skip(SkipReason.Invalid, "DX10 header truncated"));
            dataOffset = 148;
            uint dxgi = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(128, 4));
            dx10ResourceDimension = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(132, 4));
            dx10MiscFlag = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(136, 4));
            dx10ArraySize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(140, 4));
            (format, colorSpace) = MapDxgi(dxgi);
            if (format is null)
                return (
                    null,
                    Skip(
                        SkipReason.UnsupportedFormat,
                        $"DXGI format {dxgi} has no Unity equivalent"
                    )
                );
        }
        else if ((pfFlags & DDPF_FOURCC) != 0)
        {
            format = MapFourCC(fourCC);
            colorSpace = 1;
            if (format is null)
                return (
                    null,
                    Skip(
                        SkipReason.UnsupportedFormat,
                        $"FourCC 0x{fourCC:X8} ('{FourCcString(fourCC)}') has no Unity equivalent"
                    )
                );
        }
        else
        {
            format = MapUncompressed(pfFlags, rgbBitCount, rMask, gMask, bMask, aMask);
            colorSpace = 1;
            if (format is null)
            {
                // An unrecognised 4/8 bpp image of exactly the right size is a
                // Kopernicus palette texture. Decode it to a flat 16/32-bit format
                // (the same data the loader would expand it to). 128 is the DDS
                // header size; palette images never carry a DX10 header.
                long paletteDataLen = bytes.Length - 128;
                if (rgbBitCount == 4 && paletteDataLen == (long)width * height / 2 + 16 * 4)
                    return DecodePalette(bytes, 128, 16, width, height, fourBit: true, name, path);
                if (rgbBitCount == 8 && paletteDataLen == (long)width * height + 256 * 4)
                    return DecodePalette(
                        bytes,
                        128,
                        256,
                        width,
                        height,
                        fourBit: false,
                        name,
                        path
                    );

                return (
                    null,
                    Skip(
                        SkipReason.UnsupportedFormat,
                        $"unrecognised uncompressed pixel format ({rgbBitCount}bpp)"
                    )
                );
            }
        }

        if (width <= 0 || height <= 0)
            return (null, Skip(SkipReason.Invalid, $"invalid dimensions {width}x{height}"));

        // Classify the surface topology: a plain 2D texture, a cubemap (6 faces), a
        // 2D/cube array (DX10 arraySize), or a volume (3D). DDS stores every face and
        // array slice as a full mip chain in the same order Unity expects, so the
        // payload is still copied verbatim regardless of kind.
        var kind = TextureKind.Texture2D;
        int layers = 1; // per-kind surface count carried on SourceTexture.Layers
        long surfaces = 1; // number of full mip chains concatenated in the payload
        bool volume = false;

        bool cube =
            (caps2 & DDSCAPS2_CUBEMAP) != 0
            || (isDx10 && (dx10MiscFlag & DDS_RESOURCE_MISC_TEXTURECUBE) != 0);

        if (cube)
        {
            // A legacy (non-DX10) cubemap must declare all six faces to be a Unity
            // Cubemap; partial cubemaps have no equivalent.
            if (!isDx10 && (caps2 & DDSCAPS2_CUBEMAP_ALLFACES) != DDSCAPS2_CUBEMAP_ALLFACES)
                return (
                    null,
                    Skip(SkipReason.UnsupportedFormat, "partial cubemap (missing faces)")
                );

            int cubes = isDx10 ? (int)Math.Max(1u, dx10ArraySize) : 1;
            kind = cubes > 1 ? TextureKind.CubemapArray : TextureKind.Cubemap;
            layers = cubes; // unused for a single Cubemap; cubemap count for an array
            surfaces = 6L * cubes;
        }
        else if (
            (dwFlags & DDSD_DEPTH) != 0
            || (caps2 & DDSCAPS2_VOLUME) != 0
            || (isDx10 && dx10ResourceDimension == DDS_DIMENSION_TEXTURE3D)
        )
        {
            kind = TextureKind.Texture3D;
            layers = Math.Max(1, depth);
            volume = true;
        }
        else if (isDx10 && dx10ArraySize > 1)
        {
            kind = TextureKind.Texture2DArray;
            layers = (int)dx10ArraySize;
            surfaces = dx10ArraySize;
        }

        long expected = volume
            ? TextureFormatInfo.VolumeMipChainSize(format.Value, width, height, layers, mipCount)
            : surfaces * TextureFormatInfo.MipChainSize(format.Value, width, height, mipCount);
        long available = bytes.Length - dataOffset;
        if (available < expected)
            return (
                null,
                Skip(
                    SkipReason.Invalid,
                    $"truncated pixel data: have {available} bytes, need {expected} "
                        + $"for {kind} {width}x{height} {format} x{mipCount} mips, {layers} layer(s)"
                )
            );

        var data = new byte[expected];
        Array.Copy(bytes, dataOffset, data, 0, expected);

        return (
            new SourceTexture
            {
                Name = name,
                Kind = kind,
                Layers = layers,
                Width = width,
                Height = height,
                MipCount = mipCount,
                Format = format.Value,
                ColorSpace = colorSpace,
                Data = data,
                SourcePath = path,
            },
            null
        );
    }

    static (SourceTexture? texture, SkippedTexture? skip) DecodePalette(
        byte[] bytes,
        int offset,
        int entries,
        int width,
        int height,
        bool fourBit,
        string name,
        string path
    )
    {
        if (width <= 0 || height <= 0)
            return (
                null,
                new SkippedTexture
                {
                    SourcePath = path,
                    Reason = SkipReason.Invalid,
                    Detail = $"invalid palette dimensions {width}x{height}",
                }
            );

        int paletteBytes = entries * 4;
        var palette = bytes.AsSpan(offset, paletteBytes);
        var indices = bytes.AsSpan(offset + paletteBytes);

        var (format, data) = PaletteConverter.Convert(
            palette,
            entries,
            indices,
            width,
            height,
            fourBit
        );

        return (
            new SourceTexture
            {
                Name = name,
                Width = width,
                Height = height,
                MipCount = 1,
                Format = format,
                ColorSpace = 1, // palette colours are sRGB
                Data = data,
                SourcePath = path,
            },
            null
        );
    }

    static UnityTextureFormat? MapFourCC(uint fourCC)
    {
        if (fourCC == FourCC('D', 'X', 'T', '1'))
            return UnityTextureFormat.DXT1;
        if (fourCC == FourCC('D', 'X', 'T', '5') || fourCC == FourCC('D', 'X', 'T', '4'))
            return UnityTextureFormat.DXT5;
        // DXT2/DXT3 (BC2) has no classic Unity TextureFormat — caller skips it.
        if (
            fourCC == FourCC('A', 'T', 'I', '1')
            || fourCC == FourCC('B', 'C', '4', 'U')
            || fourCC == FourCC('B', 'C', '4', 'S')
        )
            return UnityTextureFormat.BC4;
        if (
            fourCC == FourCC('A', 'T', 'I', '2')
            || fourCC == FourCC('B', 'C', '5', 'U')
            || fourCC == FourCC('B', 'C', '5', 'S')
        )
            return UnityTextureFormat.BC5;

        // Legacy D3DFMT numeric FourCC codes for typed surfaces.
        return fourCC switch
        {
            111 => UnityTextureFormat.RHalf,
            112 => UnityTextureFormat.RGHalf,
            113 => UnityTextureFormat.RGBAHalf,
            114 => UnityTextureFormat.RFloat,
            115 => UnityTextureFormat.RGFloat,
            116 => UnityTextureFormat.RGBAFloat,
            _ => null,
        };
    }

    static UnityTextureFormat? MapUncompressed(
        uint flags,
        uint bits,
        uint r,
        uint g,
        uint b,
        uint a
    )
    {
        bool Mask(uint rr, uint gg, uint bb, uint aa) => r == rr && g == gg && b == bb && a == aa;

        if ((flags & DDPF_RGB) != 0)
        {
            switch (bits)
            {
                case 32:
                    if (Mask(0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000))
                        return UnityTextureFormat.RGBA32;
                    if (Mask(0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000))
                        return UnityTextureFormat.BGRA32;
                    // 32bpp R16G16 (0x0000FFFF/0xFFFF0000) has no classic Unity
                    // TextureFormat in 2019.4 (RG32 arrived in 2020.2); skip it.
                    break;
                case 16:
                    if (Mask(0x00FF, 0, 0, 0xFF00))
                        return UnityTextureFormat.RG16;
                    if (Mask(0xFFFF, 0, 0, 0))
                        return UnityTextureFormat.R16;
                    break;
                case 8:
                    if (Mask(0xFF, 0, 0, 0))
                        return UnityTextureFormat.R8;
                    break;
            }
        }
        else if ((flags & DDPF_LUMINANCE) != 0)
        {
            switch (bits)
            {
                case 16:
                    if (Mask(0xFFFF, 0, 0, 0))
                        return UnityTextureFormat.R16;
                    if (Mask(0x00FF, 0, 0, 0xFF00))
                        return UnityTextureFormat.RG16;
                    break;
                case 8:
                    if (Mask(0xFF, 0, 0, 0))
                        return UnityTextureFormat.R8;
                    break;
            }
        }
        else if ((flags & DDPF_ALPHA) != 0)
        {
            if (bits == 8)
                return UnityTextureFormat.Alpha8;
        }

        return null;
    }

    // DXGI_FORMAT subset -> (Unity format, colorSpace). colorSpace: 1 sRGB, 0 linear.
    static (UnityTextureFormat?, int) MapDxgi(uint dxgi) =>
        dxgi switch
        {
            // R8G8B8A8
            28 => (UnityTextureFormat.RGBA32, 0), // R8G8B8A8_UNORM
            29 => (UnityTextureFormat.RGBA32, 1), // R8G8B8A8_UNORM_SRGB
            // B8G8R8A8
            87 => (UnityTextureFormat.BGRA32, 0), // B8G8R8A8_UNORM
            91 => (UnityTextureFormat.BGRA32, 1), // B8G8R8A8_UNORM_SRGB
            // single / dual channel
            61 => (UnityTextureFormat.R8, 0), // R8_UNORM
            49 => (UnityTextureFormat.RG16, 0), // R8G8_UNORM
            56 => (UnityTextureFormat.R16, 0), // R16_UNORM
            // float
            54 => (UnityTextureFormat.RHalf, 0), // R16_FLOAT
            34 => (UnityTextureFormat.RGHalf, 0), // R16G16_FLOAT
            10 => (UnityTextureFormat.RGBAHalf, 0), // R16G16B16A16_FLOAT
            41 => (UnityTextureFormat.RFloat, 0), // R32_FLOAT
            16 => (UnityTextureFormat.RGFloat, 0), // R32G32_FLOAT
            2 => (UnityTextureFormat.RGBAFloat, 0), // R32G32B32A32_FLOAT
            67 => (UnityTextureFormat.RGB9e5Float, 0), // R9G9B9E5_SHAREDEXP
            // block compressed
            70 or 71 => (UnityTextureFormat.DXT1, 0), // BC1_TYPELESS/UNORM
            72 => (UnityTextureFormat.DXT1, 1), // BC1_UNORM_SRGB
            // BC2 (73/74/75) has no classic Unity format -> unsupported
            76 or 77 => (UnityTextureFormat.DXT5, 0), // BC3_TYPELESS/UNORM
            78 => (UnityTextureFormat.DXT5, 1), // BC3_UNORM_SRGB
            79 or 80 => (UnityTextureFormat.BC4, 0), // BC4_UNORM
            81 => (UnityTextureFormat.BC4, 0), // BC4_SNORM
            82 or 83 => (UnityTextureFormat.BC5, 0), // BC5_UNORM
            84 => (UnityTextureFormat.BC5, 0), // BC5_SNORM
            95 or 96 => (UnityTextureFormat.BC6H, 0), // BC6H_UF16 / SF16
            98 or 99 => (UnityTextureFormat.BC7, 0), // BC7_TYPELESS/UNORM
            100 => (UnityTextureFormat.BC7, 1), // BC7_UNORM_SRGB
            _ => (null, 1),
        };

    static string FourCcString(uint v) =>
        new([(char)(v & 0xFF), (char)((v >> 8) & 0xFF), (char)((v >> 16) & 0xFF), (char)(v >> 24)]);
}
