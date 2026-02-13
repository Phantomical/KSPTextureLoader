using System;
using KSP.Testing;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class DXT1Tests : CPUTexture2DTests
{
    static ushort EncodeRGB565(int r5, int g6, int b5) => (ushort)((r5 << 11) | (g6 << 5) | b5);

    static byte[] MakeDXT1Block(ushort c0, ushort c1, byte row0, byte row1, byte row2, byte row3)
    {
        return
        [
            (byte)(c0 & 0xFF),
            (byte)(c0 >> 8),
            (byte)(c1 & 0xFF),
            (byte)(c1 >> 8),
            row0,
            row1,
            row2,
            row3,
        ];
    }

    static byte MakeIndexRow(int i0, int i1, int i2, int i3) =>
        (byte)(i0 | (i1 << 2) | (i2 << 4) | (i3 << 6));

    /// <summary>
    /// Opaque mode (c0 > c1): 4-color palette with 1/3 and 2/3 interpolation.
    /// Each row uses a single index (0–3) so all four palette entries are tested.
    /// </summary>
    [TestInfo("CPUTexture2D_DXT1_Opaque")]
    public void TestDXT1OpaqueMode()
    {
        // c0 = pure red, c1 = pure blue; c0 (0xF800) > c1 (0x001F) → opaque mode
        ushort c0 = EncodeRGB565(31, 0, 0);
        ushort c1 = EncodeRGB565(0, 0, 31);

        var block = MakeDXT1Block(
            c0,
            c1,
            MakeIndexRow(0, 0, 0, 0), // row 0: color0
            MakeIndexRow(1, 1, 1, 1), // row 1: color1
            MakeIndexRow(2, 2, 2, 2), // row 2: 2/3*c0 + 1/3*c1
            MakeIndexRow(3, 3, 3, 3) // row 3: 1/3*c0 + 2/3*c1
        );

        var tex = CreateTexture(4, 4, TextureFormat.DXT1, block);
        try
        {
            var rawData = tex.GetRawTextureData<byte>();
            var cpu = new CPUTexture2D.DXT1(rawData, 4, 4, 1);

            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                Color expected = tex.GetPixel(x, y);
                Color actual = cpu.GetPixel(x, y);
                assertColorEquals($"DXT1_Opaque({x},{y})", actual, expected, 0.02f);
            }

            // All pixels in opaque mode should be fully opaque
            for (int y = 0; y < 4; y++)
                assertFloatEquals($"DXT1_Opaque_Alpha(row{y})", cpu.GetPixel(0, y).a, 1f, 0.001f);
        }
        finally
        {
            UnityEngine.Object.Destroy(tex);
        }
    }

    /// <summary>
    /// Transparent mode (c0 &lt; c1): 3-color palette + transparent black at index 3.
    /// </summary>
    [TestInfo("CPUTexture2D_DXT1_Transparent")]
    public void TestDXT1TransparentMode()
    {
        // Swap: c0 = blue (0x001F), c1 = red (0xF800); c0 < c1 → transparent mode
        ushort c0 = EncodeRGB565(0, 0, 31);
        ushort c1 = EncodeRGB565(31, 0, 0);

        var block = MakeDXT1Block(
            c0,
            c1,
            MakeIndexRow(0, 0, 0, 0), // row 0: color0
            MakeIndexRow(1, 1, 1, 1), // row 1: color1
            MakeIndexRow(2, 2, 2, 2), // row 2: 1/2*(c0+c1)
            MakeIndexRow(3, 3, 3, 3) // row 3: transparent black
        );

        var tex = CreateTexture(4, 4, TextureFormat.DXT1, block);
        try
        {
            var rawData = tex.GetRawTextureData<byte>();
            var cpu = new CPUTexture2D.DXT1(rawData, 4, 4, 1);

            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                Color expected = tex.GetPixel(x, y);
                Color actual = cpu.GetPixel(x, y);
                assertColorEquals($"DXT1_Transparent({x},{y})", actual, expected, 0.02f);
            }

            // Index 3 must be transparent black
            Color black = cpu.GetPixel(0, 3);
            assertColorEquals("DXT1_TransparentBlack", black, new Color(0, 0, 0, 0), 0.001f);
        }
        finally
        {
            UnityEngine.Object.Destroy(tex);
        }
    }

    /// <summary>
    /// Equal endpoints (c0 == c1): treated as transparent mode.
    /// Indices 0, 1, 2 all resolve to the same color; index 3 = transparent black.
    /// </summary>
    [TestInfo("CPUTexture2D_DXT1_EqualEndpoints")]
    public void TestDXT1EqualEndpoints()
    {
        ushort c = EncodeRGB565(15, 32, 15);

        var block = MakeDXT1Block(
            c,
            c,
            MakeIndexRow(0, 1, 2, 3),
            MakeIndexRow(0, 1, 2, 3),
            MakeIndexRow(0, 1, 2, 3),
            MakeIndexRow(0, 1, 2, 3)
        );

        var tex = CreateTexture(4, 4, TextureFormat.DXT1, block);
        try
        {
            var rawData = tex.GetRawTextureData<byte>();
            var cpu = new CPUTexture2D.DXT1(rawData, 4, 4, 1);

            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                Color expected = tex.GetPixel(x, y);
                Color actual = cpu.GetPixel(x, y);
                assertColorEquals($"DXT1_Equal({x},{y})", actual, expected, 0.02f);
            }

            // Index 3 column should be transparent black
            assertColorEquals(
                "DXT1_Equal_TransparentBlack",
                cpu.GetPixel(3, 0),
                new Color(0, 0, 0, 0),
                0.001f
            );

            // Indices 0 and 1 should be the same color since c0 == c1
            assertColorEquals(
                "DXT1_Equal_SameColor",
                cpu.GetPixel(0, 0),
                cpu.GetPixel(1, 0),
                0.001f
            );
        }
        finally
        {
            UnityEngine.Object.Destroy(tex);
        }
    }

    /// <summary>
    /// Opaque mode with every pixel in the block assigned a different index pattern,
    /// verifying correct per-pixel index extraction from the packed 2-bit fields.
    /// </summary>
    [TestInfo("CPUTexture2D_DXT1_MixedIndices")]
    public void TestDXT1MixedIndices()
    {
        ushort c0 = EncodeRGB565(20, 40, 10);
        ushort c1 = EncodeRGB565(5, 10, 25);
        // c0 = 42250 > c1 = 10585 → opaque mode

        var block = MakeDXT1Block(
            c0,
            c1,
            MakeIndexRow(0, 1, 2, 3),
            MakeIndexRow(3, 2, 1, 0),
            MakeIndexRow(1, 3, 0, 2),
            MakeIndexRow(2, 0, 3, 1)
        );

        var tex = CreateTexture(4, 4, TextureFormat.DXT1, block);
        try
        {
            var rawData = tex.GetRawTextureData<byte>();
            var cpu = new CPUTexture2D.DXT1(rawData, 4, 4, 1);

            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                Color expected = tex.GetPixel(x, y);
                Color actual = cpu.GetPixel(x, y);
                assertColorEquals($"DXT1_Mixed({x},{y})", actual, expected, 0.02f);
            }
        }
        finally
        {
            UnityEngine.Object.Destroy(tex);
        }
    }

    /// <summary>
    /// 8x8 texture (2x2 blocks) mixing opaque and transparent blocks,
    /// verifying correct block addressing across multiple blocks.
    /// </summary>
    [TestInfo("CPUTexture2D_DXT1_MultiBlock")]
    public void TestDXT1MultiBlock()
    {
        // Block (0,0): opaque, red/blue
        var block00 = MakeDXT1Block(
            EncodeRGB565(31, 0, 0),
            EncodeRGB565(0, 0, 31),
            MakeIndexRow(0, 1, 2, 3),
            MakeIndexRow(0, 1, 2, 3),
            MakeIndexRow(0, 1, 2, 3),
            MakeIndexRow(0, 1, 2, 3)
        );

        // Block (1,0): opaque, white/green
        var block10 = MakeDXT1Block(
            EncodeRGB565(31, 63, 31),
            EncodeRGB565(0, 63, 0),
            MakeIndexRow(0, 0, 1, 1),
            MakeIndexRow(0, 0, 1, 1),
            MakeIndexRow(2, 2, 3, 3),
            MakeIndexRow(2, 2, 3, 3)
        );

        // Block (0,1): transparent, blue/red (c0 < c1)
        var block01 = MakeDXT1Block(
            EncodeRGB565(0, 0, 31),
            EncodeRGB565(31, 0, 0),
            MakeIndexRow(0, 0, 0, 0),
            MakeIndexRow(1, 1, 1, 1),
            MakeIndexRow(2, 2, 2, 2),
            MakeIndexRow(3, 3, 3, 3)
        );

        // Block (1,1): equal endpoints
        ushort mid = EncodeRGB565(16, 32, 16);
        var block11 = MakeDXT1Block(
            mid,
            mid,
            MakeIndexRow(0, 1, 2, 3),
            MakeIndexRow(0, 1, 2, 3),
            MakeIndexRow(0, 1, 2, 3),
            MakeIndexRow(0, 1, 2, 3)
        );

        // DXT1 blocks in row-major order: (0,0), (1,0), (0,1), (1,1)
        var rawData = new byte[32];
        Array.Copy(block00, 0, rawData, 0, 8);
        Array.Copy(block10, 0, rawData, 8, 8);
        Array.Copy(block01, 0, rawData, 16, 8);
        Array.Copy(block11, 0, rawData, 24, 8);

        var tex = CreateTexture(8, 8, TextureFormat.DXT1, rawData);
        try
        {
            var nativeData = tex.GetRawTextureData<byte>();
            var cpu = new CPUTexture2D.DXT1(nativeData, 8, 8, 1);

            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                Color expected = tex.GetPixel(x, y);
                Color actual = cpu.GetPixel(x, y);
                assertColorEquals($"DXT1_Multi({x},{y})", actual, expected, 0.02f);
            }
        }
        finally
        {
            UnityEngine.Object.Destroy(tex);
        }
    }
}
