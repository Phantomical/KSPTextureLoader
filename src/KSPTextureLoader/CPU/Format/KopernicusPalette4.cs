using System;
using KSPTextureLoader.Burst;
using KSPTextureLoader.Jobs;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    /// <summary>
    /// Kopernicus custom 4-bit palette format: 16-entry RGBA32 palette (64 bytes)
    /// followed by 4bpp color indices (two pixels per byte).
    /// </summary>
    public readonly struct KopernicusPalette4 : ICPUTexture2D, ICompileToTexture, IGetPixels
    {
        const int PaletteEntries = 16;
        const int PaletteBytes = PaletteEntries * 4;

        public int Width { get; }
        public int Height { get; }
        public int MipCount => 1;
        public TextureFormat Format => default;

        readonly NativeArray<byte> data;

        public KopernicusPalette4(NativeArray<byte> data, int width, int height)
        {
            this.data = data;
            this.Width = width;
            this.Height = height;

            int expected = PaletteBytes + width * height / 2;
            if (expected != data.Length)
                throw new Exception(
                    $"data size did not match expected texture size (expected {expected}, but got {data.Length} instead)"
                );
        }

        public unsafe Color32 GetPixel32(int x, int y, int mipLevel = 0)
        {
            x = Mathf.Clamp(x, 0, Width - 1);
            y = Mathf.Clamp(y, 0, Height - 1);

            int pixel = y * Width + x;
            byte packed = data[PaletteBytes + pixel / 2];
            int index = (packed >> (4 * (pixel & 1))) & 0xF;

            Color32* palette = (Color32*)data.GetUnsafePtr();
            return palette[index];
        }

        public Color GetPixel(int x, int y, int mipLevel = 0) => GetPixel32(x, y, mipLevel);

        public Color GetPixelBilinear(float u, float v, int mipLevel = 0) =>
            CPUTexture2D.GetPixelBilinear(in this, u, v, mipLevel);

        public NativeArray<T> GetRawTextureData<T>()
            where T : unmanaged
        {
            return GetNonOwningNativeArray(data).Reinterpret<T>(sizeof(byte));
        }

        public NativeArray<Color> GetPixels(int mipLevel = 0, Allocator allocator = Allocator.Temp)
        {
            using var pixels32 = GetPixels32(mipLevel, Allocator.Temp);
            var result = new NativeArray<Color>(
                pixels32.Length,
                allocator,
                NativeArrayOptions.UninitializedMemory
            );
            for (int i = 0; i < pixels32.Length; i++)
            {
                var c = pixels32[i];
                result[i] = new Color(
                    c.r * Byte2Float,
                    c.g * Byte2Float,
                    c.b * Byte2Float,
                    c.a * Byte2Float
                );
            }
            return result;
        }

        public NativeArray<Color32> GetPixels32(
            int mipLevel = 0,
            Allocator allocator = Allocator.Temp
        )
        {
            int pixelCount = Width * Height;
            var result = new NativeArray<Color32>(
                pixelCount,
                allocator,
                NativeArrayOptions.UninitializedMemory
            );
            new DecodeKopernicusPalette4bitJob { data = this.data, colors = result }
                .ScheduleBatch(pixelCount / 2, 4096)
                .Complete();
            return result;
        }

        public Texture2D CompileToTexture(bool readable)
        {
            var texture = TextureUtils.CreateUninitializedTexture2D(
                Width,
                Height,
                TextureFormat.RGBA32
            );
            var data = new NativeArray<Color32>(
                Width * Height,
                Allocator.Temp,
                NativeArrayOptions.UninitializedMemory
            );
            var job = new DecodeKopernicusPalette4bitJob
            {
                data = GetRawTextureData<byte>().GetSubArray(0, PaletteBytes + Width * Height / 2),
                colors = data,
            };
            var handle = job.ScheduleBatch(Width * Height / 2, 4096);
            JobHandle.ScheduleBatchedJobs();

            var texdata = texture.GetRawTextureData<Color32>();
            handle.Complete();
            data.CopyTo(texdata);

            texture.Apply(true, !readable);
            return texture;
        }
    }
}
