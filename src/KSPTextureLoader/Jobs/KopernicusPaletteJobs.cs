using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace KSPTextureLoader.Jobs;

// This one is a custom texture format for Kopernicus.
//
// It has a 16-element RGBA color palette, followed by 4-bpp color indices.
struct DecodeKopernicusPalette4bitJob : IJob
{
    [ReadOnly]
    public NativeArray<byte> data;

    [WriteOnly]
    public NativeSlice<Color32> colors;

    public int pixels;

    public readonly unsafe void Execute()
    {
        Color32* palette = (Color32*)this.data.GetUnsafePtr();
        Color32* colors = (Color32*)this.colors.GetUnsafePtr();
        byte* data = (byte*)palette + sizeof(Color32) * 16;

        for (int i = 0; i < pixels; i += 2)
        {
            colors[i + 0] = palette[data[i / 2] & 0xF];
            colors[i + 1] = palette[data[i / 2] >> 4];
        }
    }
}

// Another custom palette texture format for Kopernicus.
//
// This one has 256 palette entries followed by 8bpp palette pixel indices.
struct DecodeKopernicusPalette8bitJob : IJob
{
    [ReadOnly]
    public NativeArray<byte> data;

    [WriteOnly]
    public NativeSlice<Color32> colors;

    public int pixels;

    public readonly unsafe void Execute()
    {
        Color32* palette = (Color32*)this.data.GetUnsafePtr();
        Color32* colors = (Color32*)this.colors.GetUnsafePtr();
        byte* data = (byte*)palette + sizeof(Color32) * 256;

        for (int i = 0; i < pixels; ++i)
            colors[i] = palette[data[i]];
    }
}
