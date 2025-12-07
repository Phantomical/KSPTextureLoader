using System;
using System.Runtime.CompilerServices;
using DDSHeaders;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace KSPTextureLoader.DDS;

internal static class DDSUtil
{
    // This is basically a translation of GetDXGIFormat from DirectXTex
    internal static GraphicsFormat GetDDSPixelGraphicsFormat(DDSPixelFormat ddpf)
    {
        bool IsBitMask(uint r, uint g, uint b, uint a)
        {
            return ddpf.dwRBitMask == r
                && ddpf.dwGBitMask == g
                && ddpf.dwBBitMask == b
                && ddpf.dwABitMask == a;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint MakeFourCC(char c0, char c1, char c2, char c3) =>
            (uint)c0 | (uint)c1 << 8 | (uint)c2 << 16 | (uint)c3 << 24;

        var flags = (DDSPixelFormatFlags)ddpf.dwFlags;
        if (flags.HasFlag(DDSPixelFormatFlags.DDPF_RGB))
        {
            // sRGB formats are written in the DX10 extended header
            switch (ddpf.dwRGBBitCount)
            {
                case 32:
                    if (IsBitMask(0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000))
                        return GraphicsFormat.R8G8B8A8_UNorm;
                    if (IsBitMask(0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000))
                        return GraphicsFormat.B8G8R8A8_UNorm;
                    // This doesn't exist in unity
                    // if (IsBitMask(0x00FF0000, 0x0000FF00, 0x000000FF, 0))
                    //     return GraphicsFormat.B8G8R8X8_Unorm;

                    if (IsBitMask(0x3ff00000, 0x000ffc00, 0x000003ff, 0xC0000000))
                        return GraphicsFormat.A2R10G10B10_UNormPack32;

                    if (IsBitMask(0x0000FFFF, 0xFFFF0000, 0, 0))
                        return GraphicsFormat.R16G16_UNorm;

                    if (IsBitMask(0xFFFFFFFF, 0, 0, 0))
                        return GraphicsFormat.R32_SFloat;

                    break;

                case 24:
                    break;

                case 16:
                    if (IsBitMask(0x7C00, 0x3E0, 0x001F, 0x8000))
                        return GraphicsFormat.B5G5R5A1_UNormPack16;
                    if (IsBitMask(0xF800, 0x07E0, 0x001F, 0))
                        return GraphicsFormat.B5G6R5_UNormPack16;

                    if (IsBitMask(0x0F00, 0x00F0, 0x000F, 0xF000))
                        return GraphicsFormat.B4G4R4A4_UNormPack16;

                    if (IsBitMask(0x00FF, 0, 0, 0xFF00))
                        return GraphicsFormat.R8G8_UNorm;

                    if (IsBitMask(0xFFFF, 0, 0, 0))
                        return GraphicsFormat.R16_UNorm;

                    break;

                case 8:
                    if (IsBitMask(0xFF, 0, 0, 0))
                        return GraphicsFormat.R8_UNorm;

                    break;
            }
        }
        else if (flags.HasFlag((DDSPixelFormatFlags)0x20000)) // DDS_LUMINANCE
        {
            switch (ddpf.dwRGBBitCount)
            {
                case 16:
                    if (IsBitMask(0xFFFF, 0, 0, 0))
                        return GraphicsFormat.R16_UNorm;
                    if (IsBitMask(0x00FF, 0, 0, 0xFF00))
                        return GraphicsFormat.R8G8_UNorm;

                    // Match this as a L16 texture.
                    //
                    // I haven't seen this in the wild, but it is better to load it like this.
                    if (IsBitMask(0xFFFF, 0xFFFF, 0xFFFF, 0))
                        return GraphicsFormat.R16_UNorm;

                    break;
                case 8:
                    if (IsBitMask(0xFF, 0, 0, 0))
                        return GraphicsFormat.R8_UNorm;

                    if (IsBitMask(0x00FF, 0, 0, 0xFF00))
                        return GraphicsFormat.R8G8_UNorm;

                    // Sol uses this format for its L8 heightmaps.
                    if (IsBitMask(0xFF, 0xFF, 0xFF, 0))
                        return GraphicsFormat.R8_UNorm;

                    break;
            }
        }
        else if (flags.HasFlag((DDSPixelFormatFlags)0x2)) // DDS_ALPHA
        {
            if (ddpf.dwRGBBitCount == 8)
                return GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.Alpha8, false);
        }
        else if (flags.HasFlag(DDSPixelFormatFlags.DDPF_NORMALA)) // DDS_BUMPDUDV
        {
            switch (ddpf.dwRGBBitCount)
            {
                case 32:
                    if (IsBitMask(0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000))
                        return GraphicsFormat.R8G8B8A8_SNorm;
                    if (IsBitMask(0x0000FFFF, 0xFFFF0000, 0, 0))
                        return GraphicsFormat.R16G16_SNorm;

                    break;

                case 16:
                    if (IsBitMask(0x00FF, 0xFF00, 0, 0))
                        return GraphicsFormat.R8G8_SNorm;

                    break;
            }
        }
        else if (flags.HasFlag(DDSPixelFormatFlags.DDPF_FOURCC))
        {
            if (ddpf.dwFourCC == DDSValues.uintDXT1)
                return GraphicsFormat.RGBA_DXT1_UNorm;
            if (ddpf.dwFourCC == DDSValues.uintDXT3)
                return GraphicsFormat.RGBA_DXT3_UNorm;
            if (ddpf.dwFourCC == DDSValues.uintDXT5)
                return GraphicsFormat.RGBA_DXT5_UNorm;

            // Unity doesn't expose premultiplied alpha in this way, but both formats
            // are otherwise compatible
            if (ddpf.dwFourCC == DDSValues.uintDXT2)
                return GraphicsFormat.RGBA_DXT3_UNorm;
            if (ddpf.dwFourCC == DDSValues.uintDXT4)
                return GraphicsFormat.RGBA_DXT5_UNorm;

            if (ddpf.dwFourCC == MakeFourCC('A', 'T', 'I', '1'))
                return GraphicsFormat.R_BC4_UNorm;
            if (ddpf.dwFourCC == MakeFourCC('B', 'C', '4', 'U'))
                return GraphicsFormat.R_BC4_UNorm;
            if (ddpf.dwFourCC == MakeFourCC('B', 'C', '4', 'S'))
                return GraphicsFormat.R_BC4_SNorm;

            if (ddpf.dwFourCC == MakeFourCC('A', 'T', 'I', '2'))
                return GraphicsFormat.RG_BC5_UNorm;
            if (ddpf.dwFourCC == MakeFourCC('B', 'C', '5', 'U'))
                return GraphicsFormat.RG_BC5_UNorm;
            if (ddpf.dwFourCC == MakeFourCC('B', 'C', '5', 'S'))
                return GraphicsFormat.RG_BC5_SNorm;

            // Both BC6H and BC7 are written using the DX10 extended header

            // These formats are not supported by unity
            // if (ddpf.dwFourCC == MakeFourCC('R', 'G', 'B', 'G'))
            //     return GraphicsFormat.R8G8_B8G8_Unorm;
            // if (ddpf.dwFourCC == MakeFourCC('G', 'R', 'G', 'B'))
            //     return GraphicsFormat.G8R8_G8B8_Unorm;
            // if (ddpf.dwFourCC == MakeFourCC('Y', 'U', 'Y', '2'))
            //     return GraphicsFormat.YUY2;

            // Now check for D3DFORMAT enums being set here
            switch (ddpf.dwFourCC)
            {
                case 36: // D3DFMT_A16R16G16R16
                    return GraphicsFormat.R16G16B16A16_UNorm;

                case 110: // D3DFMT_Q16W16V16U16
                    return GraphicsFormat.R16G16B16A16_SNorm;

                case 111: // D3DFMT_R16F
                    return GraphicsFormat.R16_SFloat;

                case 112: // D3DFMT_G16R16F
                    return GraphicsFormat.R16G16_SFloat;

                case 113: // D3DFMT_A16B16G16R16F
                    return GraphicsFormat.R16G16B16A16_SFloat;

                case 114: // D3DFMT_R32F
                    return GraphicsFormat.R32_SFloat;

                case 115: // D3DFMT_G32R32F
                    return GraphicsFormat.R32G32_SFloat;

                case 116: // D3DFMT_A32B32G32R32F
                    return GraphicsFormat.R32G32B32A32_SFloat;
            }
        }

        return GraphicsFormat.None;
    }

