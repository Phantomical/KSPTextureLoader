using System;
using KSP.Testing;
using KSPTextureLoader;
using KSPTextureLoader.Utils;
using UnityEngine;
using DXT1Block = KSPTextureLoader.CPU.Block.DXT1;

namespace KSPTextureLoaderTests;

/// <summary>
/// Validates the new SIMD block decoder <see cref="KSPTextureLoader.CPU.Block.DXT1"/>
/// against the existing, Unity-validated public decoder <see cref="CPUTexture2D.DXT1"/>,
/// which is treated as ground truth.
///
/// For each raw 4x4 DXT1 block the block decoder is fed the same 8 bytes (packed into a
/// ulong exactly as <see cref="CPUTexture2D.DXT1"/> reinterprets them) and both
/// <see cref="DXT1Block.DecodePixel"/> for all 16 pixels and every element of
/// <see cref="DXT1Block.DecodeBlock"/> are compared to the ground-truth pixels.
/// </summary>
public class DXT1BlockTests : KSPTextureLoaderTestBase
{
    // Same tolerance the existing DXT1 suite uses when comparing decoded colors.
    const float Tol = 0.02f;

    static ushort EncodeRGB565(int r5, int g6, int b5) => (ushort)((r5 << 11) | (g6 << 5) | b5);

    static byte MakeIndexRow(int i0, int i1, int i2, int i3) =>
        (byte)(i0 | (i1 << 2) | (i2 << 4) | (i3 << 6));

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

    /// <summary>
    /// Builds a deterministic pseudo-random 8-byte block from <paramref name="k"/> using
    /// pure arithmetic (no System.Random, no DateTime), so runs are reproducible.
    /// The endpoints land in both 4-color (c0 &gt; c1) and 3-color (c0 &lt;= c1) modes.
    /// </summary>
    static byte[] MakePseudoRandomBlock(int k)
    {
        var b = new byte[8];
        for (int j = 0; j < 8; j++)
            b[j] = (byte)(((k * 131) + (j * 61) + 17) * (j + 1) % 256);
        return b;
    }

    /// <summary>
    /// Core comparison: constructs the ground-truth <see cref="CPUTexture2D.DXT1"/> from the
    /// raw bytes and checks that the new block decoder's <c>DecodePixel</c> (all 16 pixels)
    /// and every element of <c>DecodeBlock</c> agree, within the format's tolerance.
    /// </summary>
    void AssertBlockMatchesGroundTruth(string name, byte[] blockBytes)
    {
        var tex = CreateTexture(4, 4, TextureFormat.DXT1, blockBytes);
        try
        {
            var rawData = tex.GetRawTextureData<byte>();
            var cpu = new CPUTexture2D.DXT1(
                LargeNativeArray<byte>.FromNativeArray(rawData),
                4,
                4,
                1
            );

            // Pack the raw bytes into a ulong exactly as CPUTexture2D.DXT1 reinterprets
            // them (little-endian: byte 0 is the low byte).
            ulong block = 0;
            for (int i = 0; i < 8; i++)
                block |= (ulong)rawData[i] << (i * 8);

            var decoded = DXT1Block.DecodeBlock(block);

            for (int i = 0; i < 16; i++)
            {
                // Ground-truth pixel index (y % 4) * 4 + (x % 4) == i for a 4x4 texture,
                // matching the block decoder's row-major pixel numbering.
                int x = i % 4;
                int y = i / 4;
                Color expected = cpu.GetPixel(x, y);

                Color pixel = DXT1Block.DecodePixel(block, i);
                assertColorEquals($"{name}_DecodePixel[{i}]", pixel, expected, Tol);
                assertColorEquals($"{name}_DecodeBlock[{i}]", decoded[i], expected, Tol);
            }
        }
        finally
        {
            UnityEngine.Object.Destroy(tex);
        }
    }

    /// <summary>
    /// 4-color mode (c0 &gt; c1): palette entries 2/3 are the 2/3 and 1/3 lerps and every
    /// pixel is opaque. Each row selects a single palette entry so all four are exercised.
    /// </summary>
    [TestInfo("CPUTexture2D_DXT1Block_FourColorMode")]
    public void TestFourColorMode()
    {
        ushort c0 = EncodeRGB565(31, 0, 0); // 0xF800
        ushort c1 = EncodeRGB565(0, 0, 31); // 0x001F  -> c0 > c1
        var block = MakeDXT1Block(
            c0,
            c1,
            MakeIndexRow(0, 0, 0, 0),
            MakeIndexRow(1, 1, 1, 1),
            MakeIndexRow(2, 2, 2, 2),
            MakeIndexRow(3, 3, 3, 3)
        );
        AssertBlockMatchesGroundTruth("FourColor", block);
    }

