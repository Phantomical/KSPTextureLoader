using KSPTextureLoader.Burst;
using KSPTextureLoader.Utils;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace KSPTextureLoader.Jobs;

// This one is a custom texture format for Kopernicus.
//
// It has a 16-element RGBA color palette, followed by 4-bpp color indices.
// Each batch item represents one byte (two pixels).
[BurstCompile(FloatMode = FloatMode.Fast)]
struct DecodeKopernicusPalette4bitJob : IJobParallelForBatch
{
    public LargeNativeArray<byte> data;

    [WriteOnly]
    [NativeDisableParallelForRestriction]
    [NativeDisableContainerSafetyRestriction]
    public NativeSlice<Color32> colors;

    public readonly unsafe void Execute(int start, int count)
    {
        Color32* palette = (Color32*)this.data.GetUnsafePtr();
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
[BurstCompile(FloatMode = FloatMode.Fast)]
struct DecodeKopernicusPalette8bitJob : IJobParallelForBatch
{
    public LargeNativeArray<byte> data;

    [WriteOnly]
    [NativeDisableContainerSafetyRestriction]
    public NativeSlice<Color32> colors;

    public readonly unsafe void Execute(int start, int count)
    {
        Color32* palette = (Color32*)this.data.GetUnsafePtr();
        Color32* colors = (Color32*)this.colors.GetUnsafePtr();
        byte* data = (byte*)palette + sizeof(Color32) * 256;

        int end = start + count;
        for (int i = start; i < end; ++i)
            colors[i] = palette[data[i]];
    }
}

// Same as DecodeKopernicusPalette4bitJob but writes Color instead of Color32.
[BurstCompile(FloatMode = FloatMode.Fast)]
struct DecodeKopernicusPalette4bitColorJob : IJobParallelForBatch
{
    const float Byte2Float = 1f / 255f;

    public LargeNativeArray<byte> data;

    [WriteOnly]
    [NativeDisableParallelForRestriction]
    [NativeDisableContainerSafetyRestriction]
    public NativeSlice<Color> colors;

    public readonly unsafe void Execute(int start, int count)
    {
        Color32* palette = (Color32*)this.data.GetUnsafePtr();
        Color* colors = (Color*)this.colors.GetUnsafePtr();
        byte* data = (byte*)palette + sizeof(Color32) * 16;

        int end = start + count;
        for (int i = start; i < end; ++i)
        {
            byte packed = data[i];
            colors[i * 2 + 0] = Convert(palette[packed & 0xF]);
            colors[i * 2 + 1] = Convert(palette[packed >> 4]);
        }
    }

    static Color Convert(Color32 c) =>
        new(c.r * Byte2Float, c.g * Byte2Float, c.b * Byte2Float, c.a * Byte2Float);
}

// Same as DecodeKopernicusPalette8bitJob but writes Color instead of Color32.
[BurstCompile(FloatMode = FloatMode.Fast)]
struct DecodeKopernicusPalette8bitColorJob : IJobParallelForBatch
{
    const float Byte2Float = 1f / 255f;

    public LargeNativeArray<byte> data;

    [WriteOnly]
    [NativeDisableContainerSafetyRestriction]
    public NativeSlice<Color> colors;

    public readonly unsafe void Execute(int start, int count)
    {
        Color32* palette = (Color32*)this.data.GetUnsafePtr();
        Color* colors = (Color*)this.colors.GetUnsafePtr();
        byte* data = (byte*)palette + sizeof(Color32) * 256;

        int end = start + count;
        for (int i = start; i < end; ++i)
        {
            Color32 c = palette[data[i]];
            colors[i] = new Color(
                c.r * Byte2Float,
                c.g * Byte2Float,
                c.b * Byte2Float,
                c.a * Byte2Float
            );
        }
    }
}
