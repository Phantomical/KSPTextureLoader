using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using KSPTextureLoader.Utils;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using VehiclePhysics;
using Direct3D11 = SharpDX.Direct3D11;

namespace KSPTextureLoader;

internal static unsafe class DX11
{
    static readonly ProfilerMarker GetNativeTexturePtr = new("GetNativeTexturePtr");
    static readonly ProfilerMarker CreateExternalTexture = new("CreateExternalTexture");

    static UnityEngine.Texture2D ReferenceTexture = null;
    static uint LastUpdateCount = uint.MaxValue;
    static Direct3D11.Texture2D Dx11Texture;

    internal static bool SupportsAsyncUpload(int width, int height, GraphicsFormat format)
    {
        if (!Config.Instance.AllowNativeUploads)
            return false;

        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
            return false;

        // If unity doesn't support this then it is likely that the device is
        // created in a way that would prevent us from creating textures in a job.
        if (!Texture.allowThreadedTextureCreation)
            return false;

        // Feeding a texture that is not a multiple of the block size will cause
        // DX11 to error, so we let unity take care of figuring that out.
        if (GraphicsFormatUtility.IsCompressedFormat(format))
        {
            var blockWidth = (int)GraphicsFormatUtility.GetBlockWidth(format);
            var blockHeight = (int)GraphicsFormatUtility.GetBlockHeight(format);

            if (width % blockWidth != 0 || height % blockHeight != 0)
                return false;
        }

        return GetDxgiFormat(format) is not null;
    }

    internal static IEnumerable<object> UploadTexture2D<T>(
        TextureHandleImpl handle,
        int width,
        int height,
        int mipCount,
        GraphicsFormat format,
        TextureLoadOptions options,
        NativeArrayGuard<byte> bufGuard,
        SafeReadHandleGuard readGuard
    )
        where T : Texture
    {
        if (GetDxgiFormat(format) is not Format dx11format)
            throw new NotSupportedException(
                "DX11.UploadTexture2D called with a graphics format not natively supported by DX11"
            );

        if (ReferenceTexture == null)
        {
            ReferenceTexture = new UnityEngine.Texture2D(1, 1);
            LastUpdateCount = uint.MaxValue;
        }

        var updateCount = ReferenceTexture.updateCount;
        if (LastUpdateCount != updateCount)
        {
            using var nativePtrScope = GetNativeTexturePtr.Auto();

            LastUpdateCount = updateCount;
            Dx11Texture = new Direct3D11.Texture2D(ReferenceTexture.GetNativeTexturePtr());
        }

        var reftex = Dx11Texture;
        var device = reftex.Device;
        using var shared = new SharedData();

        var job = new CreateTexture2DJob
        {
            buffer = bufGuard.array,
            width = width,
            height = height,
            mipCount = mipCount,
            graphicsFormat = format,
            format = dx11format,
            handle = new(shared),
            device = device.NativePointer,
            readHandle = readGuard.Handle,
        };
        readGuard.JobHandle = job.Schedule(readGuard.JobHandle);
        bufGuard.array.Dispose(readGuard.JobHandle);
        bufGuard.array = default;
        JobHandle.ScheduleBatchedJobs();

        using (handle.WithCompleteHandler(new JobHandleCompleteHandler(readGuard.JobHandle)))
            yield return new WaitUntil(() => readGuard.JobHandle.IsCompleted);

        if (readGuard.Status != ReadStatus.Complete)
            throw new Exception("Failed to read file data");

        // If the job failed with an exception then we should rethrow that.
        shared.ex?.Throw();

        if (shared.texture is null)
            throw new Exception("Job failed to create the texture but didn't throw an exception");

        UnityEngine.Texture2D texture;
        using (CreateExternalTexture.Auto())
            texture = TextureUtils.CreateExternalTexture2D(
                width,
                height,
                mipCount,
                format,
                shared.texture.NativePointer
            );

        shared.texture = null;

        handle.SetTexture<T>(texture, options);
    }

    class SharedData : IDisposable
    {
        public Direct3D11.Texture2D texture = null;
        public ExceptionDispatchInfo ex = null;

        public void Dispose()
        {
            texture?.Dispose();
            texture = null;
            GC.SuppressFinalize(this);
        }

        ~SharedData()
        {
            texture?.Dispose();
        }
    }

