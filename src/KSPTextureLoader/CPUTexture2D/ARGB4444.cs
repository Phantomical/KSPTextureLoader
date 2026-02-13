using System;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    public readonly struct ARGB4444 : ICPUTexture2D
    {
        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.ARGB4444;

        readonly NativeArray<ushort> data;

        public ARGB4444(NativeArray<byte> data, int width, int height, int mipCount)
        {
            this.data = data.Reinterpret<ushort>(sizeof(byte));
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

            ushort pixel = data[p.offset + y * p.width + x];

            float a = ((pixel >> 12) & 0xF) * (1f / 15f);
            float r = ((pixel >> 8) & 0xF) * (1f / 15f);
            float g = ((pixel >> 4) & 0xF) * (1f / 15f);
            float b = (pixel & 0xF) * (1f / 15f);

            return new Color(r, g, b, a);
        }

        public Color32 GetPixel32(int x, int y, int mipLevel = 0) => GetPixel(x, y, mipLevel);

        public Color GetPixelBilinear(float u, float v, int mipLevel = 0) =>
            CPUTexture2D.GetPixelBilinear(in this, u, v, mipLevel);

        public NativeArray<T> GetRawTextureData<T>()
            where T : unmanaged
        {
            return GetNonOwningNativeArray(data).Reinterpret<T>(sizeof(ushort));
        }
    }
}
