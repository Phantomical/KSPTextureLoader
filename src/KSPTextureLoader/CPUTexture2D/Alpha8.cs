using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    public readonly struct Alpha8 : ICPUTexture2D
    {
        const int bpp = 1;

        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.Alpha8;

        readonly NativeArray<byte> data;

        public Alpha8(NativeArray<byte> data, int width, int height, int mipCount)
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte GetPixelValue(int x, int y, int mipLevel)
        {
            var p = GetMipProperties(in this, mipLevel);

            x = Mathf.Clamp(x, 0, p.width - 1);
            y = Mathf.Clamp(y, 0, p.height - 1);

            return data[p.offset + y * p.width + x];
        }

        public Color32 GetPixel32(int x, int y, int mipLevel = 0)
        {
            var a = GetPixelValue(x, y, mipLevel);
            return new Color32(0, 0, 0, a);
        }

        public Color GetPixel(int x, int y, int mipLevel = 0)
        {
            var a = GetPixelValue(x, y, mipLevel);
            return new Color(0f, 0f, 0f, a * Byte2Float);
        }

        public Color GetPixelBilinear(float u, float v, int mipLevel = 0) =>
            CPUTexture2D.GetPixelBilinear(in this, u, v, mipLevel);

        public NativeArray<T> GetRawTextureData<T>()
            where T : unmanaged
        {
            return GetNonOwningNativeArray(data).Reinterpret<T>(sizeof(byte));
        }
    }
}