    struct CreateTexture2DJob : IJob
    {
        [ReadOnly]
        public NativeArray<byte> buffer;

        public int width;
        public int height;
        public int mipCount;
        public GraphicsFormat graphicsFormat;
        public Format format;

        public ObjectHandle<SharedData> handle;
        public IntPtr device;
        public ReadHandle readHandle;

        public void Execute()
        {
            using var guard = handle;
            using var device = new Direct3D11.Device(this.device);
            var shared = handle.Target;

            try
            {
                ExecuteImpl(device, shared);
            }
            catch (Exception ex)
            {
                shared.ex = ExceptionDispatchInfo.Capture(ex);
            }
        }

        void ExecuteImpl(Direct3D11.Device device, SharedData shared)
        {
            if (readHandle.Status != ReadStatus.Complete)
                return;

            var desc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = mipCount,
                ArraySize = 1,
                Format = format,
                SampleDescription = new(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            };

            var blockWidth = (int)GraphicsFormatUtility.GetBlockWidth(graphicsFormat);
            var blockHeight = (int)GraphicsFormatUtility.GetBlockHeight(graphicsFormat);
            var blockSize = (int)GraphicsFormatUtility.GetBlockSize(graphicsFormat);

            var boxes = new DataBox[mipCount];
            var data = (byte*)buffer.GetUnsafePtr();
            var offset = 0;

            var mipWidth = width;
            var mipHeight = height;

            for (int i = 0; i < mipCount; ++i)
            {
                var rowPitch = DivCeil(mipWidth, blockWidth) * blockSize;
                var mipSize = rowPitch * DivCeil(mipHeight, blockHeight);

                if (offset + mipSize > buffer.Length)
                    throw new Exception("loaded data was too small for texture size");

                boxes[i] = new DataBox((IntPtr)(data + offset), rowPitch, mipSize);
                offset += mipSize;

                mipWidth >>= 1;
                mipHeight >>= 1;
            }

            shared.texture = new Direct3D11.Texture2D(device, desc, boxes);
        }

        static int DivCeil(int x, int y) => (x + y - 1) / y;
    }

