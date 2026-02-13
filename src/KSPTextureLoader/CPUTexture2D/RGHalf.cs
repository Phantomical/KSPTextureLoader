using System;
using KSPTextureLoader.Utils;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    public readonly struct RGHalf : ICPUTexture2D
    {
        const int epp = 2;

        public int Width { get; }
        public int Height { get; }
        public int MipCount { get; }
        public TextureFormat Format => TextureFormat.RGHalf;

        readonly NativeArray<Half> data;

        public RGHalf(NativeArray<byte> data, int width, int height, int mipCount)
        {
            this.data = data.Reinterpret<Half>(sizeof(byte));
            this.Width = width;
            this.Height = height;
            this.MipCount = mipCount;

            int expected = GetTotalSize(in this) * epp;
            if (expected != data.Length)
                throw new Exception(
                    $"data size did not match expected texture size (expected {expected}, but got {data.Length} instead)"
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

            return new Color(r, g, 1f, 1f);
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
