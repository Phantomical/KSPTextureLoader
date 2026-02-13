using System;
using KSPTextureLoader.Utils;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    public readonly struct RGBAHalf : ICPUTexture2D
    {
        const int epp = 4;

        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.RGBAHalf;

        readonly NativeArray<Half> data;

        public RGBAHalf(NativeArray<byte> data, int width, int height, int mipCount)
        {
            this.data = data.Reinterpret<Half>(sizeof(byte));
            this.Width = width;
            this.Height = height;
            this.MipCount = mipCount;

            int expected = GetTotalSize(in this) * epp;
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

            int idx = (p.offset + y * p.width + x) * epp;
            float r = data[idx];
            float g = data[idx + 1];
            float b = data[idx + 2];
            float a = data[idx + 3];

            return new Color(r, g, b, a);
        }

        public Color32 GetPixel32(int x, int y, int mipLevel = 0) => GetPixel(x, y, mipLevel);

        public Color GetPixelBilinear(float u, float v, int mipLevel = 0) =>
            CPUTexture2D.GetPixelBilinear(in this, u, v, mipLevel);

        public unsafe NativeArray<T> GetRawTextureData<T>()
            where T : unmanaged
        {
            return GetNonOwningNativeArray(data).Reinterpret<T>(sizeof(Half));
        }
    }
}
