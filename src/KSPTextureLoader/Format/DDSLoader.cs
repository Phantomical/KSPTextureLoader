using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DDSHeaders;
using KSPTextureLoader.Burst;
using KSPTextureLoader.Format.DDS;
using KSPTextureLoader.Jobs;
using KSPTextureLoader.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using static KSPTextureLoader.CPUTexture2D;
using static KSPTextureLoader.Format.DDS.DDSUtil;

namespace KSPTextureLoader.Format;

internal static unsafe class DDSLoader
{
    internal enum DDSTextureType
    {
        Texture2D,
        Texture3D,
        Texture2DArray,
        Cubemap,
        CubemapArray,
    }

    static CommandBuffer SignalBuffer;

    public static IEnumerable<object> LoadDDSTexture<T>(
        TextureHandleImpl handle,
        TextureLoadOptions options
    )
        where T : Texture
    {
        var diskPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", handle.Path);

        FileInfo info;
        if (options.Hint <= TextureLoadHint.BatchAsynchronous)
        {
            var tcs = new TaskCompletionSource<FileInfo>();
            var job = new ReadFileHeaderJob { diskPath = new(diskPath), tcs = new(tcs) };
            var rfhandle = job.Schedule();
            JobHandle.ScheduleBatchedJobs();

            using (handle.WithCompleteHandler(new JobHandleCompleteHandler(rfhandle)))
                yield return new WaitUntil(() => rfhandle.IsCompleted);

            rfhandle.Complete();

            info = tcs.Task.Result;
        }
        else
        {
            info = ReadFileHeader(diskPath);
        }

        DDSHeader header = info.header;
        DDSHeaderDX10 header10 = info.header10;

        if (options.Hint < TextureLoadHint.BatchSynchronous && AllocatorUtil.IsAboveWatermark)
            yield return AllocatorUtil.WaitUntilMemoryBelowWatermark();

        NativeArray<byte> buffer = AllocatorUtil.CreateNativeArrayHGlobal<byte>(
            (int)info.fileLength,
            NativeArrayOptions.UninitializedMemory
        );

        IFileReadStatus readStatus = FileLoader.ReadFileContents(
            diskPath,
            info.dataOffset,
            buffer,
            out JobHandle jobHandle
        );

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
                format = GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.RGBA32, false);

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

                    var colors = AllocatorUtil.CreateNativeArrayHGlobal<byte>(
                        UnsafeUtility.SizeOf<Color32>() * width * height,
                        NativeArrayOptions.UninitializedMemory
                    );

                    var job = new DecodeKopernicusPalette4bitJob
                    {
                        data = buffer,
                        colors = colors.Slice().SliceConvert<Color32>(),
                    };

                    jobGuard.JobHandle = job.ScheduleBatch(
                        width * height / 2,
                        4096,
                        jobGuard.JobHandle
                    );
                    buffer.DisposeExt(jobGuard.JobHandle);
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

                    var colors = AllocatorUtil.CreateNativeArrayHGlobal<byte>(
                        UnsafeUtility.SizeOf<Color32>() * width * height,
                        NativeArrayOptions.UninitializedMemory
                    );

                    var job = new DecodeKopernicusPalette8bitJob
                    {
                        data = buffer,
                        colors = colors.Slice().SliceConvert<Color32>(),
                    };

                    jobGuard.JobHandle = job.ScheduleBatch(
                        width * height,
                        4096,
                        jobGuard.JobHandle
                    );
                    buffer.DisposeExt(jobGuard.JobHandle);
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

        if (Config.Instance.DebugMode >= DebugLevel.Trace)
        {
            Debug.Log(
                $"[KSPTextureLoader] Loading DDS file: {handle.Path}\n"
                    + $"  - width:     {width}\n"
                    + $"  - height:    {height}\n"
                    + $"  - depth:     {depth}\n"
                    + $"  - arraySize: {arraySize}\n"
                    + $"  - mipCount:  {mipCount}\n"
                    + $"  - format:    {format}\n"
                    + $"  - data start  {info.dataOffset}\n"
                    + $"  - data length {info.fileLength}"
            );
        }

