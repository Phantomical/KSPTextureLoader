using System;
using KSPTextureLoader.Burst;
using KSPTextureLoader.Jobs;
using KSPTextureLoader.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    /// <summary>
    /// Kopernicus custom 8-bit palette format: 256-entry RGBA32 palette (1024 bytes)
    /// followed by 8bpp color indices (one pixel per byte).
    /// </summary>
    public readonly struct KopernicusPalette8 : ICPUTexture2D, ICompileToTexture, IGetPixels, IGetPixelsV2
    {
        const int PaletteEntries = 256;
        const int PaletteBytes = PaletteEntries * 4;

        public int Width { get; }
        public int Height { get; }
        public int MipCount => 1;
        public TextureFormat Format => default;

        readonly LargeNativeArray<byte> data;

        public unsafe NativeArray<Color32> Palette
        {
            get
            {
                if (data.Length < PaletteBytes)
                    throw new InvalidOperationException(
                        "referenced data is too small to contain a palette"
                    );

                var palette = (Color32*)data.GetUnsafePtr();
                return NativeArraySafety.Wrap<Color32>(
                    palette,
                    PaletteEntries,
                    Allocator.Invalid
                );
            }
        }

        internal KopernicusPalette8(LargeNativeArray<byte> data, int width, int height)
        {
            this.data = data;
            this.Width = width;
            this.Height = height;

            int expected = PaletteBytes + width * height;
            if (expected != data.Length)
                throw new Exception(
                    $"data size did not match expected texture size (expected {expected}, but got {data.Length} instead)"
                );
        }

        public KopernicusPalette8(NativeArray<byte> data, int width, int height)
            : this((LargeNativeArray<byte>)data, width, height) { }

        public unsafe Color32 GetPixel32(int x, int y, int mipLevel = 0)
        {
            x = Mathf.Clamp(x, 0, Width - 1);
            y = Mathf.Clamp(y, 0, Height - 1);

            Color32* palette = (Color32*)data.GetUnsafePtr();
            int index = data[PaletteBytes + y * Width + x];
            return palette[index];
        }

        public Color GetPixel(int x, int y, int mipLevel = 0) => GetPixel32(x, y, mipLevel);

        public Color GetPixelBilinear(float u, float v, int mipLevel = 0) =>
            CPUTexture2D.GetPixelBilinear(in this, u, v, mipLevel);

        public NativeArray<T> GetRawTextureData<T>()
            where T : unmanaged
        {
            return data.Reinterpret<T>().AsNativeArray();
        }

        public NativeArray<Color> GetPixels(int mipLevel = 0, Allocator allocator = Allocator.Temp)
        {
            var pixels = new NativeArray<Color>(
                Width * Height,
                allocator,
                NativeArrayOptions.UninitializedMemory
            );

            try
            {
                GetPixels(pixels, mipLevel).Complete();
            }
            catch
            {
                pixels.Dispose();
                throw;
            }

            return pixels;
        }

        public NativeArray<Color32> GetPixels32(
            int mipLevel = 0,
            Allocator allocator = Allocator.Temp
        )
        {
            var pixels = new NativeArray<Color32>(
                Width * Height,
                allocator,
                NativeArrayOptions.UninitializedMemory
            );

            try
            {
                GetPixels32(pixels, mipLevel).Complete();
            }
            catch
            {
                pixels.Dispose();
                throw;
            }

            return pixels;
        }

        public JobHandle GetPixels(
            NativeArray<Color> pixels,
            int mipLevel = 0,
            JobHandle dependsOn = default
        )
        {
            ValidatePixels(pixels.Length, mipLevel);

            return new DecodeKopernicusPalette8bitColorJob { data = data, colors = pixels }
                .ScheduleBatch(pixels.Length, 4096, dependsOn);
        }

        public JobHandle GetPixels32(
            NativeArray<Color32> pixels,
            int mipLevel = 0,
            JobHandle dependsOn = default
        )
        {
            ValidatePixels(pixels.Length, mipLevel);

            return new DecodeKopernicusPalette8bitJob { data = data, colors = pixels }
                .ScheduleBatch(pixels.Length, 4096, dependsOn);
        }

        void ValidatePixels(int length, int mipLevel)
        {
            if (mipLevel != 0)
                throw new ArgumentOutOfRangeException(nameof(mipLevel));
            if (length != Width * Height)
                throw new InvalidOperationException("pixel length did not match mipmap size");
        }

        public Texture2D CompileToTexture(bool readable)
        {
            var texture = TextureUtils.CreateUninitializedTexture2D(
                Width,
                Height,
                TextureFormat.RGBA32
            );

#if KSP_RETOOLED
            var data = texture.GetRawTextureData<Color32>();
#else
            using var data = new NativeArray<Color32>(
                Width * Height,
                Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory
            );
#endif

            var job = new DecodeKopernicusPalette8bitJob
            {
                data = this.data.GetSubArray(0, PaletteBytes + Width * Height),
                colors = data,
            };
            var handle = job.ScheduleBatch(Width * Height, 4096);

#if KSP_RETOOLED
            handle.Complete();
#else
            // overlap the job with GetRawTextureData
            JobHandle.ScheduleBatchedJobs();

            var texdata = texture.GetRawTextureData<Color32>();
            handle.Complete();
            data.CopyTo(texdata);
#endif

            texture.Apply(true, !readable);
            return texture;
        }
    }
}
