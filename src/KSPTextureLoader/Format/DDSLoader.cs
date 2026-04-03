using System;
using System.IO;
using System.IO.MemoryMappedFiles;
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
    static FileInfo ReadFileHeader(string diskPath)
    {
        using var scope = ReadFileHeaderMarker.Auto();
        using var file = File.OpenRead(diskPath);

        var br = new BinaryReader(file);
        return ReadFileHeader(br, file.Length);
    }

    static FileInfo ReadFileHeader(BinaryReader br, long fileLength)
    {
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

        long remainingLength = fileLength - fileOffset;

        return new()
        {
            header = header,
            header10 = header10,
            fileLength = remainingLength,
            dataOffset = fileOffset,
        };
    }

    internal static Task<FileInfo> ReadFileHeaderAsync(string diskPath)
    {
        return Task.Run(() => ReadFileHeader(diskPath));
    }
    #endregion

    #region ReadTextureMetadata
    internal enum KopernicusPaletteType
    {
        None,
        Palette4,
        Palette8,
    }

    internal struct TextureMetadata
    {
        public Task<LargeNativeArray<byte>> data;
        public GraphicsFormat format;
        public int width;
        public int height;
        public int depth;
        public int arraySize;
        public int mipCount;
        public DDSTextureType type;
        public KopernicusPaletteType paletteType;
    }

    internal static Task<TextureMetadata> GetTextureMetadata<T>(
        Task<FileInfo> infoTask,
        Task<LargeNativeArray<byte>> dataTask,
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
            var paletteType = KopernicusPaletteType.None;
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
                    // Try using Kopernicus' special palette based formats.
                    // Pass through the raw palette data; LoadTexture will decode
                    // it to RGBA32 before uploading to the GPU.
                    if (header.ddspf.dwRGBBitCount == 4)
                    {
                        var expected = width * height / 2 + 16 * 4;
                        if (info.fileLength != expected)
                            throw new Exception(
                                "Unsupported DDS file: no recognized format (tried 4bpp palette image, but file size was not correct)"
                            );

                        paletteType = KopernicusPaletteType.Palette4;
                    }
                    else if (header.ddspf.dwRGBBitCount == 8)
                    {
                        var expected = width * height + 256 * 4;
                        if (info.fileLength != expected)
                            throw new Exception(
                                "Unsupported DDS file: no recognized format (tried 8bpp palette image, but file size was not correct)"
                            );

                        paletteType = KopernicusPaletteType.Palette8;
                    }
                    else
                    {
                        throw new Exception("Unsupported DDS file: no recognized format");
                    }
                }

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

            if (paletteType == KopernicusPaletteType.None && options.Linear is bool linear)
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
                paletteType = paletteType,
            };
        });
    }

    internal class TaskArrayDisposeGuard(Task<LargeNativeArray<byte>> data) : IDisposable
    {
        public Task<LargeNativeArray<byte>> data = data;

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

    #region LoadTexture
    public static async Task LoadTexture<T>(TextureHandleImpl handle, TextureLoadOptions options)
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
                    length = info.fileLength,
                };
            })
        );
        var metadataTask = GetTextureMetadata<T>(infoTask, dataTask, options);
        dataTask = Task.Run(async () => (await metadataTask).data).Unwrap();

        using var dguard = new TaskArrayDisposeGuard(dataTask);
        var metadata = await metadataTask;

        if (metadata.paletteType != KopernicusPaletteType.None)
        {
            dataTask = DecodePaletteToRGBA32(metadata, dataTask);
            dguard.data = dataTask;

            var format = GraphicsFormat.R8G8B8A8_SRGB;
            if (options.Linear is bool linear)
            {
                var tformat = GraphicsFormatUtility.GetTextureFormat(format);
                format = GraphicsFormatUtility.GetGraphicsFormat(tformat, isSRGB: !linear);
            }
            metadata.format = format;
        }

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

                dguard.data = null;
                await LoadTextureCubemap<T>(handle, options, metadata, dataTask);
                break;

            case DDSTextureType.CubemapArray:
                await LoadTextureCubemapArray<T>(handle, options, metadata, dataTask);
                break;

            case DDSTextureType.Texture3D:
                await LoadTexture3D<T>(handle, options, metadata, dataTask);
                break;

            default:
                throw new NotImplementedException($"Unknown texture type {metadata.type}");
        }
    }

    static Task<LargeNativeArray<byte>> DecodePaletteToRGBA32(
        TextureMetadata metadata,
        Task<LargeNativeArray<byte>> rawDataTask
    )
    {
        int width = metadata.width;
        int height = metadata.height;
        var paletteType = metadata.paletteType;
        return Task.Run(async () =>
        {
            using var rawGuard = new TaskArrayDisposeGuard(rawDataTask);
            var rawData = await rawDataTask;
            var colors = AllocatorUtil.CreateNativeArrayHGlobal<Color32>(
                width * height,
                NativeArrayOptions.UninitializedMemory
            );
            using var colorsGuard = new NativeArrayGuard<Color32>(colors);
            await await AsyncUtil.LaunchMainThreadTask(async () =>
            {
                JobHandle jobHandle = paletteType switch
                {
                    KopernicusPaletteType.Palette4 => new DecodeKopernicusPalette4bitJob
                    {
                        data = rawData.GetSubArray(0, 16 * 4 + width * height / 2),
                        colors = colors,
                    }.ScheduleBatch(width * height / 2, 4096),
                    KopernicusPaletteType.Palette8 => new DecodeKopernicusPalette8bitJob
                    {
                        data = rawData.GetSubArray(0, 256 * 4 + width * height),
                        colors = colors,
                    }.ScheduleBatch(width * height, 4096),
                    _ => throw new InvalidOperationException(
                        $"Unknown palette type: {paletteType}"
                    ),
                };
                JobHandle.ScheduleBatchedJobs();
                return AsyncUtil.WaitFor(jobHandle);
            });
            colorsGuard.array = default;
            return LargeNativeArray<byte>.FromNativeArray(
                colors.Reinterpret<byte>(UnsafeUtility.SizeOf<Color32>())
            );
        });
    }

    static readonly ProfilerMarker LoadTextureDataMarker = new("LoadTextureData");

    static async Task LoadTexture2D<T>(
        TextureHandleImpl handle,
        TextureLoadOptions options,
        TextureMetadata metadata,
        Task<LargeNativeArray<byte>> dataTask
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
            texture.LoadRawTextureData(data.AsNativeArray());
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

                texdata.CopyFrom(data.AsNativeArray());
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
        Task<LargeNativeArray<byte>> dataTask
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

        long offset = 0;
        for (int element = 0; element < arraySize; ++element)
        {
            for (int mip = 0; mip < mipCount; ++mip)
            {
                int mipSize = Get2DMipMapSize(width, height, mip, format);

                if (offset + mipSize > buffer.Length)
                    throw new Exception(
                        "Invalid DDS file: not enough data for specified texture size"
                    );

                var mipData = buffer.GetSubArray(offset, mipSize).AsNativeArray();
                tex2dArray.SetPixelData(mipData, mip, element);
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
        Task<LargeNativeArray<byte>> dataTask
    )
        where T : Texture
    {
        using var dguard = new TaskArrayDisposeGuard(dataTask);
        var arraySize = metadata.arraySize;
        var mipCount = metadata.mipCount;
        var width = metadata.width;
        var height = metadata.height;
        var format = metadata.format;

        if (options.Unreadable && DX11.SupportsAsyncUpload(width, height, format))
        {
            dguard.data = null;
            await DX11.UploadTextureCubemapAsync<T>(handle, options, metadata, dataTask);
            return;
        }

        var cubemap = TextureUtils.CreateUninitializedCubemap(width, mipCount, format);
        using var texGuard = new TextureCleanupGuard(cubemap);

        if (options.Hint != TextureLoadHint.Synchronous)
        {
            // This will wait until the texture has been uploaded by the graphics
            // and so it won't make a copy if we call SetPixelData.
            await AsyncUtil.RunOnGraphicsThread(() => { });
        }

        var buffer = await dataTask;

        if (buffer.Length <= int.MaxValue)
        {
            long offset = 0;
            for (int face = 0; face < 6; ++face)
            {
                for (int mip = 0; mip < mipCount; ++mip)
                {
                    int mipSize = Get2DMipMapSize(width, height, mip, format);

                    if (offset + mipSize > buffer.Length)
                        throw new Exception(
                            "Invalid DDS file: not enough data for specified texture size"
                        );

                    var mipData = buffer.GetSubArray(offset, mipSize).AsNativeArray();
                    cubemap.SetPixelData(mipData, mip, (CubemapFace)face);
                    offset += mipSize;
                }
            }
        }
        else
        {
            // Alternate path for textures larger than 2^32. SetPixelData does its
            // bound-checks using ints and it will throw spurious errors if you
            // try to set data that has an offset > int.MaxValue.
            //
            // This shows up with 16k cubemaps, which are pretty niche but are used
            // by Sol.
            //
            // This case is a workaround to the issue. Graphics.CopyTexture will
            // copy the cpu half of the texture regardless of support, so we can
            // use it to copy the parts we need as we go.

            var staging = TextureUtils.CreateUninitializedTexture2D(
                width,
                height,
                mipCount,
                format
            );
            using var guard = new TextureDisposeGuard(staging);

            if (options.Hint != TextureLoadHint.Synchronous)
            {
                // This will wait until the texture has been uploaded by the graphics
                // and so it won't make a copy if we call SetPixelData.
                await AsyncUtil.RunOnGraphicsThread(() => { });
            }

            var sdata = staging.GetRawTextureData<byte>();
            int mipChainSize = GetFaceMipChainSize(width, height, mipCount, format);
            long offset = 0;

            for (int face = 0; face < 6; ++face)
            {
                if (offset + mipChainSize > buffer.Length)
                    throw new Exception(
                        "Invalid DDS file: not enough data for specified texture size"
                    );

                sdata.CopyFrom(buffer.GetSubArray(offset, mipChainSize).AsNativeArray());
                Graphics.CopyTexture(staging, 0, cubemap, face);
                offset += mipChainSize;
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
        Task<LargeNativeArray<byte>> dataTask
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

        long offset = 0;
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

                var mipData = buffer.GetSubArray(offset, mipSize).AsNativeArray();
                cubeArray.SetPixelData(mipData, mip, (CubemapFace)face, element);
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
        Task<LargeNativeArray<byte>> dataTask
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

        long offset = 0;
        for (int mip = 0; mip < mipCount; ++mip)
        {
            var mipSize = Get3DMipMapSize(width, height, depth, mip, format);

            if (offset + mipSize > buffer.Length)
                throw new Exception("Invalid DDS file: not enough data for specified texture size");

            var mipData = buffer.GetSubArray(offset, mipSize).AsNativeArray();
            tex3d.SetPixelData(mipData, mip);
            offset += mipSize;
        }

        tex3d.Apply(false, makeNoLongerReadable: true);
        handle.SetTexture<T>(tex3d, options);
        texGuard.Clear();
    }
    #endregion

    #region LoadCPUTexture
    static readonly ProfilerMarker LoadCPUTextureMarker = new("LoadCPUTexture");

    sealed class MmapGuard(MmapInfo? info) : IDisposable
    {
        public MmapInfo? info = info;

        public void Dispose()
        {
            info?.Dispose();
            info = null;
        }
    }

    internal static Task<CPUTexture2D> LoadCPUTexture2D(string diskPath, TextureLoadOptions options)
    {
        return Task.Run(async () =>
        {
            using var scope = LoadCPUTextureMarker.Auto();
            var mmap = MapFile(diskPath);
            using var mmapGuard = new MmapGuard(mmap);
            var br = mmap.GetBinaryReader();
            var info = ReadFileHeader(br, mmap.fileLength);

            var length = info.fileLength;

            var mmapData = mmap.GetLargeNativeArray(info.dataOffset, length);
            var dataTask = Task.FromResult(mmapData);
            var infoTask = Task.FromResult(info);

            TextureMetadata metadata;
            LargeNativeArray<byte> data;
            using (new ScopeSuspendGuard(scope))
            {
                metadata = await GetTextureMetadata<UnityEngine.Texture2D>(
                    infoTask,
                    dataTask,
                    options
                );
                data = await metadata.data;
            }

            if (metadata.type != DDSTextureType.Texture2D)
                throw new NotImplementedException(
                    "Only 2D CPU textures are supported at this time"
                );

            if (metadata.paletteType != KopernicusPaletteType.None)
            {
                var factory = new CPU.MemoryMappedTexture2D.Factory(mmap.file, mmap.accessor);
                var texture = metadata.paletteType switch
                {
                    KopernicusPaletteType.Palette4 => factory.CreateTexture2D(
                        new CPUTexture2D.KopernicusPalette4(data, metadata.width, metadata.height)
                    ),
                    KopernicusPaletteType.Palette8 => factory.CreateTexture2D(
                        new CPUTexture2D.KopernicusPalette8(data, metadata.width, metadata.height)
                    ),
                    _ => throw new InvalidOperationException(
                        $"Unknown palette type: {metadata.paletteType}"
                    ),
                };
                mmapGuard.info = null;
                return texture;
            }

            var format = GraphicsFormatUtility.GetTextureFormat(metadata.format);

            var dataNative = data.AsNativeArray();
            bool isSame;
            unsafe
            {
                isSame = data.GetUnsafePtr() == mmapData.GetUnsafePtr();
            }

            if (isSame)
            {
                var texture = CPUTexture2D.Create(
                    mmap.file,
                    mmap.accessor,
                    dataNative,
                    metadata.width,
                    metadata.height,
                    metadata.mipCount,
                    format
                );
                mmapGuard.info = null;
                return texture;
            }
            else
            {
                return CPUTexture2D.Create(
                    dataNative,
                    metadata.width,
                    metadata.height,
                    metadata.mipCount,
                    format
                );
            }
        });
    }

    unsafe struct MmapInfo : IDisposable
    {
        public long fileLength;
        public MemoryMappedFile file;
        public MemoryMappedViewAccessor accessor;
        public byte* pointer;

        public readonly void Dispose()
        {
            if (pointer is not null)
                accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
            accessor?.Dispose();
            file?.Dispose();
        }

        public readonly BinaryReader GetBinaryReader() =>
            new(new UnmanagedMemoryStream(pointer, fileLength, fileLength, FileAccess.Read));

        public readonly NativeArray<byte> GetNativeArray(long offset, int length) =>
            NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(
                pointer + offset,
                length,
                Allocator.Invalid
            );

        public readonly LargeNativeArray<byte> GetLargeNativeArray(long offset, long length) =>
            new(pointer + offset, length, Allocator.Invalid);
    }

    static unsafe MmapInfo MapFile(string diskPath)
    {
        var info = new MmapInfo
        {
            fileLength = new System.IO.FileInfo(diskPath).Length,
            file = MemoryMappedFile.CreateFromFile(
                diskPath,
                FileMode.Open,
                null,
                0,
                MemoryMappedFileAccess.Read
            ),
        };

        try
        {
            info.accessor = info.file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            info.accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref info.pointer);
        }
        catch
        {
            info.Dispose();
            throw;
        }

        return info;
    }
    #endregion

    internal struct FileInfo
    {
        public DDSHeader header;
        public DDSHeaderDX10 header10;
        public long fileLength;
        public long dataOffset;
    }

    static readonly ProfilerMarker ReadFileHeaderMarker = new("ReadFileHeader");
}
