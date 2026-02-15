using KSPTextureLoader.Burst;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace KSPTextureLoader.Jobs;

// This one is a custom texture format for Kopernicus.
//
// It has a 16-element RGBA color palette, followed by 4-bpp color indices.
// Each batch item represents one byte (two pixels).
[BurstCompile]
struct DecodeKopernicusPalette4bitJob : IJobParallelForBatch
{
    [ReadOnly]
    public NativeArray<byte> data;

    [WriteOnly]
    [NativeDisableParallelForRestriction]
    public NativeSlice<Color32> colors;

    public readonly unsafe void Execute(int start, int count)
    {
        Color32* palette = (Color32*)this.data.GetUnsafeReadOnlyPtr();
        Color32* colors = (Color32*)this.colors.GetUnsafePtr();
        byte* data = (byte*)palette + sizeof(Color32) * 16;

        int end = start + count;
        for (int i = start; i < end; ++i)
        {
            byte packed = data[i];
            colors[i * 2 + 0] = palette[packed & 0xF];
            colors[i * 2 + 1] = palette[packed >> 4];
        }
    }
}

// Another custom palette texture format for Kopernicus.
//
// This one has 256 palette entries followed by 8bpp palette pixel indices.
[BurstCompile]
struct DecodeKopernicusPalette8bitJob : IJobParallelForBatch
{
    [ReadOnly]
    public NativeArray<byte> data;

    [WriteOnly]
    public NativeSlice<Color32> colors;

    public readonly unsafe void Execute(int start, int count)
    {
        Color32* palette = (Color32*)this.data.GetUnsafeReadOnlyPtr();
        Color32* colors = (Color32*)this.colors.GetUnsafePtr();
        byte* data = (byte*)palette + sizeof(Color32) * 256;

        int end = start + count;
        for (int i = start; i < end; ++i)
            colors[i] = palette[data[i]];
    }
}
