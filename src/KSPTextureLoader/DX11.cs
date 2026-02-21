using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using KSPTextureLoader.Utils;
using SharpDX;
using SharpDX.Direct3D11;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Direct3D11 = SharpDX.Direct3D11;
using DXGIFormat = SharpDX.DXGI.Format;
using TaskArrayDisposeGuard = KSPTextureLoader.Format.DDSLoader.TaskArrayDisposeGuard;
using TextureMetadata = KSPTextureLoader.Format.DDSLoader.TextureMetadata;

namespace KSPTextureLoader;

internal static class DX11
{
    static readonly ProfilerMarker GetNativeTexturePtr = new("GetNativeTexturePtr");
    static readonly ProfilerMarker CreateExternalTexture = new("CreateExternalTexture");
    static readonly ProfilerMarker CreateDX11Texture = new("CreateDX11Texture");
    static readonly ProfilerMarker CopyTextureData = new("CopyTextureData");

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

    internal static async Task UploadTexture2DAsync<T>(
        TextureHandleImpl handle,
        TextureLoadOptions options,
        TextureMetadata metadata,
        Task<NativeArray<byte>> dataTask
    )
        where T : Texture
    {
        using var dguard = new TaskArrayDisposeGuard(dataTask);

        if (GetDxgiFormat(metadata.format) is not DXGIFormat dx11format)
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

        bool readable = TextureLoader.Texture2DShouldBeReadable<T>(options);
        var reftex = Dx11Texture;
        var device = reftex.Device;

        var task = Task.Run(async () =>
        {
            var buffer = await dataTask;

            using var scope = CreateDX11Texture.Auto();

            var desc = new Texture2DDescription
            {
                Width = metadata.width,
                Height = metadata.height,
                MipLevels = metadata.mipCount,
                ArraySize = 1,
                Format = dx11format,
                SampleDescription = new(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            };

            var boxes = FillInitData(metadata, buffer);
            return new Direct3D11.Texture2D(device, desc, boxes);
        });

        using var texguard = new Dx11AsyncTextureGuard(task);
        dguard.AddDependency(task);

        UnityEngine.Texture2D texture = null;

        if (readable)
        {
            texture = TextureUtils.CreateUninitializedTexture2D(
                metadata.width,
                metadata.height,
                metadata.mipCount,
                metadata.format
            );
            using var uguard = new TextureDisposeGuard(texture);

            if (options.Hint != TextureLoadHint.Synchronous)
                await AsyncUtil.RunOnGraphicsThread(static () => { });

            var rawdata = texture.GetRawTextureData<byte>();
            var copy = Task.Run(async () =>
            {
                var data = await dataTask;
                if (rawdata.Length != data.Length)
                    throw new Exception("loaded file data did not match texture data length");
                rawdata.CopyFrom(data);
            });

            dguard.AddDependency(copy);
            dguard.Dispose();

            var dx11texture = await task;
            texture.UpdateExternalTexture(dx11texture.NativePointer);
            texguard.task = null;

            uguard.Clear();
            await copy;
        }
        else
        {
            dguard.Dispose();
            var dx11texture = await task;
            texture = TextureUtils.CreateExternalTexture2D(
                metadata.width,
                metadata.height,
                metadata.mipCount,
                metadata.format,
                dx11texture.NativePointer
            );
            texguard.task = null;
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

        if (!readable)
            TextureUtils.MarkExternalTextureAsUnreadable(texture);

        handle.SetTexture<T>(texture, options);
    }

    static unsafe DataBox[] FillInitData(TextureMetadata metadata, NativeArray<byte> buffer)
    {
        int w = metadata.width;
        int h = metadata.height;
        var boxes = new DataBox[metadata.mipCount];

        var blockWidth = (int)GraphicsFormatUtility.GetBlockWidth(metadata.format);
        var blockHeight = (int)GraphicsFormatUtility.GetBlockHeight(metadata.format);
        var blockSize = (int)GraphicsFormatUtility.GetBlockSize(metadata.format);

        var data = (byte*)buffer.GetUnsafePtr();
        var offset = 0;

        for (int m = 0; m < metadata.mipCount; ++m)
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

        return boxes;
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

    class Dx11TextureGuard(Direct3D11.Texture2D texture = null) : IDisposable
    {
        public Direct3D11.Texture2D texture = texture;

        public void Dispose()
        {
            texture?.Dispose();
        }
    }

    class Dx11AsyncTextureGuard(Task<Direct3D11.Texture2D> task = null) : IDisposable
    {
        public Task<Direct3D11.Texture2D> task = task;

        public void Dispose()
        {
            Task.Run(async () =>
            {
                try
                {
                    var texture = await task;
                    texture.Dispose();
                }
                catch { }
            });
        }
    }
}
