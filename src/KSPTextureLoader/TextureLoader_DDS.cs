using System;
using System.Collections.Generic;
using System.IO;
using DDSHeaders;
using KSPTextureLoader.DDS;
using KSPTextureLoader.Jobs;
using KSPTextureLoader.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using static KSPTextureLoader.DDS.DDSUtil;

namespace KSPTextureLoader;

partial class TextureLoader
{
    static readonly ProfilerMarker LaunchReadMarker = new("LaunchRead");
    static readonly ProfilerMarker ReadHeaderMarker = new("Read File Header");

    internal enum DDSTextureType
    {
        Texture2D,
        Texture3D,
        Texture2DArray,
        Cubemap,
        CubemapArray,
    }

    static IEnumerable<object> LoadDDSTexture<T>(
        TextureHandleImpl handle,
        TextureLoadOptions options
    )
        where T : Texture
    {
        var diskPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", handle.Path);
        DDSHeader header;
        DDSHeaderDX10 header10 = null;
        NativeArray<byte> buffer;

        JobHandle jobHandle;
        IFileReadStatus readStatus;

        using (var file = File.OpenRead(diskPath))
        {
            using (ReadHeaderMarker.Auto())
            {
                var br = new BinaryReader(file);
                var magic = br.ReadUInt32();
                if (magic != DDSValues.uintMagic)
                    throw new Exception("Invalid DDS file: incorrect magic number");

                header = new DDSHeader(br);
                if (header.ddspf.dwFourCC == DDSValues.uintDX10)
                    header10 = new DDSHeaderDX10(br);
            }

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
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory
            );

            if (Config.Instance.UseAsyncReadManager)
            {
                unsafe
                {
                    using var readScope = LaunchReadMarker.Auto();
                    var command = new ReadCommand
                    {
                        Buffer = buffer.GetUnsafePtr(),
                        Offset = file.Position,
                        Size = length,
                    };
                    var readHandle = LaunchRead(diskPath, command);

                    readStatus = new ReadHandleStatus(readHandle);
                    jobHandle = readHandle.JobHandle;
                }
            }
            else
            {
                var exceptionStatus = new SavedExceptionStatus();
                var job = new FileReadJob
                {
                    data = buffer,
                    path = new(diskPath),
                    status = new(exceptionStatus),
                    offset = file.Position,
                };

                readStatus = exceptionStatus;
                jobHandle = job.Schedule();
                JobHandle.ScheduleBatchedJobs();
            }
        }

        using var bufGuard = new NativeArrayGuard<byte>(buffer);
        using var jobGuard = new JobCompleteGuard(jobHandle);

#if false
        {
            var prefault = new BufferPrefaultJob(buffer).Schedule();
            readGuard.JobHandle = JobHandle.CombineDependencies(prefault, readGuard.JobHandle);
            JobHandle.ScheduleBatchedJobs();
        }
#endif

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
                        Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory
                    );

                    var job = new DecodeKopernicusPalette4bitJob
                    {
                        data = buffer,
                        colors = colors.Slice().SliceConvert<Color32>(),
                        pixels = width * height,
                    };

                    jobGuard.JobHandle = job.Schedule(jobGuard.JobHandle);
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
                        Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory
                    );

                    var job = new DecodeKopernicusPalette8bitJob
                    {
                        data = buffer,
                        colors = colors.Slice().SliceConvert<Color32>(),
                        pixels = width * height,
                    };

                    jobGuard.JobHandle = job.Schedule(jobGuard.JobHandle);
                    bufGuard.array = colors;
                    buffer = colors;
                }
                else
                {
                    throw new Exception("Unsupported DDS file: no recognized format");
                }

                JobHandle.ScheduleBatchedJobs();
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
        }

        if (options.Linear is bool linear)
        {
            var tformat = GraphicsFormatUtility.GetTextureFormat(format);
            format = GraphicsFormatUtility.GetGraphicsFormat(tformat, isSRGB: !linear);
        }

        switch (type)
        {
            case DDSTextureType.Texture2D:
                if (typeof(T) == typeof(Texture2DArray))
                {
                    arraySize = 1;
                    goto case DDSTextureType.Texture2DArray;
                }

                var upload = DDSLoader.UploadTexture2D<T>(
                    handle,
                    width,
                    height,
                    mipCount,
                    format,
                    options,
                    bufGuard,
                    readStatus,
                    jobGuard
                );

                foreach (var item in upload)
                    yield return item;

                break;

            case DDSTextureType.Texture2DArray:
                var tex2dArray = TextureUtils.CreateUninitializedTexture2DArray(
                    width,
                    height,
                    arraySize,
                    mipCount,
                    format
                );
                using (var texGuard = new TextureCleanupGuard(tex2dArray))
                {
                    using (
                        handle.WithCompleteHandler(new JobHandleCompleteHandler(jobGuard.JobHandle))
                    )
                        yield return new WaitUntil(() => jobGuard.JobHandle.IsCompleted);

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
                    texGuard.Clear();
                    break;
                }

            case DDSTextureType.Cubemap:
                if (typeof(T) == typeof(CubemapArray))
                    goto case DDSTextureType.CubemapArray;

                var cubemap = TextureUtils.CreateUninitializedCubemap(width, mipCount, format);

                using (var texGuard = new TextureCleanupGuard(cubemap))
                {
                    using (
                        handle.WithCompleteHandler(new JobHandleCompleteHandler(jobGuard.JobHandle))
                    )
                        yield return new WaitUntil(() => jobGuard.JobHandle.IsCompleted);

                    int offset = 0;
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
                    texGuard.Clear();
                    break;
                }

            case DDSTextureType.CubemapArray:
                var cubeArray = TextureUtils.CreateUninitializedCubemapArray(
                    width,
                    arraySize / 6,
                    mipCount,
                    format
                );

                using (var texGuard = new TextureCleanupGuard(cubeArray))
                {
                    using (
                        handle.WithCompleteHandler(new JobHandleCompleteHandler(jobGuard.JobHandle))
                    )
                        yield return new WaitUntil(() => jobGuard.JobHandle.IsCompleted);

                    int offset = 0;
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
                    texGuard.Clear();
                    break;
                }

            case DDSTextureType.Texture3D:
                var tex3d = TextureUtils.CreateUninitializedTexture3D(
                    width,
                    height,
                    depth,
                    mipCount,
                    format
                );

                using (var texGuard = new TextureCleanupGuard(tex3d))
                {
                    using (
                        handle.WithCompleteHandler(new JobHandleCompleteHandler(jobGuard.JobHandle))
                    )
                        yield return new WaitUntil(() => jobGuard.JobHandle.IsCompleted);

                    int offset = 0;
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
                    texGuard.Clear();
                    break;
                }

            default:
                throw new NotImplementedException($"Unknown texture type {type}");
        }
    }
}
