using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace KSPTextureLoader;

partial class CPUTexture2D
{
    /// <summary>
    /// Kopernicus custom 8-bit palette format: 256-entry RGBA32 palette (1024 bytes)
    /// followed by 8bpp color indices (one pixel per byte).
    /// </summary>
    public readonly struct KopernicusPalette8 : ICPUTexture2D
    {
        const int PaletteEntries = 256;
        const int PaletteBytes = PaletteEntries * 4;

        public int Width { get; }
        public int Height { get; }
        public int MipCount => 1;
        public TextureFormat Format => TextureFormat.RGBA32;

        readonly NativeArray<byte> data;

        public KopernicusPalette8(NativeArray<byte> data, int width, int height)
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
            return GetNonOwningNativeArray(data).Reinterpret<T>(sizeof(byte));
        }
    }
}
