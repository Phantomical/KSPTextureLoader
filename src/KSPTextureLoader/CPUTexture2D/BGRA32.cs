using System;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    public readonly struct BGRA32 : ICPUTexture2D
    {
        const int bpp = 4;

        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.BGRA32;

        readonly NativeArray<byte> data;

        public BGRA32(NativeArray<byte> data, int width, int height, int mipCount)
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
                data[byteIdx + 2],
                data[byteIdx + 1],
                data[byteIdx],
                data[byteIdx + 3]
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
    }
}
