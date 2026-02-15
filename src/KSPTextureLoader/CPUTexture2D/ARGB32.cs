using System;
using KSPTextureLoader.Burst;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    [BurstCompile(FloatMode = FloatMode.Fast)]
    public readonly struct ARGB32 : ICPUTexture2D
    {
        const int bpp = 4;

        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.ARGB32;

        readonly NativeArray<byte> data;

        public ARGB32(NativeArray<byte> data, int width, int height, int mipCount)
        {
            this.data = data;
            this.Width = width;
            this.Height = height;
            this.MipCount = mipCount;

            int expected = GetTotalSize(in this) * bpp;
            if (expected != data.Length)
                throw new Exception(
                    $"data size did not match expected texture size (expected {expected}, but got {data.Length} instead)"
                );
        }

        public Color32 GetPixel32(int x, int y, int mipLevel = 0)
        {
            var p = GetMipProperties(in this, mipLevel);

            x = Mathf.Clamp(x, 0, p.width - 1);
            y = Mathf.Clamp(y, 0, p.height - 1);

            int byteIdx = (p.offset + y * p.width + x) * bpp;
            return new Color32(
                data[byteIdx + 1],
                data[byteIdx + 2],
                data[byteIdx + 3],
                data[byteIdx]
            );
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
            return GetPixels<ARGB32, Color32, GetPixelsJob>(
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
            return GetPixels32<ARGB32, Color32, GetPixels32Job>(
                in this,
                mipLevel,
                allocator,
                (data, pixels) => new GetPixels32Job { data = data, pixels = pixels }
            );
        }

        [BurstCompile(FloatMode = FloatMode.Fast)]
        struct GetPixelsJob : IJobParallelForBatch
        {
            public NativeArray<Color32> data;
            public NativeArray<Color> pixels;

            public void Execute(int start, int count)
            {
                int end = start + count;
                for (int i = start; i < end; ++i)
                {
                    // data is ARGB in memory: .r=A, .g=R, .b=G, .a=B
                    var c = data[i];
                    pixels[i] = new Color(
                        c.g * Byte2Float,
                        c.b * Byte2Float,
                        c.a * Byte2Float,
                        c.r * Byte2Float
                    );
                }
            }
        }

        [BurstCompile(FloatMode = FloatMode.Fast)]
        struct GetPixels32Job : IJobParallelForBatch
        {
            public NativeArray<Color32> data;
            public NativeArray<Color32> pixels;

            public void Execute(int start, int count)
            {
                int end = start + count;
                for (int i = start; i < end; ++i)
                {
                    var c = data[i];
                    pixels[i] = new Color32(c.g, c.b, c.a, c.r);
                }
            }
        }
    }
}