    /// <summary>
    /// 3-color mode (c0 &lt; c1): palette entry 2 is the midpoint and palette entry 3 is
    /// transparent black (rgba = 0). Alpha is 1 everywhere except the index-3 pixels.
    /// </summary>
    [TestInfo("CPUTexture2D_DXT1Block_ThreeColorMode")]
    public void TestThreeColorMode()
    {
        ushort c0 = EncodeRGB565(0, 0, 31); // 0x001F
        ushort c1 = EncodeRGB565(31, 0, 0); // 0xF800  -> c0 < c1
        var block = MakeDXT1Block(
            c0,
            c1,
            MakeIndexRow(0, 0, 0, 0),
            MakeIndexRow(1, 1, 1, 1),
            MakeIndexRow(2, 2, 2, 2),
            MakeIndexRow(3, 3, 3, 3) // transparent black
        );
        AssertBlockMatchesGroundTruth("ThreeColor", block);
    }

    /// <summary>
    /// Equal endpoints (c0 == c1): treated as 3-color mode. Indices 0,1,2 resolve to the
    /// same color; index 3 is transparent black.
    /// </summary>
    [TestInfo("CPUTexture2D_DXT1Block_EqualEndpoints")]
    public void TestEqualEndpoints()
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
        AssertBlockMatchesGroundTruth("EqualEndpoints", block);
    }

    /// <summary>
    /// Exercises the RGB565 unpack widths (5/6/5) by choosing endpoint channel values that
    /// differ across all three channels, so a wrong mask/shift width surfaces. c0 &gt; c1
    /// keeps the block in 4-color (opaque) mode.
    /// </summary>
    [TestInfo("CPUTexture2D_DXT1Block_RGB565Widths")]
    public void TestRGB565Widths()
    {
        // Distinct values spanning the low/high bits of each channel's width.
        ushort c0 = EncodeRGB565(21, 42, 9); // r5=21, g6=42, b5=9
        ushort c1 = EncodeRGB565(6, 17, 28); // r5=6,  g6=17, b5=28
        // c0 = 0xACC9, c1 = 0x323C -> c0 > c1 (4-color mode)
        var block = MakeDXT1Block(
            c0,
            c1,
            MakeIndexRow(0, 1, 2, 3),
            MakeIndexRow(1, 2, 3, 0),
            MakeIndexRow(2, 3, 0, 1),
            MakeIndexRow(3, 0, 1, 2)
        );
        AssertBlockMatchesGroundTruth("RGB565Widths", block);
    }

    /// <summary>
    /// Also cover boundary channel values: max/min per channel width to verify 31/63/31
    /// scaling is exact (and c0 &lt; c1 so the transparent path is included).
    /// </summary>
    [TestInfo("CPUTexture2D_DXT1Block_ChannelExtremes")]
    public void TestChannelExtremes()
    {
        ushort c0 = EncodeRGB565(0, 0, 0); // black
        ushort c1 = EncodeRGB565(31, 63, 31); // white -> c0 < c1 (3-color mode)
        var block = MakeDXT1Block(
            c0,
            c1,
            MakeIndexRow(0, 1, 2, 3),
            MakeIndexRow(3, 2, 1, 0),
            MakeIndexRow(0, 3, 1, 2),
            MakeIndexRow(2, 1, 3, 0)
        );
        AssertBlockMatchesGroundTruth("ChannelExtremes", block);
    }

    /// <summary>
    /// 4-color mode with every pixel assigned a different index pattern, verifying correct
    /// per-pixel 2-bit index extraction from the packed rows.
    /// </summary>
    [TestInfo("CPUTexture2D_DXT1Block_MixedIndices")]
    public void TestMixedIndices()
    {
        ushort c0 = EncodeRGB565(20, 40, 10);
        ushort c1 = EncodeRGB565(5, 10, 25); // c0 > c1
        var block = MakeDXT1Block(
            c0,
            c1,
            MakeIndexRow(0, 1, 2, 3),
            MakeIndexRow(3, 2, 1, 0),
            MakeIndexRow(1, 3, 0, 2),
            MakeIndexRow(2, 0, 3, 1)
        );
        AssertBlockMatchesGroundTruth("MixedIndices", block);
    }

    /// <summary>
    /// A spread of deterministic pseudo-random blocks. The endpoints span both encoding
    /// modes and the index rows cover arbitrary palette selections.
    /// </summary>
    [TestInfo("CPUTexture2D_DXT1Block_PseudoRandom")]
    public void TestPseudoRandom()
    {
        for (int k = 0; k < 32; k++)
        {
            var block = MakePseudoRandomBlock(k);
            AssertBlockMatchesGroundTruth($"PseudoRandom[{k}]", block);
        }
    }
}
