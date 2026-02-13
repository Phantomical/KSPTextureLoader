using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Smooth.Delegates;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    public readonly struct RGB24 : ICPUTexture2D
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Color24
        {
            public byte r;
            public byte g;
            public byte b;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static implicit operator Color32(Color24 c) => new(c.r, c.g, c.b, 255);
        }

        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.RGB24;

        readonly NativeArray<Color24> data;

        public unsafe RGB24(NativeArray<byte> data, int width, int height, int mipCount)
        {
            if (sizeof(Color24) != 3)
                throw new Exception("sizeof(Color24) was not 3");

            this.data = data.Reinterpret<Color24>(sizeof(byte));
            this.Width = width;
            this.Height = height;
            this.MipCount = mipCount;

            int expected = GetTotalSize(in this);
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

            return data[p.offset + y * p.width + x];
        }

        public Color GetPixel(int x, int y, int mipLevel = 0) => GetPixel32(x, y, mipLevel);

        public Color GetPixelBilinear(float u, float v, int mipLevel = 0) =>
            CPUTexture2D.GetPixelBilinear(in this, u, v, mipLevel);

        public unsafe NativeArray<T> GetRawTextureData<T>()
            where T : unmanaged
        {
            return GetNonOwningNativeArray(data).Reinterpret<T>(sizeof(Color24));
        }
    }
}