    internal static GraphicsFormat GetDxgiGraphicsFormat(DXGI_FORMAT format)
    {
        return format switch
        {
            // Single channel 8-bit
            DXGI_FORMAT.DXGI_FORMAT_R8_UNORM => GraphicsFormat.R8_UNorm,
            DXGI_FORMAT.DXGI_FORMAT_R8_SNORM => GraphicsFormat.R8_SNorm,
            DXGI_FORMAT.DXGI_FORMAT_R8_SINT => GraphicsFormat.R8_SInt,
            DXGI_FORMAT.DXGI_FORMAT_R8_UINT => GraphicsFormat.R8_UInt,
            DXGI_FORMAT.DXGI_FORMAT_A8_UNORM => GraphicsFormat.R8_UNorm,

            // Dual channel 8-bit
            DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM => GraphicsFormat.R8G8_UNorm,
            DXGI_FORMAT.DXGI_FORMAT_R8G8_SNORM => GraphicsFormat.R8G8_SNorm,
            DXGI_FORMAT.DXGI_FORMAT_R8G8_SINT => GraphicsFormat.R8G8_SInt,
            DXGI_FORMAT.DXGI_FORMAT_R8G8_UINT => GraphicsFormat.R8G8_UInt,

            // Quad channel 8-bit
            DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM => GraphicsFormat.R8G8B8A8_UNorm,
            DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB => GraphicsFormat.R8G8B8A8_SRGB,
            DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_SNORM => GraphicsFormat.R8G8B8A8_SNorm,
            DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_SINT => GraphicsFormat.R8G8B8A8_SInt,
            DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UINT => GraphicsFormat.R8G8B8A8_UInt,

            // BGRA 8-bit
            DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM => GraphicsFormat.B8G8R8A8_UNorm,
            DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM_SRGB => GraphicsFormat.B8G8R8A8_SRGB,
            DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_UNORM => GraphicsFormat.B8G8R8A8_UNorm,
            DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_UNORM_SRGB => GraphicsFormat.B8G8R8A8_SRGB,

            // Single channel 16-bit
            DXGI_FORMAT.DXGI_FORMAT_R16_UNORM => GraphicsFormat.R16_UNorm,
            DXGI_FORMAT.DXGI_FORMAT_R16_SNORM => GraphicsFormat.R16_SNorm,
            DXGI_FORMAT.DXGI_FORMAT_R16_SINT => GraphicsFormat.R16_SInt,
            DXGI_FORMAT.DXGI_FORMAT_R16_UINT => GraphicsFormat.R16_UInt,
            DXGI_FORMAT.DXGI_FORMAT_R16_FLOAT => GraphicsFormat.R16_SFloat,

            // Dual channel 16-bit
            DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM => GraphicsFormat.R16G16_UNorm,
            DXGI_FORMAT.DXGI_FORMAT_R16G16_SNORM => GraphicsFormat.R16G16_SNorm,
            DXGI_FORMAT.DXGI_FORMAT_R16G16_SINT => GraphicsFormat.R16G16_SInt,
            DXGI_FORMAT.DXGI_FORMAT_R16G16_UINT => GraphicsFormat.R16G16_UInt,
            DXGI_FORMAT.DXGI_FORMAT_R16G16_FLOAT => GraphicsFormat.R16G16_SFloat,

            // Quad channel 16-bit
            DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM => GraphicsFormat.R16G16B16A16_UNorm,
            DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_SNORM => GraphicsFormat.R16G16B16A16_SNorm,
            DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_SINT => GraphicsFormat.R16G16B16A16_SInt,
            DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UINT => GraphicsFormat.R16G16B16A16_UInt,
            DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT => GraphicsFormat.R16G16B16A16_SFloat,

            // Single channel 32-bit
            DXGI_FORMAT.DXGI_FORMAT_R32_SINT => GraphicsFormat.R32_SInt,
            DXGI_FORMAT.DXGI_FORMAT_R32_UINT => GraphicsFormat.R32_UInt,
            DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT => GraphicsFormat.R32_SFloat,

            // Dual channel 32-bit
            DXGI_FORMAT.DXGI_FORMAT_R32G32_SINT => GraphicsFormat.R32G32_SInt,
            DXGI_FORMAT.DXGI_FORMAT_R32G32_UINT => GraphicsFormat.R32G32_UInt,
            DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT => GraphicsFormat.R32G32_SFloat,

            // Triple channel 32-bit
            DXGI_FORMAT.DXGI_FORMAT_R32G32B32_SINT => GraphicsFormat.R32G32B32_SInt,
            DXGI_FORMAT.DXGI_FORMAT_R32G32B32_UINT => GraphicsFormat.R32G32B32_UInt,
            DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT => GraphicsFormat.R32G32B32_SFloat,

            // Quad channel 32-bit
            DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_SINT => GraphicsFormat.R32G32B32A32_SInt,
            DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_UINT => GraphicsFormat.R32G32B32A32_UInt,
            DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT => GraphicsFormat.R32G32B32A32_SFloat,

            // Packed formats
            DXGI_FORMAT.DXGI_FORMAT_B5G6R5_UNORM => GraphicsFormat.B5G6R5_UNormPack16,
            DXGI_FORMAT.DXGI_FORMAT_B5G5R5A1_UNORM => GraphicsFormat.B5G5R5A1_UNormPack16,
            DXGI_FORMAT.DXGI_FORMAT_B4G4R4A4_UNORM => GraphicsFormat.B4G4R4A4_UNormPack16,
            DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM => GraphicsFormat.A2R10G10B10_UNormPack32,
            DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UINT => GraphicsFormat.A2R10G10B10_UIntPack32,
            DXGI_FORMAT.DXGI_FORMAT_R11G11B10_FLOAT => GraphicsFormat.B10G11R11_UFloatPack32,
            DXGI_FORMAT.DXGI_FORMAT_R9G9B9E5_SHAREDEXP => GraphicsFormat.E5B9G9R9_UFloatPack32,

            // Block compressed formats
            DXGI_FORMAT.DXGI_FORMAT_BC1_UNORM => GraphicsFormat.RGBA_DXT1_UNorm,
            DXGI_FORMAT.DXGI_FORMAT_BC1_UNORM_SRGB => GraphicsFormat.RGBA_DXT1_SRGB,
            DXGI_FORMAT.DXGI_FORMAT_BC2_UNORM => GraphicsFormat.RGBA_DXT3_UNorm,
            DXGI_FORMAT.DXGI_FORMAT_BC2_UNORM_SRGB => GraphicsFormat.RGBA_DXT3_SRGB,
            DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM => GraphicsFormat.RGBA_DXT5_UNorm,
            DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM_SRGB => GraphicsFormat.RGBA_DXT5_SRGB,
            DXGI_FORMAT.DXGI_FORMAT_BC4_UNORM => GraphicsFormat.R_BC4_UNorm,
            DXGI_FORMAT.DXGI_FORMAT_BC4_SNORM => GraphicsFormat.R_BC4_SNorm,
            DXGI_FORMAT.DXGI_FORMAT_BC5_UNORM => GraphicsFormat.RG_BC5_UNorm,
            DXGI_FORMAT.DXGI_FORMAT_BC5_SNORM => GraphicsFormat.RG_BC5_SNorm,
            DXGI_FORMAT.DXGI_FORMAT_BC6H_UF16 => GraphicsFormat.RGB_BC6H_UFloat,
            DXGI_FORMAT.DXGI_FORMAT_BC6H_SF16 => GraphicsFormat.RGB_BC6H_SFloat,
            DXGI_FORMAT.DXGI_FORMAT_BC7_UNORM => GraphicsFormat.RGBA_BC7_UNorm,
            DXGI_FORMAT.DXGI_FORMAT_BC7_UNORM_SRGB => GraphicsFormat.RGBA_BC7_SRGB,

            _ => throw new Exception(
                $"Unsupported DDS texture: DXGI format {format} is not supported"
            ),
        };
    }

    internal static int Get2DMipMapSize(int width, int height, int mip, GraphicsFormat format)
    {
        var bheight = (int)GraphicsFormatUtility.GetBlockHeight(format);
        var bwidth = (int)GraphicsFormatUtility.GetBlockWidth(format);
        var bsize = (int)GraphicsFormatUtility.GetBlockSize(format);

        return ((width / bwidth) * (height / bheight) >> (mip * 2)) * bsize;
    }

    internal static int Get3DMipMapSize(
        int width,
        int height,
        int depth,
        int mip,
        GraphicsFormat format
    )
    {
        var bheight = (int)GraphicsFormatUtility.GetBlockHeight(format);
        var bwidth = (int)GraphicsFormatUtility.GetBlockWidth(format);
        var bsize = (int)GraphicsFormatUtility.GetBlockSize(format);

        return ((width / bwidth) * (height / bheight) * depth >> (mip * 3)) * bsize;
    }
}
