using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using AsyncTextureLoad.DDS;
using DDSHeaders;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace AsyncTextureLoad;

partial class TextureLoader
{
    internal enum DDSTextureType
    {
        Texture2D,
        Texture3D,
        Texture2DArray,
        Cubemap,
        CubemapArray,
    }

    IEnumerable<object> LoadDDSTexture<T>(TextureHandle handle, TextureLoadOptions options)
        where T : Texture
    {
        var diskPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", handle.Path);
        DDSHeader header;
        DDSHeaderDX10 header10 = null;
        ReadHandle readHandle;
        NativeArray<byte> buffer;

        using (var file = File.OpenRead(diskPath))
        {
            var br = new BinaryReader(file);
            var magic = br.ReadUInt32();
            if (magic != DDSValues.uintMagic)
                throw new Exception("Invalid DDS file: incorrect magic number");

            header = new DDSHeader(br);
            if (header.ddspf.dwFourCC == DDSValues.uintDX10)
                header10 = new DDSHeaderDX10(br);

            if (header.dwSize != 124)
                throw new Exception("Invalid DDS file: incorrect header size");
            if (header.ddspf.dwSize != 32)
                throw new Exception("Invalid DDS file: invalid pixel format size");

            var length = file.Length - file.Position;
            if (length > int.MaxValue)
                throw new Exception(
                    "DDS file is too large to load. Only files < 2GB in size are supported"
                );

            buffer = new NativeArray<byte>(
                (int)length,
                Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory
            );

            unsafe
            {
                var command = new ReadCommand
                {
                    Buffer = buffer.GetUnsafePtr(),
                    Offset = file.Position,
                    Size = length,
                };
                readHandle = LaunchRead(diskPath, command);
            }
        }

        using var bufGuard = new NativeArrayGuard<byte>(buffer);
        using var readGuard = new SafeReadHandleGuard(readHandle);

        var flags = (DDS_HEADER_FLAGS)header.dwFlags;

        GraphicsFormat format;
        var height = (int)header.dwHeight;
        var width = (int)header.dwWidth;
        var depth = (int)header.dwDepth;
        var arraySize = 1;
        var mipCount = (int)header.dwMipMapCount;
        if (mipCount == 0)
            mipCount = 1;
        var type = DDSTextureType.Texture2D;

        if (header10 is not null)
        {
            arraySize = (int)header10.arraySize;
            if (arraySize == 0)
                throw new Exception("Invalid DDS file: DX10 array size is 0");

            format = GetDxgiGraphicsFormat(header10.dxgiFormat);
            switch (header10.resourceDimension)
            {
                case D3D10_RESOURCE_DIMENSION.D3D11_RESOURCE_DIMENSION_TEXTURE1D:
                    if (flags.HasFlag(DDS_HEADER_FLAGS.HEIGHT) && height != 1)
                        throw new Exception(
                            "Invalid DDS file: resource dimension is TEXTURE1D but height is not 1"
                        );

                    height = depth = 1;

                    if (arraySize > 1)
                        throw new Exception(
                            "1D texture arrays are not supported. Use a 2D texture array instead"
                        );

                    type = DDSTextureType.Texture2D;
                    break;

                case D3D10_RESOURCE_DIMENSION.D3D11_RESOURCE_DIMENSION_TEXTURE2D:
                    if (header10.miscFlag.HasFlag((DDSHeaderDX10MiscFlags)0x4)) // D3D11_RESOURCE_MISC_TEXTURECUBE
                    {
                        arraySize *= 6;

                        if (arraySize == 6 && typeof(T) != typeof(CubemapArray))
                            type = DDSTextureType.Cubemap;
                        else
                            type = DDSTextureType.CubemapArray;

                        if (width != height)
                            throw new Exception(
                                "Invalid DDS file: texture is a cubemap but width and height are not equal"
                            );
                    }
                    else if (arraySize == 1 && typeof(T) != typeof(Texture2DArray))
                    {
                        type = DDSTextureType.Texture2D;
                    }
                    else
                    {
                        type = DDSTextureType.Texture2DArray;
                    }
                    break;

                case D3D10_RESOURCE_DIMENSION.D3D11_RESOURCE_DIMENSION_TEXTURE3D:
                    if (!((DDS_HEADER_FLAGS)header.dwFlags).HasFlag(DDS_HEADER_FLAGS.DEPTH))
                        throw new Exception(
                            "Invalid DDS file: resource dimension is TEXTURE3D but DDS_HEADER_FLAG_DEPTH is not set"
                        );

                    if (arraySize > 1)
                        throw new Exception("Texture3D arrays are not supported");

                    type = DDSTextureType.Texture3D;
                    break;

                default:
                    throw new Exception(
                        $"Unsupported DDS resource dimension: {header10.resourceDimension}"
                    );
            }
        }
        else
        {
            format = GetDDSPixelGraphicsFormat(header.ddspf);

            if (format == GraphicsFormat.None)
            {
                // Try using Kopernicus' special palette based formats
                if (header.ddspf.dwRGBBitCount == 4)
                {
                    var expected = width * height / 2 + 16 * 4;
                    if (buffer.Length != expected)
                    {
                        throw new Exception(
                            "Unsupported DDS file: no recognized format (tried 4bpp palette image, but file size was not correct)"
                        );
                    }

                    mipCount = 1;
                    arraySize = 1;
                    depth = 1;

                    var colors = new NativeArray<byte>(
                        UnsafeUtility.SizeOf<Color32>() * width * height,
                        Allocator.TempJob,
                        NativeArrayOptions.UninitializedMemory
                    );

                    var job = new DecodeKopernicusPalette4bitJob
                    {
                        data = buffer,
                        colors = colors.Slice().SliceConvert<Color32>(),
                        pixels = width * height,
                    };

                    readGuard.JobHandle = job.Schedule(readGuard.JobHandle);
                    bufGuard.array = colors;
                    buffer = colors;
                }
                else if (header.ddspf.dwRGBBitCount == 8)
                {
                    var expected = width * height + 256 * 4;
                    if (buffer.Length != expected)
                    {
                        throw new Exception(
                            "Unsupported DDS file: no recognized format (tried 8bpp palette image, but file size was not correct)"
                        );
                    }

                    mipCount = 1;
                    arraySize = 1;
                    depth = 1;

                    var colors = new NativeArray<byte>(
                        UnsafeUtility.SizeOf<Color32>() * width * height,
                        Allocator.TempJob,
                        NativeArrayOptions.UninitializedMemory
                    );

                    var job = new DecodeKopernicusPalette8bitJob
                    {
                        data = buffer,
                        colors = colors.Slice().SliceConvert<Color32>(),
                        pixels = width * height,
                    };

                    readGuard.JobHandle = job.Schedule(readGuard.JobHandle);
                    bufGuard.array = colors;
                    buffer = colors;
                }
                else
                {
                    throw new Exception("Unsupported DDS file: no recognized format");
                }
            }
            else
            {
                if (flags.HasFlag(DDS_HEADER_FLAGS.DEPTH))
                {
                    type = DDSTextureType.Texture3D;
                }
                else
                {
                    if (header.dwCaps2.HasFlag(DDSPixelFormatCaps2.CUBEMAP))
                    {
                        const DDSPixelFormatCaps2 CUBEMAP_ALLFACES =
                            DDSPixelFormatCaps2.CUBEMAP_POSITIVEX
                            | DDSPixelFormatCaps2.CUBEMAP_NEGATIVEX
                            | DDSPixelFormatCaps2.CUBEMAP_POSITIVEY
                            | DDSPixelFormatCaps2.CUBEMAP_NEGATIVEY
                            | DDSPixelFormatCaps2.CUBEMAP_POSITIVEZ
                            | DDSPixelFormatCaps2.CUBEMAP_NEGATIVEZ;

                        if (!header.dwCaps2.HasFlag(CUBEMAP_ALLFACES))
                            throw new Exception(
                                "Unsupported DDS file: cubemap textures must have all cubemap faces"
                            );

                        arraySize = 6;
                        type = DDSTextureType.Cubemap;
                    }

                    depth = 1;
                }
            }

            JobHandle.ScheduleBatchedJobs();
        }

        if (options.Linear is bool linear)
        {
            if (linear)
                format = GraphicsFormatUtility.GetLinearFormat(format);
            else
                format = GraphicsFormatUtility.GetSRGBFormat(format);
        }

        switch (type)
        {
            case DDSTextureType.Texture2D:
                if (typeof(T) == typeof(Texture2DArray))
                {
                    arraySize = 1;
                    goto case DDSTextureType.Texture2DArray;
                }

                var tex2d = TextureUtils.CreateUninitializedTexture2D(
                    width,
                    height,
                    mipCount,
                    format
                );

                // If we are loading synchronously then we want to run UnshareTextureData
                // now, instead of waiting until after the disk read is complete.
                if (options.Hint == TextureLoadHint.Synchronous)
                {
                    tex2d.GetRawTextureData<byte>();

                    handle.completeHandler = new JobHandleCompleteHandler(readGuard.JobHandle);
                    yield return new WaitUntil(() => readGuard.JobHandle.IsCompleted);
                    handle.completeHandler = null;

                    tex2d.LoadRawTextureData(buffer);
                }
                else
                {
                    // TODO: Actually detect when the texture has finished being uploaded.
                    yield return null;
                    yield return null;

                    var texbuffer = tex2d.GetRawTextureData<byte>();

                    var job = new BufferCopyJob { input = buffer, output = texbuffer };
                    readGuard.JobHandle = job.Schedule(readGuard.JobHandle);
                    buffer.Dispose(readGuard.JobHandle);
                    JobHandle.ScheduleBatchedJobs();

                    bufGuard.array = default;

                    handle.completeHandler = new JobHandleCompleteHandler(readGuard.JobHandle);
                    yield return new WaitUntil(() => readGuard.JobHandle.IsCompleted);
                    handle.completeHandler = null;
                }

                tex2d.Apply(
                    false,
                    makeNoLongerReadable: options.Unreadable && typeof(T) != typeof(Cubemap)
                );

                handle.SetTexture<T>(tex2d, options);
                break;

            case DDSTextureType.Texture2DArray:
                var tex2dArray = TextureUtils.CreateUninitializedTexture2DArray(
                    width,
                    height,
                    arraySize,
                    mipCount,
                    format
                );

                handle.completeHandler = new JobHandleCompleteHandler(readGuard.JobHandle);
                yield return new WaitUntil(() => readGuard.JobHandle.IsCompleted);
                handle.completeHandler = null;

                int offset = 0;
                for (int element = 0; element < arraySize; ++element)
                {
                    for (int mip = 0; mip < mipCount; ++mip)
                    {
                        int mipSize = Get2DMipMapSize(width, height, mip, format);

                        if (offset + mipSize > buffer.Length)
                            throw new Exception(
                                "Invalid DDS file: not enough data for specified texture size"
                            );

                        tex2dArray.SetPixelData(buffer, mip, element, offset);
                    }
                }

                tex2dArray.Apply(false, options.Unreadable);
                handle.SetTexture<T>(tex2dArray, options);
                break;

            case DDSTextureType.Cubemap:
                if (typeof(T) == typeof(CubemapArray))
                    goto case DDSTextureType.CubemapArray;

                var cubemap = TextureUtils.CreateUninitializedCubemap(width, mipCount, format);

                handle.completeHandler = new JobHandleCompleteHandler(readGuard.JobHandle);
                yield return new WaitUntil(() => readGuard.JobHandle.IsCompleted);
                handle.completeHandler = null;

                offset = 0;
                for (int face = 0; face < 6; ++face)
                {
                    for (int mip = 0; mip < mipCount; ++mip)
                    {
                        int mipSize = Get2DMipMapSize(width, height, mip, format);

                        if (offset + mipSize > buffer.Length)
                            throw new Exception(
                                "Invalid DDS file: not enough data for specified texture size"
                            );

                        cubemap.SetPixelData(buffer, mip, (CubemapFace)face, offset);
                        offset += mipSize;
                    }
                }
                cubemap.Apply(false, options.Unreadable);
                handle.SetTexture<T>(cubemap, options);
                break;

            case DDSTextureType.CubemapArray:
                var cubeArray = TextureUtils.CreateUninitializedCubemapArray(
                    width,
                    arraySize / 6,
                    mipCount,
                    format
                );

                handle.completeHandler = new JobHandleCompleteHandler(readGuard.JobHandle);
                yield return new WaitUntil(() => readGuard.JobHandle.IsCompleted);
                handle.completeHandler = null;

                offset = 0;
                for (int element = 0; element < arraySize; ++element)
                {
                    int face = element % 6;
                    for (int mip = 0; mip < mipCount; ++mip)
                    {
                        int mipSize = Get2DMipMapSize(width, height, mip, format);

                        if (offset + mipSize > buffer.Length)
                            throw new Exception(
                                "Invalid DDS file: not enough data for specified texture size"
                            );

                        cubeArray.SetPixelData(buffer, mip, (CubemapFace)face, element, offset);
                        offset += mipSize;
                    }
                }
                cubeArray.Apply(false, options.Unreadable);
                handle.SetTexture<T>(cubeArray, options);
                break;

            case DDSTextureType.Texture3D:
                var tex3d = TextureUtils.CreateUninitializedTexture3D(
                    width,
                    height,
                    depth,
                    mipCount,
                    format
                );

                offset = 0;
                for (int mip = 0; mip < mipCount; ++mip)
                {
                    var mipSize = Get3DMipMapSize(width, height, depth, mip, format);

                    if (offset + mipSize > buffer.Length)
                        throw new Exception(
                            "Invalid DDS file: not enough data for specified texture size"
                        );

                    tex3d.SetPixelData(buffer, mip, offset);
                    offset += mipSize;
                }

                tex3d.Apply(false, makeNoLongerReadable: true);
                handle.SetTexture<T>(tex3d, options);
                break;

            default:
                throw new NotImplementedException($"Unknown texture type {type}");
        }
    }

