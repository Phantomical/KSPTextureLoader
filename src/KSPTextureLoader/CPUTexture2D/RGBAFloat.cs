using System;
using System.Runtime.CompilerServices;
using KSPTextureLoader.Burst;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    [BurstCompile]
    public readonly struct RGBAFloat : ICPUTexture2D
    {
        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.RGBAFloat;

        readonly NativeArray<Color> data;

        public unsafe RGBAFloat(NativeArray<byte> data, int width, int height, int mipCount)
        {
            this.data = data.Reinterpret<Color>(sizeof(byte));
            this.Width = width;
            this.Height = height;
            this.MipCount = mipCount;

            int expected = GetTotalSize(in this);
            if (expected != this.data.Length)
                throw new Exception(
                    $"data size did not match expected texture size (expected {expected}, but got {this.data.Length} instead)"
                );
        }

        public Color GetPixel(int x, int y, int mipLevel = 0)
        {
            var p = GetMipProperties(in this, mipLevel);

            x = Mathf.Clamp(x, 0, p.width - 1);
            y = Mathf.Clamp(y, 0, p.height - 1);

            return data[p.offset + y * p.width + x];
        }

        public Color32 GetPixel32(int x, int y, int mipLevel = 0) => GetPixel(x, y, mipLevel);

        public Color GetPixelBilinear(float u, float v, int mipLevel = 0) =>
            CPUTexture2D.GetPixelBilinear(in this, u, v, mipLevel);

        public unsafe NativeArray<T> GetRawTextureData<T>()
            where T : unmanaged
        {
            return GetNonOwningNativeArray(data).Reinterpret<T>(sizeof(Color));
        }

        public NativeArray<Color> GetPixels(int mipLevel = 0, Allocator allocator = Allocator.Temp)
        {
            return GetPixels<RGBAFloat, Color, GetPixelsJob>(
                in this,
                mipLevel,
                allocator,
                (data, pixels) => new GetPixelsJob { data = data, pixels = pixels }
            );
        }

        public NativeArray<Color32> GetPixels32(
            int mipLevel = 0,
            Allocator allocator = Allocator.Temp
        )
        {
            return GetPixels32<RGBAFloat, Color, GetPixels32Job>(
                in this,
                mipLevel,
                allocator,
                (data, pixels) => new GetPixels32Job { data = data, pixels = pixels }
            );
        }

        [BurstCompile]
        struct GetPixelsJob : IJobParallelForBatch
        {
            public NativeArray<Color> data;
            public NativeArray<Color> pixels;

            public void Execute(int start, int count)
            {
                int end = start + count;
                for (int i = start; i < end; ++i)
                    pixels[i] = data[i];
            }
        }

        [BurstCompile]
        struct GetPixels32Job : IJobParallelForBatch
        {
            public NativeArray<Color> data;
            public NativeArray<Color32> pixels;

            public void Execute(int start, int count)
            {
                int end = start + count;
                for (int i = start; i < end; ++i)
                    pixels[i] = (Color32)data[i];
            }
        }
    }
}