    static Format? GetDxgiFormat(GraphicsFormat format)
    {
        return format switch
        {
            // 8-bit single channel
            GraphicsFormat.R8_UNorm => Format.R8_UNorm,
            GraphicsFormat.R8_UInt => Format.R8_UInt,
            GraphicsFormat.R8_SNorm => Format.R8_SNorm,
            GraphicsFormat.R8_SInt => Format.R8_SInt,

            // 8-bit dual channel
            GraphicsFormat.R8G8_UNorm => Format.R8G8_UNorm,
            GraphicsFormat.R8G8_UInt => Format.R8G8_UInt,
            GraphicsFormat.R8G8_SNorm => Format.R8G8_SNorm,
            GraphicsFormat.R8G8_SInt => Format.R8G8_SInt,

            // 8-bit quad channel RGBA
            GraphicsFormat.R8G8B8A8_UNorm => Format.R8G8B8A8_UNorm,
            GraphicsFormat.R8G8B8A8_SRGB => Format.R8G8B8A8_UNorm_SRgb,
            GraphicsFormat.R8G8B8A8_UInt => Format.R8G8B8A8_UInt,
            GraphicsFormat.R8G8B8A8_SNorm => Format.R8G8B8A8_SNorm,
            GraphicsFormat.R8G8B8A8_SInt => Format.R8G8B8A8_SInt,

            // 8-bit quad channel BGRA
            GraphicsFormat.B8G8R8A8_UNorm => Format.B8G8R8A8_UNorm,
            GraphicsFormat.B8G8R8A8_SRGB => Format.B8G8R8A8_UNorm_SRgb,

            // 16-bit single channel
            GraphicsFormat.R16_UNorm => Format.R16_UNorm,
            GraphicsFormat.R16_UInt => Format.R16_UInt,
            GraphicsFormat.R16_SNorm => Format.R16_SNorm,
            GraphicsFormat.R16_SInt => Format.R16_SInt,
            GraphicsFormat.R16_SFloat => Format.R16_Float,

            // 16-bit dual channel
            GraphicsFormat.R16G16_UNorm => Format.R16G16_UNorm,
            GraphicsFormat.R16G16_UInt => Format.R16G16_UInt,
            GraphicsFormat.R16G16_SNorm => Format.R16G16_SNorm,
            GraphicsFormat.R16G16_SInt => Format.R16G16_SInt,
            GraphicsFormat.R16G16_SFloat => Format.R16G16_Float,

            // 16-bit quad channel
            GraphicsFormat.R16G16B16A16_UNorm => Format.R16G16B16A16_UNorm,
            GraphicsFormat.R16G16B16A16_UInt => Format.R16G16B16A16_UInt,
            GraphicsFormat.R16G16B16A16_SNorm => Format.R16G16B16A16_SNorm,
            GraphicsFormat.R16G16B16A16_SInt => Format.R16G16B16A16_SInt,
            GraphicsFormat.R16G16B16A16_SFloat => Format.R16G16B16A16_Float,

            // 32-bit single channel
            GraphicsFormat.R32_UInt => Format.R32_UInt,
            GraphicsFormat.R32_SInt => Format.R32_SInt,
            GraphicsFormat.R32_SFloat => Format.R32_Float,

            // 32-bit dual channel
            GraphicsFormat.R32G32_UInt => Format.R32G32_UInt,
            GraphicsFormat.R32G32_SInt => Format.R32G32_SInt,
            GraphicsFormat.R32G32_SFloat => Format.R32G32_Float,

            // 32-bit triple channel
            GraphicsFormat.R32G32B32_UInt => Format.R32G32B32_UInt,
            GraphicsFormat.R32G32B32_SInt => Format.R32G32B32_SInt,
            GraphicsFormat.R32G32B32_SFloat => Format.R32G32B32_Float,

            // 32-bit quad channel
            GraphicsFormat.R32G32B32A32_UInt => Format.R32G32B32A32_UInt,
            GraphicsFormat.R32G32B32A32_SInt => Format.R32G32B32A32_SInt,
            GraphicsFormat.R32G32B32A32_SFloat => Format.R32G32B32A32_Float,

            // Packed formats
            GraphicsFormat.R5G6B5_UNormPack16 => Format.B5G6R5_UNorm,
            GraphicsFormat.B5G6R5_UNormPack16 => Format.B5G6R5_UNorm,
            GraphicsFormat.B5G5R5A1_UNormPack16 => Format.B5G5R5A1_UNorm,
            GraphicsFormat.B4G4R4A4_UNormPack16 => Format.B4G4R4A4_UNorm,
            GraphicsFormat.A2B10G10R10_UNormPack32 => Format.R10G10B10A2_UNorm,
            GraphicsFormat.A2B10G10R10_UIntPack32 => Format.R10G10B10A2_UInt,
            GraphicsFormat.B10G11R11_UFloatPack32 => Format.R11G11B10_Float,
            GraphicsFormat.E5B9G9R9_UFloatPack32 => Format.R9G9B9E5_Sharedexp,

            // Block compression formats (BC/DXT)
            GraphicsFormat.RGBA_DXT1_SRGB => Format.BC1_UNorm_SRgb,
            GraphicsFormat.RGBA_DXT1_UNorm => Format.BC1_UNorm,
            GraphicsFormat.RGBA_DXT3_SRGB => Format.BC2_UNorm_SRgb,
            GraphicsFormat.RGBA_DXT3_UNorm => Format.BC2_UNorm,
            GraphicsFormat.RGBA_DXT5_SRGB => Format.BC3_UNorm_SRgb,
            GraphicsFormat.RGBA_DXT5_UNorm => Format.BC3_UNorm,
            GraphicsFormat.R_BC4_UNorm => Format.BC4_UNorm,
            GraphicsFormat.R_BC4_SNorm => Format.BC4_SNorm,
            GraphicsFormat.RG_BC5_UNorm => Format.BC5_UNorm,
            GraphicsFormat.RG_BC5_SNorm => Format.BC5_SNorm,
            GraphicsFormat.RGB_BC6H_UFloat => Format.BC6H_Uf16,
            GraphicsFormat.RGB_BC6H_SFloat => Format.BC6H_Sf16,
            GraphicsFormat.RGBA_BC7_SRGB => Format.BC7_UNorm_SRgb,
            GraphicsFormat.RGBA_BC7_UNorm => Format.BC7_UNorm,

            _ => null,
        };
    }
}
