using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using KSPTextureLoader.Utils;
using SharpDX;
using SharpDX.Direct3D11;
using Smooth.Pools;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Direct3D11 = SharpDX.Direct3D11;
using DXGIFormat = SharpDX.DXGI.Format;

namespace KSPTextureLoader;

internal static unsafe class DX11
{
    static readonly ProfilerMarker GetNativeTexturePtr = new("GetNativeTexturePtr");
    static readonly ProfilerMarker CreateExternalTexture = new("CreateExternalTexture");
    static readonly ProfilerMarker CreateDX11Texture = new("CreateDX11Texture");

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
        IFileReadStatus readStatus,
        JobCompleteGuard jobGuard
    )
        where T : Texture
    {
        if (GetDxgiFormat(format) is not DXGIFormat dx11format)
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

        var fileTask = AsyncUtil.WaitFor(jobGuard.JobHandle);
        var task = Task.Run(async () =>
        {
            var buffer = bufGuard.array;
            using var guard = new NativeArrayAsyncGuard<byte>(buffer);
            bufGuard.array = default;

            await fileTask;
            readStatus.ThrowIfError();

            using var scope = CreateDX11Texture.Auto();

            var desc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = mipCount,
                ArraySize = 1,
                Format = dx11format,
                SampleDescription = new(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            };

            var boxes = new DataBox[mipCount];
            FillInitData(width, height, mipCount, format, buffer, boxes);

            return new Direct3D11.Texture2D(device, desc, boxes);
        });

        using (handle.WithCompleteHandler(new TaskCompleteHandler(task)))
            yield return new WaitUntil(() => task.IsCompleted);

        var dx11texture = task.Result;
        if (dx11texture is null)
            throw new Exception("Job failed to create the texture but didn't throw an exception");

        UnityEngine.Texture2D texture;

        try
        {
            using var scope = CreateExternalTexture.Auto();
            texture = TextureUtils.CreateExternalTexture2D(
                width,
                height,
                mipCount,
                format,
                dx11texture.NativePointer
            );
        }
        catch
        {
            dx11texture.Dispose();
            throw;
        }

        // Unity doesn't configure anisotropic filtering for external textures
        // by default. Enable it if configured by default.
        //
        // 9 seems to match what is picked when loading textures via Texture2D
        // normally in RenderDoc.
        switch (Texture.anisotropicFiltering)
        {
            case AnisotropicFiltering.ForceEnable:
            case AnisotropicFiltering.Enable:
                texture.anisoLevel = 9;
                break;
        }

        TextureUtils.MarkExternalTextureAsUnreadable(texture);

        handle.SetTexture<T>(texture, options);
    }

    static void FillInitData(
        int width,
        int height,
        int mipCount,
        GraphicsFormat graphicsFormat,
        NativeArray<byte> buffer,
        DataBox[] boxes
    )
    {
        int w = width;
        int h = height;

        var blockWidth = (int)GraphicsFormatUtility.GetBlockWidth(graphicsFormat);
        var blockHeight = (int)GraphicsFormatUtility.GetBlockHeight(graphicsFormat);
        var blockSize = (int)GraphicsFormatUtility.GetBlockSize(graphicsFormat);

        var data = (byte*)buffer.GetUnsafePtr();
        var offset = 0;

        for (int m = 0; m < mipCount; ++m)
        {
            var rowbytes = DivCeil(w, blockWidth) * blockSize;
            var allbytes = DivCeil(h, blockHeight) * rowbytes;

            boxes[m] = new DataBox((IntPtr)(data + offset), rowbytes, allbytes);
            offset += allbytes;

            if (offset > buffer.Length)
                throw new IndexOutOfRangeException(
                    "image buffer was too small for specified image dimensions and mipmaps"
                );

            w >>= 1;
            h >>= 1;

            if (w < 1)
                w = 1;
            if (h < 1)
                h = 1;
        }
    }

    static int DivCeil(int x, int y) => (x + (y - 1)) / y;

    static DXGIFormat? GetDxgiFormat(GraphicsFormat format)
    {
        return format switch
        {
            // 8-bit single channel
            GraphicsFormat.R8_UNorm => DXGIFormat.R8_UNorm,
            GraphicsFormat.R8_UInt => DXGIFormat.R8_UInt,
            GraphicsFormat.R8_SNorm => DXGIFormat.R8_SNorm,
            GraphicsFormat.R8_SInt => DXGIFormat.R8_SInt,

            // 8-bit dual channel
            GraphicsFormat.R8G8_UNorm => DXGIFormat.R8G8_UNorm,
            GraphicsFormat.R8G8_UInt => DXGIFormat.R8G8_UInt,
            GraphicsFormat.R8G8_SNorm => DXGIFormat.R8G8_SNorm,
            GraphicsFormat.R8G8_SInt => DXGIFormat.R8G8_SInt,

            // 8-bit quad channel RGBA
            GraphicsFormat.R8G8B8A8_UNorm => DXGIFormat.R8G8B8A8_UNorm,
            GraphicsFormat.R8G8B8A8_SRGB => DXGIFormat.R8G8B8A8_UNorm_SRgb,
            GraphicsFormat.R8G8B8A8_UInt => DXGIFormat.R8G8B8A8_UInt,
            GraphicsFormat.R8G8B8A8_SNorm => DXGIFormat.R8G8B8A8_SNorm,
            GraphicsFormat.R8G8B8A8_SInt => DXGIFormat.R8G8B8A8_SInt,

            // 8-bit quad channel BGRA
            GraphicsFormat.B8G8R8A8_UNorm => DXGIFormat.B8G8R8A8_UNorm,
            GraphicsFormat.B8G8R8A8_SRGB => DXGIFormat.B8G8R8A8_UNorm_SRgb,

            // 16-bit single channel
            GraphicsFormat.R16_UNorm => DXGIFormat.R16_UNorm,
            GraphicsFormat.R16_UInt => DXGIFormat.R16_UInt,
            GraphicsFormat.R16_SNorm => DXGIFormat.R16_SNorm,
            GraphicsFormat.R16_SInt => DXGIFormat.R16_SInt,
            GraphicsFormat.R16_SFloat => DXGIFormat.R16_Float,

            // 16-bit dual channel
            GraphicsFormat.R16G16_UNorm => DXGIFormat.R16G16_UNorm,
            GraphicsFormat.R16G16_UInt => DXGIFormat.R16G16_UInt,
            GraphicsFormat.R16G16_SNorm => DXGIFormat.R16G16_SNorm,
            GraphicsFormat.R16G16_SInt => DXGIFormat.R16G16_SInt,
            GraphicsFormat.R16G16_SFloat => DXGIFormat.R16G16_Float,

            // 16-bit quad channel
            GraphicsFormat.R16G16B16A16_UNorm => DXGIFormat.R16G16B16A16_UNorm,
            GraphicsFormat.R16G16B16A16_UInt => DXGIFormat.R16G16B16A16_UInt,
            GraphicsFormat.R16G16B16A16_SNorm => DXGIFormat.R16G16B16A16_SNorm,
            GraphicsFormat.R16G16B16A16_SInt => DXGIFormat.R16G16B16A16_SInt,
            GraphicsFormat.R16G16B16A16_SFloat => DXGIFormat.R16G16B16A16_Float,

            // 32-bit single channel
            GraphicsFormat.R32_UInt => DXGIFormat.R32_UInt,
            GraphicsFormat.R32_SInt => DXGIFormat.R32_SInt,
            GraphicsFormat.R32_SFloat => DXGIFormat.R32_Float,

            // 32-bit dual channel
            GraphicsFormat.R32G32_UInt => DXGIFormat.R32G32_UInt,
            GraphicsFormat.R32G32_SInt => DXGIFormat.R32G32_SInt,
            GraphicsFormat.R32G32_SFloat => DXGIFormat.R32G32_Float,

            // 32-bit triple channel
            GraphicsFormat.R32G32B32_UInt => DXGIFormat.R32G32B32_UInt,
            GraphicsFormat.R32G32B32_SInt => DXGIFormat.R32G32B32_SInt,
            GraphicsFormat.R32G32B32_SFloat => DXGIFormat.R32G32B32_Float,

            // 32-bit quad channel
            GraphicsFormat.R32G32B32A32_UInt => DXGIFormat.R32G32B32A32_UInt,
            GraphicsFormat.R32G32B32A32_SInt => DXGIFormat.R32G32B32A32_SInt,
            GraphicsFormat.R32G32B32A32_SFloat => DXGIFormat.R32G32B32A32_Float,

            // Packed formats
            GraphicsFormat.R5G6B5_UNormPack16 => DXGIFormat.B5G6R5_UNorm,
            GraphicsFormat.B5G6R5_UNormPack16 => DXGIFormat.B5G6R5_UNorm,
            GraphicsFormat.B5G5R5A1_UNormPack16 => DXGIFormat.B5G5R5A1_UNorm,
            GraphicsFormat.B4G4R4A4_UNormPack16 => DXGIFormat.B4G4R4A4_UNorm,
            GraphicsFormat.A2B10G10R10_UNormPack32 => DXGIFormat.R10G10B10A2_UNorm,
            GraphicsFormat.A2B10G10R10_UIntPack32 => DXGIFormat.R10G10B10A2_UInt,
            GraphicsFormat.B10G11R11_UFloatPack32 => DXGIFormat.R11G11B10_Float,
            GraphicsFormat.E5B9G9R9_UFloatPack32 => DXGIFormat.R9G9B9E5_Sharedexp,

            // Block compression formats (BC/DXT)
            GraphicsFormat.RGBA_DXT1_SRGB => DXGIFormat.BC1_UNorm_SRgb,
            GraphicsFormat.RGBA_DXT1_UNorm => DXGIFormat.BC1_UNorm,
            GraphicsFormat.RGBA_DXT3_SRGB => DXGIFormat.BC2_UNorm_SRgb,
            GraphicsFormat.RGBA_DXT3_UNorm => DXGIFormat.BC2_UNorm,
            GraphicsFormat.RGBA_DXT5_SRGB => DXGIFormat.BC3_UNorm_SRgb,
            GraphicsFormat.RGBA_DXT5_UNorm => DXGIFormat.BC3_UNorm,
            GraphicsFormat.R_BC4_UNorm => DXGIFormat.BC4_UNorm,
            GraphicsFormat.R_BC4_SNorm => DXGIFormat.BC4_SNorm,
            GraphicsFormat.RG_BC5_UNorm => DXGIFormat.BC5_UNorm,
            GraphicsFormat.RG_BC5_SNorm => DXGIFormat.BC5_SNorm,
            GraphicsFormat.RGB_BC6H_UFloat => DXGIFormat.BC6H_Uf16,
            GraphicsFormat.RGB_BC6H_SFloat => DXGIFormat.BC6H_Sf16,
            GraphicsFormat.RGBA_BC7_SRGB => DXGIFormat.BC7_UNorm_SRgb,
            GraphicsFormat.RGBA_BC7_UNorm => DXGIFormat.BC7_UNorm,

            _ => null,
        };
    }
}