    // This is basically a translation of GetDXGIFormat from DirectXTex
    static GraphicsFormat GetDDSPixelGraphicsFormat(DDSPixelFormat ddpf)
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
        else if (flags.HasFlag((DDSPixelFormatFlags)0x00020000)) // DDS_LUMINANCE
        {
            switch (ddpf.dwRGBBitCount)
            {
                case 16:
                    if (IsBitMask(0xFFFF, 0, 0, 0))
                        return GraphicsFormat.R16_UNorm;
                    if (IsBitMask(0x00FF, 0, 0, 0xFF00))
                        return GraphicsFormat.R8G8_UNorm;

                    break;
                case 8:
                    if (IsBitMask(0xFF, 0, 0, 0))
                        return GraphicsFormat.R8_UNorm;

                    if (IsBitMask(0x00FF, 0, 0, 0xFF00))
                        return GraphicsFormat.R8G8_UNorm;

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
            if (ddpf.dwFourCC == MakeFourCC('B', 'C', '4', 'S'))
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

    static GraphicsFormat GetDxgiGraphicsFormat(DXGI_FORMAT format)
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

    static int Get2DMipMapSize(int width, int height, int mip, GraphicsFormat format)
    {
        var bheight = (int)GraphicsFormatUtility.GetBlockHeight(format);
        var bwidth = (int)GraphicsFormatUtility.GetBlockWidth(format);
        var bsize = (int)GraphicsFormatUtility.GetBlockSize(format);

        return ((width / bwidth) * (height / bheight) >> (mip * 2)) * bsize;
    }

    static int Get3DMipMapSize(int width, int height, int depth, int mip, GraphicsFormat format)
    {
        var bheight = (int)GraphicsFormatUtility.GetBlockHeight(format);
        var bwidth = (int)GraphicsFormatUtility.GetBlockWidth(format);
        var bsize = (int)GraphicsFormatUtility.GetBlockSize(format);

        return ((width / bwidth) * (height / bheight) * depth >> (mip * 3)) * bsize;
    }

    // This one is a custom texture format for Kopernicus.
    //
    // It has a 16-element RGBA color palette, followed by 4-bpp color indices.
    struct DecodeKopernicusPalette4bitJob : IJob
    {
        [ReadOnly]
        [DeallocateOnJobCompletion]
        public NativeArray<byte> data;

        [WriteOnly]
        public NativeSlice<Color32> colors;

        public int pixels;

        public readonly unsafe void Execute()
        {
            Color32* palette = (Color32*)this.data.GetUnsafePtr();
            Color32* colors = (Color32*)this.colors.GetUnsafePtr();
            byte* data = (byte*)palette + sizeof(Color32) * 16;

            for (int i = 0; i < pixels; i += 2)
            {
                colors[i + 0] = palette[data[i / 2] & 0xF];
                colors[i + 1] = palette[data[i / 2] >> 4];
            }
        }
    }

    // Another custom palette texture format for Kopernicus.
    //
    // This one has 256 palette entries followed by 8bpp palette pixel indices.
    struct DecodeKopernicusPalette8bitJob : IJob
    {
        [ReadOnly]
        [DeallocateOnJobCompletion]
        public NativeArray<byte> data;

        [WriteOnly]
        public NativeSlice<Color32> colors;

        public int pixels;

        public readonly unsafe void Execute()
        {
            Color32* palette = (Color32*)this.data.GetUnsafePtr();
            Color32* colors = (Color32*)this.colors.GetUnsafePtr();
            byte* data = (byte*)palette + sizeof(Color32) * 256;

            for (int i = 0; i < pixels; ++i)
                colors[i] = palette[data[i]];
        }
    }

    struct BufferCopyJob : IJob
    {
        [ReadOnly]
        public NativeArray<byte> input;

        [WriteOnly]
        public NativeArray<byte> output;

        public readonly void Execute()
        {
            if (input.Length != output.Length)
                return;

            output.CopyFrom(input);
        }
    }

    class NativeArrayGuard<T>(NativeArray<T> array = default) : IDisposable
        where T : unmanaged
    {
        public NativeArray<T> array = array;

        public void Dispose() => array.Dispose();
    }
}