        switch (type)
        {
            case DDSTextureType.Texture2D:
                if (typeof(T) == typeof(Texture2DArray))
                {
                    arraySize = 1;
                    goto case DDSTextureType.Texture2DArray;
                }

                var upload = UploadTexture2D<T>(
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
                    jobGuard.JobHandle.Complete();

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
                            offset += mipSize;
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
                    jobGuard.JobHandle.Complete();

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
                    jobGuard.JobHandle.Complete();

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
                    jobGuard.JobHandle.Complete();

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

    internal static bool TryLoadDDSCPUTexture(
        string diskPath,
        bool? linear,
        out CPUTexture2D texture
    )
    {
        texture = null;

        var (mmf, accessor, data, info) = ReadFileHeaderFromMemoryMap(diskPath);

        try
        {
            var header = info.header;
            var header10 = info.header10;
            var flags = (DDS_HEADER_FLAGS)header.dwFlags;

            var height = (int)header.dwHeight;
            var width = (int)header.dwWidth;
            var mipCount = (int)header.dwMipMapCount;
            if (mipCount == 0)
                mipCount = 1;

            GraphicsFormat format;

            if (header10 is not null)
            {
                // Reject non-2D textures
                if (header10.miscFlag.HasFlag((DDSHeaderDX10MiscFlags)0x4)) // cubemap
                    return false;
                if (
                    header10.resourceDimension
                    == D3D10_RESOURCE_DIMENSION.D3D11_RESOURCE_DIMENSION_TEXTURE3D
                )
                    return false;
                if (header10.arraySize > 1)
                    return false;

                format = GetDxgiGraphicsFormat(header10.dxgiFormat);
            }
            else
            {
                // Reject non-2D textures
                if (flags.HasFlag(DDS_HEADER_FLAGS.DEPTH))
                    return false;
                if (header.dwCaps2.HasFlag(DDSPixelFormatCaps2.CUBEMAP))
                    return false;

                format = GetDDSPixelGraphicsFormat(header.ddspf);
                if (format == GraphicsFormat.None)
                {
                    // Try Kopernicus palette formats
                    if (header.ddspf.dwRGBBitCount == 4)
                    {
                        var expected = width * height / 2 + 16 * 4;
                        if (info.fileLength != expected)
                            return false;

                        texture = new CPU.MemoryMappedTexture2D<KopernicusPalette4>(
                            mmf,
                            accessor,
                            new(data, width, height)
                        );
                        return true;
                    }
                    else if (header.ddspf.dwRGBBitCount == 8)
                    {
                        var expected = width * height + 256 * 4;
                        if (info.fileLength != expected)
                            return false;

                        texture = new CPU.MemoryMappedTexture2D<KopernicusPalette8>(
                            mmf,
                            accessor,
                            new(data, width, height)
                        );
                        return true;
                    }

                    return false; // unsupported pixel format
                }
            }

            if (linear is bool lin)
            {
                var tformat = GraphicsFormatUtility.GetTextureFormat(format);
                format = GraphicsFormatUtility.GetGraphicsFormat(tformat, isSRGB: !lin);
            }

            {
                var textureFormat = GraphicsFormatUtility.GetTextureFormat(format);

                texture = CPUTexture2D.Create(
                    mmf,
                    accessor,
                    data,
                    width,
                    height,
                    mipCount,
                    textureFormat
                );
                return true;
            }
        }
        finally
        {
            // If texture was not assigned, we own the resources and must clean up.
            // If texture was assigned, ownership transferred to CPUTexture2D_MemoryMapped.
            if (texture == null)
            {
                accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
                accessor?.Dispose();
                mmf?.Dispose();
            }
        }
    }

    static readonly ProfilerMarker ReadFileHeaderFromMemoryMapMarker = new(
        "ReadFileHeaderFromMemoryMap"
    );

    internal static (
        MemoryMappedFile mmf,
        MemoryMappedViewAccessor accessor,
        NativeArray<byte> data,
        FileInfo info
    ) ReadFileHeaderFromMemoryMap(string diskPath)
    {
        using var scope = ReadFileHeaderFromMemoryMapMarker.Auto();

        long fileLength = new System.IO.FileInfo(diskPath).Length;

        var mmf = MemoryMappedFile.CreateFromFile(
            diskPath,
            FileMode.Open,
            null,
            0,
            MemoryMappedFileAccess.Read
        );

        MemoryMappedViewAccessor accessor = null;
        byte* pointer = null;

        try
        {
            accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);

            using var stream = new UnmanagedMemoryStream(
                pointer,
                fileLength,
                fileLength,
                FileAccess.Read
            );
            var br = new BinaryReader(stream);

            var magic = br.ReadUInt32();
            if (magic != DDSValues.uintMagic)
                throw new Exception("Invalid DDS file: incorrect magic number");

            DDSHeader header = new(br);
            DDSHeaderDX10 header10 = null;

            // file.Position doesn't reliably return the amount of bytes read
            // under certain conditions on some systems. To avoid this being an
            // issue we manually track the offset ourselves.
            long fileOffset = 128;
            if (header.ddspf.dwFourCC == DDSValues.uintDX10)
            {
                header10 = new DDSHeaderDX10(br);
                fileOffset += 20;
            }

            if (header.dwSize != 124)
                throw new Exception("Invalid DDS file: incorrect header size");
            if (header.ddspf.dwSize != 32)
                throw new Exception("Invalid DDS file: invalid pixel format size");

            long dataLength = fileLength - fileOffset;
            if (dataLength > int.MaxValue)
                throw new Exception(
                    "DDS file is too large to load. Only files < 2GB in size are supported"
                );

            var info = new FileInfo
            {
                header = header,
                header10 = header10,
                fileLength = dataLength,
                dataOffset = fileOffset,
            };

            var data = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(
                pointer + fileOffset,
                (int)dataLength,
                Allocator.Invalid
            );

            return (mmf, accessor, data, info);
        }
        catch
        {
            if (pointer != null)
                accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
            accessor?.Dispose();
            mmf.Dispose();
            throw;
        }
    }

    internal struct FileInfo
    {
        public DDSHeader header;
        public DDSHeaderDX10 header10;
        public long fileLength;
        public long dataOffset;
    }

    struct ReadFileHeaderJob : IJob
    {
        public ObjectHandle<string> diskPath;
        public ObjectHandle<TaskCompletionSource<FileInfo>> tcs;

        public void Execute()
        {
            using var dpguard = diskPath;
            using var tcsguard = this.tcs;

            var tcs = this.tcs.Target;

            try
            {
                tcs.SetResult(ReadFileHeader(diskPath.Target));
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        }
    }

    static readonly ProfilerMarker ReadFileHeaderMarker = new("ReadFileHeader");

    internal static FileInfo ReadFileHeader(string diskPath)
    {
        using var scope = ReadFileHeaderMarker.Auto();
        using var file = File.OpenRead(diskPath);

        var br = new BinaryReader(file);
        var magic = br.ReadUInt32();
        if (magic != DDSValues.uintMagic)
            throw new Exception("Invalid DDS file: incorrect magic number");

        DDSHeader header = new(br);
        DDSHeaderDX10 header10 = null;

        // file.Position doesn't reliably return the amount of bytes read
        // under certain conditions on some systems. To avoid this being an
        // issue we manually track the offset ourselves.
        long fileOffset = 128;
        if (header.ddspf.dwFourCC == DDSValues.uintDX10)
        {
            header10 = new DDSHeaderDX10(br);
            fileOffset += 20;
        }

        if (header.dwSize != 124)
            throw new Exception("Invalid DDS file: incorrect header size");
        if (header.ddspf.dwSize != 32)
            throw new Exception("Invalid DDS file: invalid pixel format size");

        long fileLength = file.Length - fileOffset;
        if (fileLength > int.MaxValue)
            throw new Exception(
                "DDS file is too large to load. Only files < 2GB in size are supported"
            );

        return new()
        {
            header = header,
            header10 = header10,
            fileLength = fileLength,
            dataOffset = fileOffset,
        };
    }

    static IEnumerable<object> UploadTexture2D<T>(
        TextureHandleImpl handle,
        int width,
        int height,
        int mipCount,
        GraphicsFormat format,
        TextureLoadOptions options,
        NativeArrayGuard<byte> bufGuard,
        IFileReadStatus readStatus,
        JobCompleteGuard jobGuard
    )
        where T : Texture
    {
        bool unreadable = !TextureLoader.Texture2DShouldBeReadable<T>(options);

        // Prefer a native texture upload if available.
        if (unreadable && DX11.SupportsAsyncUpload(width, height, format))
        {
            return DX11.UploadTexture2D<T>(
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
        }

        return UploadTexture2DBasic<T>(
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
    }

    /// <summary>
    /// A comparatively straightforward texture upload: create an uninitialized
    /// texture, wait some amount of time (depending on hints), and then copy
    /// the data into the texture.
    /// </summary>
    static IEnumerable<object> UploadTexture2DBasic<T>(
        TextureHandleImpl handle,
        int width,
        int height,
        int mipCount,
        GraphicsFormat format,
        TextureLoadOptions options,
        NativeArrayGuard<byte> bufGuard,
        IFileReadStatus readStatus,
        JobCompleteGuard jobGuard
    )
        where T : Texture
    {
        var texture = TextureUtils.CreateUninitializedTexture2D(width, height, mipCount, format);
        using var texGuard = new TextureDisposeGuard(texture);

        SignalBuffer ??= new CommandBuffer { name = "Signal Texture Upload Complete" };

        var signal = new SignalUploadCompleteData();

        unsafe
        {
            var uploadHandle = (ObjectHandle<SignalUploadCompleteData>*)
                UnsafeUtility.Malloc(
                    sizeof(ObjectHandle<SignalUploadCompleteData>),
                    16,
                    Allocator.Persistent
                );
            *uploadHandle = new(signal);

            SignalBuffer.IssuePluginEventAndData(
                SignalUploadCompleteFuncPtr,
                0,
                (IntPtr)uploadHandle
            );

            Graphics.ExecuteCommandBuffer(SignalBuffer);
            SignalBuffer.Clear();
        }

        handle.completeHandler = null;
        yield return signal;

        // If we are fully sync then we want to get this done while waiting for
        // the disk read to complete.
        if (options.Hint == TextureLoadHint.Synchronous)
            texture.GetRawTextureData<byte>();

        if (options.Hint < TextureLoadHint.BatchSynchronous)
        {
            var data = texture.GetRawTextureData<byte>();
            if (data.Length != bufGuard.array.Length)
                throw new Exception(
                    $"the texture file length is too large for the requested image size (expected {data.Length} but got {bufGuard.array.Length})"
                );

            var job = new BufferCopyJob { input = bufGuard.array, output = data };
            jobGuard.JobHandle = job.Schedule(jobGuard.JobHandle);
            bufGuard.array.DisposeExt(jobGuard.JobHandle);
            JobHandle.ScheduleBatchedJobs();

            if (!jobGuard.JobHandle.IsCompleted)
            {
                using (handle.WithCompleteHandler(new JobHandleCompleteHandler(jobGuard.JobHandle)))
                    yield return new WaitUntil(() => jobGuard.JobHandle.IsCompleted);
                jobGuard.JobHandle.Complete();
            }
        }
        else
        {
            if (!jobGuard.JobHandle.IsCompleted)
            {
                using (handle.WithCompleteHandler(new JobHandleCompleteHandler(jobGuard.JobHandle)))
                    yield return new WaitUntil(() => jobGuard.JobHandle.IsCompleted);
                jobGuard.JobHandle.Complete();
            }

            texture.LoadRawTextureData(bufGuard.array);
            bufGuard.array.DisposeExt(default);
        }

        readStatus.ThrowIfError();

        texGuard.Clear();
        handle.SetTexture<T>(texture, options);
    }

    class SignalUploadCompleteData : CustomYieldInstruction
    {
        public bool complete;

        public override bool keepWaiting => !complete;
    }

    static readonly IntPtr SignalUploadCompleteFuncPtr = Marshal.GetFunctionPointerForDelegate(
        SignalUploadComplete
    );

    static readonly ProfilerMarker SignalUploadMarker = new("SignalUploadComplete");

    /// <summary>
    /// This blocks the render thread until it is signaled by the
    /// <see cref="CountdownEvent"/>.
    /// </summary>
    /// <param name="eventID"></param>
    /// <param name="data"></param>
    static void SignalUploadComplete(uint eventID, ObjectHandle<SignalUploadCompleteData>* data)
    {
        using var scope = SignalUploadMarker.Auto();
        using var handle = *data;
        UnsafeUtility.Free(data, Allocator.Persistent);
        handle.Target.complete = true;
    }
}
