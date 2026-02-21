using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection.Emit;
using System.Threading.Tasks;
using DDSHeaders;
using KSPTextureLoader.Burst;
using KSPTextureLoader.Format.DDS;
using KSPTextureLoader.Jobs;
using KSPTextureLoader.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using static KSPTextureLoader.CPUTexture2D;
using static KSPTextureLoader.Format.DDS.DDSUtil;

namespace KSPTextureLoader.Format;

internal static class DDSLoader
{
    internal enum DDSTextureType
    {
        Texture2D,
        Texture3D,
        Texture2DArray,
        Cubemap,
        CubemapArray,
    }

    #region ReadFileHeader
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

    internal static Task<FileInfo> ReadFileHeaderAsync(string diskPath)
    {
        return Task.Run(() => ReadFileHeader(diskPath));
    }
    #endregion

    #region ReadTextureMetadata
    internal struct TextureMetadata
    {
        public Task<NativeArray<byte>> data;
        public GraphicsFormat format;
        public int width;
        public int height;
        public int depth;
        public int arraySize;
        public int mipCount;
        public DDSTextureType type;
    }

    internal static Task<TextureMetadata> GetTextureMetadata<T>(
        Task<FileInfo> infoTask,
        Task<NativeArray<byte>> dataTask,
        TextureLoadOptions options
    )
        where T : Texture
    {
        return Task.Run(async () =>
        {
            using var dguard = new TaskArrayDisposeGuard(dataTask);
            var info = await infoTask;

            DDSHeader header = info.header;
            DDSHeaderDX10 header10 = info.header10;

            GraphicsFormat format;
            var height = (int)header.dwHeight;
            var width = (int)header.dwWidth;
            var depth = (int)header.dwDepth;
            var arraySize = 1;
            var mipCount = (int)header.dwMipMapCount;
            if (mipCount == 0)
                mipCount = 1;
            var type = DDSTextureType.Texture2D;
            var flags = (DDS_HEADER_FLAGS)header.dwFlags;

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
                        mipCount = 1;
                        arraySize = 1;
                        depth = 1;

                        var colors = AllocatorUtil.CreateNativeArrayHGlobal<byte>(
                            UnsafeUtility.SizeOf<Color32>() * width * height,
                            NativeArrayOptions.UninitializedMemory
                        );
                        dguard.data = Task.FromResult(colors);

                        var task = AsyncUtil
                            .LaunchMainThreadTask(async () =>
                            {
                                var buffer = await dataTask;
                                using var guard = new TaskArrayDisposeGuard(dataTask);

                                var expected = width * height / 2 + 16 * 4;
                                if (buffer.Length != expected)
                                {
                                    throw new Exception(
                                        "Unsupported DDS file: no recognized format (tried 4bpp palette image, but file size was not correct)"
                                    );
                                }

                                var job = new DecodeKopernicusPalette4bitJob
                                {
                                    data = buffer,
                                    colors = colors.Slice().SliceConvert<Color32>(),
                                };
                                var handle = job.ScheduleBatch(width * height / 2, 4096);
                                buffer.DisposeExt(handle);
                                guard.data = null;

                                return AsyncUtil.WaitFor(handle);
                            })
                            .Unwrap();

                        dataTask = Task.Run(async () =>
                        {
                            try
                            {
                                await task;
                                return colors;
                            }
                            catch
                            {
                                colors.DisposeExt();
                                throw;
                            }
                        });
                        dguard.data = dataTask;
                    }
                    else if (header.ddspf.dwRGBBitCount == 8)
                    {
                        mipCount = 1;
                        arraySize = 1;
                        depth = 1;

                        var colors = AllocatorUtil.CreateNativeArrayHGlobal<byte>(
                            UnsafeUtility.SizeOf<Color32>() * width * height,
                            NativeArrayOptions.UninitializedMemory
                        );

                        var task = AsyncUtil
                            .LaunchMainThreadTask(async () =>
                            {
                                var buffer = await dataTask;
                                using var guard = new TaskArrayDisposeGuard(dataTask);

                                var expected = width * height + 256 * 4;
                                if (buffer.Length != expected)
                                {
                                    throw new Exception(
                                        "Unsupported DDS file: no recognized format (tried 4bpp palette image, but file size was not correct)"
                                    );
                                }

                                var job = new DecodeKopernicusPalette8bitJob
                                {
                                    data = buffer,
                                    colors = colors.Slice().SliceConvert<Color32>(),
                                };
                                var handle = job.ScheduleBatch(width * height / 2, 4096);
                                buffer.DisposeExt(handle);
                                guard.data = null;

                                return AsyncUtil.WaitFor(handle);
                            })
                            .Unwrap();

                        dataTask = Task.Run(async () =>
                        {
                            try
                            {
                                await task;
                                return colors;
                            }
                            catch
                            {
                                colors.DisposeExt();
                                throw;
                            }
                        });
                        dguard.data = dataTask;
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
            }

            if (options.Linear is bool linear)
            {
                var tformat = GraphicsFormatUtility.GetTextureFormat(format);
                format = GraphicsFormatUtility.GetGraphicsFormat(tformat, isSRGB: !linear);
            }

            dguard.data = null;
            return new TextureMetadata
            {
                data = dataTask,
                format = format,
                width = width,
                height = height,
                depth = depth,
                arraySize = arraySize,
                mipCount = mipCount,
                type = type,
            };
        });
    }

    internal class TaskArrayDisposeGuard(Task<NativeArray<byte>> data) : IDisposable
    {
        public Task<NativeArray<byte>> data = data;

        public void AddDependency(Task task)
        {
            var prev = data;
            data = Task.Run(async () =>
            {
                try
                {
                    await task;
                }
                catch { }
                return await prev;
            });
        }

        public void Dispose()
        {
            if (data is null)
                return;

            var task = data;
            Task.Run(async () =>
            {
                try
                {
                    var buffer = await task;
                    buffer.DisposeExt();
                }
                catch { }
            });
            data = null;
        }
    }
    #endregion

    public static async Task LoadDDSTextureAsync<T>(
        TextureHandleImpl handle,
        TextureLoadOptions options
    )
        where T : Texture
    {
        var diskPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", handle.Path);
        var infoTask = ReadFileHeaderAsync(diskPath);
        var dataTask = FileLoader.ReadFileContentsAsync(
            Task.Run(async () =>
            {
                var info = await infoTask;
                return new FileLoader.FileReadInfo
                {
                    path = diskPath,
                    offset = info.dataOffset,
                    length = (int)info.fileLength,
                };
            })
        );
        var metadataTask = GetTextureMetadata<T>(infoTask, dataTask, options);
        dataTask = Task.Run(async () => (await metadataTask).data).Unwrap();

        using var dguard = new TaskArrayDisposeGuard(dataTask);
        var metadata = await metadataTask;

        switch (metadata.type)
        {
            case DDSTextureType.Texture2D:
                if (typeof(T) == typeof(Texture2DArray))
                {
                    metadata.arraySize = 1;
                    goto case DDSTextureType.Texture2DArray;
                }

                dguard.data = null;
                await LoadTexture2D<T>(handle, options, metadata, dataTask);
                break;

            case DDSTextureType.Texture2DArray:
                await LoadTexture2DArray<T>(handle, options, metadata, dataTask);
                break;

            case DDSTextureType.Cubemap:
                if (typeof(T) == typeof(CubemapArray))
                    goto case DDSTextureType.CubemapArray;

                await LoadTextureCubemap<T>(handle, options, metadata, dataTask);
                break;

            case DDSTextureType.CubemapArray:
                await LoadTextureCubemapArray<T>(handle, options, metadata, dataTask);
                break;

            case DDSTextureType.Texture3D:
                await LoadTexture2D<T>(handle, options, metadata, dataTask);
                break;

            default:
                throw new NotImplementedException($"Unknown texture type {metadata.type}");
        }
    }

    static readonly ProfilerMarker LoadTextureDataMarker = new("LoadTextureData");

    static async Task LoadTexture2D<T>(
        TextureHandleImpl handle,
        TextureLoadOptions options,
        TextureMetadata metadata,
        Task<NativeArray<byte>> dataTask
    )
        where T : Texture
    {
        using var dguard = new TaskArrayDisposeGuard(dataTask);
        bool unreadable = !TextureLoader.Texture2DShouldBeReadable<T>(options);
        var width = metadata.width;
        var height = metadata.height;
        var mipCount = metadata.mipCount;
        var format = metadata.format;

        // Prefer a native texture upload if available.
        if (DX11.SupportsAsyncUpload(width, height, format))
        {
            dguard.data = null;
            await DX11.UploadTexture2DAsync<T>(handle, options, metadata, dataTask);
            return;
        }

        var texture = TextureUtils.CreateUninitializedTexture2D(width, height, mipCount, format);
        using var texGuard = new TextureDisposeGuard(texture);

        if (options.Hint == TextureLoadHint.Synchronous)
        {
            var data = await dataTask;

            using var scope = LoadTextureDataMarker.Auto();
            texture.LoadRawTextureData(data);
        }
        else
        {
            // This will wait until the texture has been uploaded by the graphics
            // and so it won't make a copy if we call GetRawTextureData.
            await AsyncUtil.RunOnGraphicsThread(static () => { });

            var texdata = texture.GetRawTextureData<byte>();
            await Task.Run(async () =>
            {
                var data = await dataTask;

                using var scope = LoadTextureDataMarker.Auto();

                if (data.Length != texdata.Length)
                    throw new InvalidOperationException(
                        $"input and output lengths do not match (input {data.Length}, output {texdata.Length})"
                    );

                texdata.CopyFrom(data);
            });
        }

        texture.Apply(false, makeNoLongerReadable: unreadable);

        texGuard.Clear();
        handle.SetTexture<T>(texture, options);
    }

    static async Task LoadTexture2DArray<T>(
        TextureHandleImpl handle,
        TextureLoadOptions options,
        TextureMetadata metadata,
        Task<NativeArray<byte>> dataTask
    )
        where T : Texture
    {
        var tex2dArray = TextureUtils.CreateUninitializedTexture2DArray(
            metadata.width,
            metadata.height,
            metadata.arraySize,
            metadata.mipCount,
            metadata.format
        );
        using var texGuard = new TextureCleanupGuard(tex2dArray);

        if (options.Hint != TextureLoadHint.Synchronous)
        {
            // This will wait until the texture has been uploaded by the graphics
            // and so it won't make a copy if we call SetPixelData.
            await AsyncUtil.RunOnGraphicsThread(() => { });
        }

        var buffer = await dataTask;
        var arraySize = metadata.arraySize;
        var mipCount = metadata.mipCount;
        var width = metadata.width;
        var height = metadata.height;
        var format = metadata.format;

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
    }

    static async Task LoadTextureCubemap<T>(
        TextureHandleImpl handle,
        TextureLoadOptions options,
        TextureMetadata metadata,
        Task<NativeArray<byte>> dataTask
    )
        where T : Texture
    {
        var arraySize = metadata.arraySize;
        var mipCount = metadata.mipCount;
        var width = metadata.width;
        var height = metadata.height;
        var format = metadata.format;

        var cubemap = TextureUtils.CreateUninitializedCubemap(width, mipCount, format);
        using var texGuard = new TextureCleanupGuard(cubemap);

        if (options.Hint != TextureLoadHint.Synchronous)
        {
            // This will wait until the texture has been uploaded by the graphics
            // and so it won't make a copy if we call SetPixelData.
            await AsyncUtil.RunOnGraphicsThread(() => { });
        }

        var buffer = await dataTask;

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
    }

    static async Task LoadTextureCubemapArray<T>(
        TextureHandleImpl handle,
        TextureLoadOptions options,
        TextureMetadata metadata,
        Task<NativeArray<byte>> dataTask
    )
        where T : Texture
    {
        var arraySize = metadata.arraySize;
        var mipCount = metadata.mipCount;
        var width = metadata.width;
        var height = metadata.height;
        var format = metadata.format;

        var cubeArray = TextureUtils.CreateUninitializedCubemapArray(
            width,
            arraySize / 6,
            mipCount,
            format
        );
        using var texGuard = new TextureCleanupGuard(cubeArray);

        if (options.Hint != TextureLoadHint.Synchronous)
        {
            // This will wait until the texture has been uploaded by the graphics
            // and so it won't make a copy if we call SetPixelData.
            await AsyncUtil.RunOnGraphicsThread(() => { });
        }

        var buffer = await dataTask;

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
    }

    static async Task LoadTexture3D<T>(
        TextureHandleImpl handle,
        TextureLoadOptions options,
        TextureMetadata metadata,
        Task<NativeArray<byte>> dataTask
    )
        where T : Texture
    {
        var arraySize = metadata.arraySize;
        var mipCount = metadata.mipCount;
        var width = metadata.width;
        var height = metadata.height;
        var depth = metadata.arraySize;
        var format = metadata.format;

        var tex3d = TextureUtils.CreateUninitializedTexture3D(
            width,
            height,
            depth,
            mipCount,
            format
        );
        using var texGuard = new TextureCleanupGuard(tex3d);

        if (options.Hint != TextureLoadHint.Synchronous)
        {
            // This will wait until the texture has been uploaded by the graphics
            // and so it won't make a copy if we call SetPixelData.
            await AsyncUtil.RunOnGraphicsThread(() => { });
        }

        var buffer = await dataTask;

        int offset = 0;
        for (int mip = 0; mip < mipCount; ++mip)
        {
            var mipSize = Get3DMipMapSize(width, height, depth, mip, format);

            if (offset + mipSize > buffer.Length)
                throw new Exception("Invalid DDS file: not enough data for specified texture size");

            tex3d.SetPixelData(buffer, mip, offset);
            offset += mipSize;
        }

        tex3d.Apply(false, makeNoLongerReadable: true);
        handle.SetTexture<T>(tex3d, options);
        texGuard.Clear();
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

    internal static unsafe (
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

    static readonly ProfilerMarker ReadFileHeaderMarker = new("ReadFileHeader");
}
