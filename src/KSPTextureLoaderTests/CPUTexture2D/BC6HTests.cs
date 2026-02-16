using System;
using KSP.Testing;
using KSPTextureLoader;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoaderTests;

/// <summary>
/// Comprehensive tests for <see cref="CPUTexture2D.BC6H"/>.
///
/// BC6H format: 16-byte blocks covering 4x4 pixels (8 bits/pixel).
/// Encodes HDR RGB (no alpha) in half-precision floats.
/// Supports 14 modes (0-13) with 1 or 2 subsets.
/// Modes 0-9: 2 subsets, 3-bit indices, 5-bit partition.
/// Modes 10-13: 1 subset, 4-bit indices.
/// Comes in signed (SF16) and unsigned (UF16) variants.
/// </summary>
public class BC6HTests : CPUTexture2DTests
{
    const float BC6HTol = 0.001f;

    // ---- Bit writer for constructing BC6H blocks ----

    struct BitWriter
    {
        readonly byte[] data;
        int pos;

        public BitWriter(byte[] data)
        {
            this.data = data;
            pos = 0;
        }

        public void Write(int value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int byteIdx = pos >> 3;
                int bitIdx = pos & 7;
                data[byteIdx] |= (byte)(((value >> i) & 1) << bitIdx);
                pos++;
            }
        }

        public void WriteN(int value, int bits, int n)
        {
            for (int i = 0; i < n; i++)
                Write(value, bits);
        }
    }

    // ---- Reference helpers (matching the decoder logic) ----

    static int RefSignExtend(int val, int bits)
    {
        int shift = 32 - bits;
        return (val << shift) >> shift;
    }

    static int RefUnquantize(int val, int bits, bool signed)
    {
        if (signed)
        {
            if (bits >= 16)
                return val;
            bool s = false;
            if (val < 0)
            {
                s = true;
                val = -val;
            }
            int unq;
            if (val == 0)
                unq = 0;
            else if (val >= ((1 << (bits - 1)) - 1))
                unq = 0x7FFF;
            else
                unq = ((val << 15) + 0x4000) >> (bits - 1);
            return s ? -unq : unq;
        }
        else
        {
            if (bits >= 15)
                return val;
            if (val == 0)
                return 0;
            if (val == ((1 << bits) - 1))
                return 0xFFFF;
            return ((val << 15) + 0x4000) >> (bits - 1);
        }
    }

    static float RefHalfToFloat(int h)
    {
        int sign = (h >> 15) & 1;
        int exp = (h >> 10) & 0x1F;
        int mantissa = h & 0x3FF;

        if (exp == 0)
        {
            if (mantissa == 0)
                return 0f;
            float f = mantissa / 1024f;
            f *= 1f / 16384f;
            return sign == 1 ? -f : f;
        }
        else if (exp == 31)
        {
            return mantissa == 0
                ? (sign == 1 ? float.NegativeInfinity : float.PositiveInfinity)
                : float.NaN;
        }
        else
        {
            float f = (1f + mantissa / 1024f) * Mathf.Pow(2f, exp - 15);
            return sign == 1 ? -f : f;
        }
    }

    static float RefFinishUnquantize(int val, bool signed)
    {
        if (signed)
        {
            int s = 0;
            if (val < 0)
            {
                s = 0x8000;
                val = -val;
            }
            return RefHalfToFloat(s | ((val * 31) >> 5));
        }
        else
        {
            return RefHalfToFloat((val * 31) >> 6);
        }
    }

    /// <summary>
    /// Compute expected float output for a solid unsigned BC6H block
    /// with the given raw endpoint value at the given bit precision.
    /// </summary>
    static float RefSolidUnsigned(int rawVal, int bits)
    {
        int uq = RefUnquantize(rawVal, bits, false);
        // Interpolation with equal endpoints -> same value
        return RefFinishUnquantize(uq, false);
    }

    // csharpier-ignore-start
    static readonly int[] RefWeights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
    static readonly int[] RefWeights4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };
    // csharpier-ignore-end

    static int RefInterpolate(int e0, int e1, int weight)
    {
        return ((64 - weight) * e0 + weight * e1 + 32) >> 6;
    }

    // ---- Factory/comparison helpers ----

    static (CPUTexture2D.BC6H tex, NativeArray<byte> data) Make(
        byte[] blockData,
        int width = 4,
        int height = 4,
        int mipCount = 1,
        bool signed = false
    )
    {
        var native = new NativeArray<byte>(blockData, Allocator.Temp);
        return (new CPUTexture2D.BC6H(native, width, height, mipCount, signed), native);
    }

    void AssertPixelHDR(
        string label,
        CPUTexture2D.BC6H bc6h,
        int x,
        int y,
        float er,
        float eg,
        float eb,
        float tol = BC6HTol
    )
    {
        Color c = bc6h.GetPixel(x, y);
        assertFloatEquals($"{label}.R({x},{y})", c.r, er, tol);
        assertFloatEquals($"{label}.G({x},{y})", c.g, eg, tol);
        assertFloatEquals($"{label}.B({x},{y})", c.b, eb, tol);
        assertFloatEquals($"{label}.A({x},{y})", c.a, 1f, 0.0001f);
    }

    void CompareWithUnity(string label, byte[] blockData, int w = 4, int h = 4)
    {
        var tex = new Texture2D(w, h, TextureFormat.BC6H, false);
        tex.LoadRawTextureData(blockData);
        tex.Apply(false, false);

        var (bc6h, data) = Make(blockData, w, h);
        try
        {
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color expected = tex.GetPixel(x, y);
                Color actual = bc6h.GetPixel(x, y);
                assertColorEquals($"{label}.Unity({x},{y})", actual, expected, BC6HTol);
            }
        }
        finally
        {
            data.Dispose();
            UnityEngine.Object.Destroy(tex);
        }
    }

    // ---- Mode block builders ----

    /// <summary>
    /// Build a solid Mode 10 block (simplest: 1 subset, 10-bit direct, no transform).
    /// Both endpoints set to (rv, gv, bv).
    /// </summary>
    byte[] BuildSolidMode10(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x03, 5); // mode 10 = 00011
        w.Write(rv, 10);
        w.Write(gv, 10);
        w.Write(bv, 10);
        w.Write(rv, 10); // rx = rw
        w.Write(gv, 10); // gx = gw
        w.Write(bv, 10); // bx = bw
        // indices all 0
        return blk;
    }

    /// <summary>
    /// Build a Mode 10 block with two endpoints and ascending 4-bit indices.
    /// </summary>
    byte[] BuildGradientMode10(int r0, int r1, int g0, int g1, int b0, int b1)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x03, 5); // mode 10
        w.Write(r0, 10);
        w.Write(g0, 10);
        w.Write(b0, 10);
        w.Write(r1, 10);
        w.Write(g1, 10);
        w.Write(b1, 10);
        // Indices: pixel 0 (anchor) = 3-bit index 0, pixels 1-15 = 4-bit ascending
        w.Write(0, 3); // pixel 0 anchor
        for (int i = 1; i < 16; i++)
            w.Write(i, 4);
        return blk;
    }

    /// <summary>
    /// Build a solid Mode 11 block (1 subset, 11-bit base + 9-bit delta, transformed).
    /// </summary>
    byte[] BuildSolidMode11(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x07, 5); // mode 11 = 00111
        // rw[9:0]
        w.Write(rv & 0x3FF, 10);
        // gw[9:0]
        w.Write(gv & 0x3FF, 10);
        // bw[9:0]
        w.Write(bv & 0x3FF, 10);
        // rx[8:0] = 0 (delta)
        w.Write(0, 9);
        // rw[10]
        w.Write((rv >> 10) & 1, 1);
        // gx[8:0] = 0
        w.Write(0, 9);
        // gw[10]
        w.Write((gv >> 10) & 1, 1);
        // bx[8:0] = 0
        w.Write(0, 9);
        // bw[10]
        w.Write((bv >> 10) & 1, 1);
        // indices all 0
        return blk;
    }

    /// <summary>
    /// Build a solid Mode 0 block (2 subsets, 10-bit base + 5/5/5 delta).
    /// All deltas = 0, partition = 0. Both subsets get same color.
    /// </summary>
    byte[] BuildSolidMode0(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x00, 2); // mode 0 = 00
        // gy[4], by[4], bz[4] = 0 (delta high bits)
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(0, 1);
        // rw[9:0], gw[9:0], bw[9:0]
        w.Write(rv, 10);
        w.Write(gv, 10);
        w.Write(bv, 10);
        // rest is all zeros (deltas + partition)
        return blk;
    }

    /// <summary>
    /// Build a solid Mode 1 block (2 subsets, 7-bit base + 6/6/6 delta).
    /// All deltas = 0, partition = 0.
    /// </summary>
    byte[] BuildSolidMode1(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x01, 2); // mode 1 = 01
        // gy[5], gz[4], gz[5] = 0
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(0, 1);
        // rw[6:0]
        w.Write(rv, 7);
        // bz[0], bz[1], by[4] = 0
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(0, 1);
        // gw[6:0]
        w.Write(gv, 7);
        // by[5], bz[2], gy[4] = 0
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(0, 1);
        // bw[6:0]
        w.Write(bv, 7);
        // rest is all zeros (remaining delta bits + partition)
        return blk;
    }

    /// <summary>
    /// Build a solid Mode 12 block (1 subset, 12-bit base (10+2 reversed), 8-bit delta).
    /// </summary>
    byte[] BuildSolidMode12(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x0B, 5); // mode 12 = 01011
        // rw[9:0]
        w.Write(rv & 0x3FF, 10);
        // gw[9:0]
        w.Write(gv & 0x3FF, 10);
        // bw[9:0]
        w.Write(bv & 0x3FF, 10);
        // rx[7:0] = 0 (delta)
        w.Write(0, 8);
        // rw[11:10] reversed: bits 10,11 of rv, written in reversed order
        int rHigh = (rv >> 10) & 3;
        w.Write(ReverseBits(rHigh, 2), 2);
        // gx[7:0] = 0
        w.Write(0, 8);
        int gHigh = (gv >> 10) & 3;
        w.Write(ReverseBits(gHigh, 2), 2);
        // bx[7:0] = 0
        w.Write(0, 8);
        int bHigh = (bv >> 10) & 3;
        w.Write(ReverseBits(bHigh, 2), 2);
        // indices all 0
        return blk;
    }

    /// <summary>
    /// Build a solid Mode 13 block (1 subset, 16-bit base (10+6 reversed), 4-bit delta).
    /// </summary>
    byte[] BuildSolidMode13(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x0F, 5); // mode 13 = 01111
        w.Write(rv & 0x3FF, 10);
        w.Write(gv & 0x3FF, 10);
        w.Write(bv & 0x3FF, 10);
        // rx[3:0] = 0 (delta)
        w.Write(0, 4);
        int rHigh = (rv >> 10) & 0x3F;
        w.Write(ReverseBits(rHigh, 6), 6);
        w.Write(0, 4);
        int gHigh = (gv >> 10) & 0x3F;
        w.Write(ReverseBits(gHigh, 6), 6);
        w.Write(0, 4);
        int bHigh = (bv >> 10) & 0x3F;
        w.Write(ReverseBits(bHigh, 6), 6);
        // indices all 0
        return blk;
    }

    /// <summary>
    /// Build a solid Mode 2 block (2 subsets, 11-bit base (10+1), 5/4/4 delta, transformed).
    /// Mode bits = 00010. All deltas = 0, partition = 0.
    /// </summary>
    byte[] BuildSolidMode2(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x02, 5); // mode 2 = 00010
        w.Write(rv & 0x3FF, 10); // rw[9:0]
        w.Write(gv & 0x3FF, 10); // gw[9:0]
        w.Write(bv & 0x3FF, 10); // bw[9:0]
        w.Write(0, 5); // rx[4:0] = 0
        w.Write((rv >> 10) & 1, 1); // rw bit 10
        w.Write(0, 4); // gy[3:0] = 0
        w.Write(0, 4); // gx[3:0] = 0
        w.Write((gv >> 10) & 1, 1); // gw bit 10
        w.Write(0, 1); // bz bit 0 = 0
        w.Write(0, 4); // gz[3:0] = 0
        w.Write(0, 4); // bx[3:0] = 0
        w.Write((bv >> 10) & 1, 1); // bw bit 10
        // remaining: bz[1]=0, by[3:0]=0, ry[4:0]=0, bz[2]=0, rz[4:0]=0, bz[3]=0, partition=0
        return blk;
    }

    /// <summary>
    /// Build a solid Mode 3 block (2 subsets, 11-bit base (10+1), 4/5/4 delta, transformed).
    /// Mode bits = 00110. All deltas = 0, partition = 0.
    /// </summary>
    byte[] BuildSolidMode3(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x06, 5); // mode 3 = 00110
        w.Write(rv & 0x3FF, 10); // rw[9:0]
        w.Write(gv & 0x3FF, 10); // gw[9:0]
        w.Write(bv & 0x3FF, 10); // bw[9:0]
        w.Write(0, 4); // rx[3:0] = 0
        w.Write((rv >> 10) & 1, 1); // rw bit 10
        w.Write(0, 1); // gz bit 4 = 0
        w.Write(0, 4); // gy[3:0] = 0
        w.Write(0, 5); // gx[4:0] = 0
        w.Write((gv >> 10) & 1, 1); // gw bit 10
        w.Write(0, 4); // gz[3:0] = 0
        w.Write(0, 4); // bx[3:0] = 0
        w.Write((bv >> 10) & 1, 1); // bw bit 10
        // remaining: bz[1]=0, by[3:0]=0, ry[3:0]=0, bz[0]=0, bz[2]=0, rz[3:0]=0, gy[4]=0, bz[3]=0, partition=0
        return blk;
    }

    /// <summary>
    /// Build a solid Mode 4 block (2 subsets, 11-bit base (10+1), 4/4/5 delta, transformed).
    /// Mode bits = 01010. All deltas = 0, partition = 0.
    /// </summary>
    byte[] BuildSolidMode4(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x0A, 5); // mode 4 = 01010
        w.Write(rv & 0x3FF, 10); // rw[9:0]
        w.Write(gv & 0x3FF, 10); // gw[9:0]
        w.Write(bv & 0x3FF, 10); // bw[9:0]
        w.Write(0, 4); // rx[3:0] = 0
        w.Write((rv >> 10) & 1, 1); // rw bit 10
        w.Write(0, 1); // by bit 4 = 0
        w.Write(0, 4); // gy[3:0] = 0
        w.Write(0, 4); // gx[3:0] = 0
        w.Write((gv >> 10) & 1, 1); // gw bit 10
        w.Write(0, 1); // bz bit 0 = 0
        w.Write(0, 4); // gz[3:0] = 0
        w.Write(0, 5); // bx[4:0] = 0
        w.Write((bv >> 10) & 1, 1); // bw bit 10
        // remaining: by[3:0]=0, ry[3:0]=0, bz[1]=0, bz[2]=0, rz[3:0]=0, bz[4]=0, bz[3]=0, partition=0
        return blk;
    }

    /// <summary>
    /// Build a solid Mode 5 block (2 subsets, 9-bit base, 5/5/5 delta, transformed).
    /// Mode bits = 01110. All deltas = 0, partition = 0.
    /// </summary>
    byte[] BuildSolidMode5(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x0E, 5); // mode 5 = 01110
        w.Write(rv, 9); // rw[8:0]
        w.Write(0, 1); // by bit 4 = 0
        w.Write(gv, 9); // gw[8:0]
        w.Write(0, 1); // gy bit 4 = 0
        w.Write(bv, 9); // bw[8:0]
        w.Write(0, 1); // bz bit 4 = 0
        // rest: rx=0, gz[4]=0, gy[3:0]=0, gx=0, bz[0]=0, gz[3:0]=0, bx=0, bz[1]=0, by[3:0]=0,
        //       ry=0, bz[2]=0, rz=0, bz[3]=0, partition=0
        return blk;
    }

    /// <summary>
    /// Build a solid Mode 6 block (2 subsets, 8-bit base, 6/5/5 delta, transformed).
    /// Mode bits = 10010. All deltas = 0, partition = 0.
    /// </summary>
    byte[] BuildSolidMode6(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x12, 5); // mode 6 = 10010
        w.Write(rv, 8); // rw[7:0]
        w.Write(0, 1); // gz bit 4 = 0
        w.Write(0, 1); // by bit 4 = 0
        w.Write(gv, 8); // gw[7:0]
        w.Write(0, 1); // bz bit 2 = 0
        w.Write(0, 1); // gy bit 4 = 0
        w.Write(bv, 8); // bw[7:0]
        w.Write(0, 1); // bz bit 3 = 0
        w.Write(0, 1); // bz bit 4 = 0
        // rest: rx=0, gy[3:0]=0, gx=0, bz[0]=0, gz[3:0]=0, bx=0, bz[1]=0, by[3:0]=0,
        //       ry=0, rz=0, partition=0
        return blk;
    }

    /// <summary>
    /// Build a solid Mode 7 block (2 subsets, 8-bit base, 5/6/5 delta, transformed).
    /// Mode bits = 10110. All deltas = 0, partition = 0.
    /// </summary>
    byte[] BuildSolidMode7(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x16, 5); // mode 7 = 10110
        w.Write(rv, 8); // rw[7:0]
        w.Write(0, 1); // bz bit 0 = 0
        w.Write(0, 1); // by bit 4 = 0
        w.Write(gv, 8); // gw[7:0]
        w.Write(0, 1); // gy bit 5 = 0
        w.Write(0, 1); // gy bit 4 = 0
        w.Write(bv, 8); // bw[7:0]
        w.Write(0, 1); // gz bit 5 = 0
        w.Write(0, 1); // bz bit 4 = 0
        // rest: rx=0, gz[4]=0, gy[3:0]=0, gx=0, gz[3:0]=0, bx=0, bz[1]=0, by[3:0]=0,
        //       ry=0, bz[2]=0, rz=0, bz[3]=0, partition=0
        return blk;
    }

    /// <summary>
    /// Build a solid Mode 8 block (2 subsets, 8-bit base, 5/5/6 delta, transformed).
    /// Mode bits = 11010. All deltas = 0, partition = 0.
    /// </summary>
    byte[] BuildSolidMode8(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x1A, 5); // mode 8 = 11010
        w.Write(rv, 8); // rw[7:0]
        w.Write(0, 1); // bz bit 1 = 0
        w.Write(0, 1); // by bit 4 = 0
        w.Write(gv, 8); // gw[7:0]
        w.Write(0, 1); // by bit 5 = 0
        w.Write(0, 1); // gy bit 4 = 0
        w.Write(bv, 8); // bw[7:0]
        w.Write(0, 1); // bz bit 5 = 0
        w.Write(0, 1); // bz bit 4 = 0
        // rest: rx=0, gz[4]=0, gy[3:0]=0, gx=0, bz[0]=0, gz[3:0]=0, bx=0, by[3:0]=0,
        //       ry=0, bz[2]=0, rz=0, bz[3]=0, partition=0
        return blk;
    }

    /// <summary>
    /// Build a solid Mode 9 block (2 subsets, 6-bit base, 6/6/6, NOT transformed).
    /// Mode bits = 11110. Since mode 9 is NOT transformed, all 4 endpoints are
    /// independent 6-bit values. For a solid block, all endpoints = (rv, gv, bv).
    /// Partition = 0.
    /// </summary>
    byte[] BuildSolidMode9(int rv, int gv, int bv)
    {
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x1E, 5); // mode 9 = 11110
        // Bit layout per spec (after 5 mode bits):
        // rw[5:0], gz[4], bz[0], bz[1], by[4],
        // gw[5:0], gy[5], by[5], bz[2], gy[4],
        // bw[5:0], gz[5], bz[3], bz[5], bz[4],
        // rx[5:0], gy[3:0], gx[5:0], gz[3:0],
        // bx[5:0], by[3:0], ry[5:0], rz[5:0], partition[4:0]
        w.Write(rv, 6); // rw
        w.Write((gv >> 4) & 1, 1); // gz[4] = gv bit 4
        w.Write(bv & 1, 1); // bz[0] = bv bit 0
        w.Write((bv >> 1) & 1, 1); // bz[1] = bv bit 1
        w.Write((bv >> 4) & 1, 1); // by[4] = bv bit 4
        w.Write(gv, 6); // gw
        w.Write((gv >> 5) & 1, 1); // gy[5] = gv bit 5
        w.Write((bv >> 5) & 1, 1); // by[5] = bv bit 5
        w.Write((bv >> 2) & 1, 1); // bz[2] = bv bit 2
        w.Write((gv >> 4) & 1, 1); // gy[4] = gv bit 4
        w.Write(bv, 6); // bw
        w.Write((gv >> 5) & 1, 1); // gz[5] = gv bit 5
        w.Write((bv >> 3) & 1, 1); // bz[3] = bv bit 3
        w.Write((bv >> 5) & 1, 1); // bz[5] = bv bit 5
        w.Write((bv >> 4) & 1, 1); // bz[4] = bv bit 4
        w.Write(rv, 6); // rx = rv
        w.Write(gv & 0xF, 4); // gy[3:0]
        w.Write(gv, 6); // gx = gv
        w.Write(gv & 0xF, 4); // gz[3:0]
        w.Write(bv, 6); // bx = bv
        w.Write(bv & 0xF, 4); // by[3:0]
        w.Write(rv, 6); // ry = rv
        w.Write(rv, 6); // rz = rv
        w.Write(0, 5); // partition = 0
        return blk;
    }

    static int ReverseBits(int val, int numBits)
    {
        int result = 0;
        for (int i = 0; i < numBits; i++)
        {
            result = (result << 1) | (val & 1);
            val >>= 1;
        }
        return result;
    }

    // ================================================================
    // 1. Mode 10 solid: simplest mode (1 subset, 10-bit direct, no transform)
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode10_Solid")]
    public void TestMode10Solid()
    {
        int rv = 512,
            gv = 256,
            bv = 100;
        float er = RefSolidUnsigned(rv, 10);
        float eg = RefSolidUnsigned(gv, 10);
        float eb = RefSolidUnsigned(bv, 10);

        var blk = BuildSolidMode10(rv, gv, bv);
        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M10Solid", bc6h, 0, 0, er, eg, eb);
            AssertPixelHDR("M10Solid", bc6h, 3, 3, er, eg, eb);
            CompareWithUnity("M10Solid", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 2. Mode 10 zero: all zeros should decode to black
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode10_Zero")]
    public void TestMode10Zero()
    {
        var blk = BuildSolidMode10(0, 0, 0);
        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M10Zero", bc6h, 0, 0, 0f, 0f, 0f);
            CompareWithUnity("M10Zero", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 3. Mode 10 max: 1023 should decode to max half-float (~65504)
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode10_Max")]
    public void TestMode10Max()
    {
        float eMax = RefSolidUnsigned(1023, 10);
        var blk = BuildSolidMode10(1023, 1023, 1023);
        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M10Max", bc6h, 0, 0, eMax, eMax, eMax, 1f);
            CompareWithUnity("M10Max", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 4. Mode 10 interpolation: different endpoints, ascending indices
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode10_Interp")]
    public void TestMode10Interpolation()
    {
        int r0 = 100,
            r1 = 900;
        int g0 = 200,
            g1 = 800;
        int b0 = 50,
            b1 = 500;

        var blk = BuildGradientMode10(r0, r1, g0, g1, b0, b1);
        var (bc6h, data) = Make(blk);
        try
        {
            // Pixel 0 (anchor, index=0): weight=0 -> endpoint 0
            int uqR0 = RefUnquantize(r0, 10, false);
            int uqG0 = RefUnquantize(g0, 10, false);
            int uqB0 = RefUnquantize(b0, 10, false);
            int uqR1 = RefUnquantize(r1, 10, false);
            int uqG1 = RefUnquantize(g1, 10, false);
            int uqB1 = RefUnquantize(b1, 10, false);

            float expR0 = RefFinishUnquantize(uqR0, false);
            float expG0 = RefFinishUnquantize(uqG0, false);
            float expB0 = RefFinishUnquantize(uqB0, false);
            AssertPixelHDR("M10Interp_p0", bc6h, 0, 0, expR0, expG0, expB0);

            // Pixel 15 (index=15): weight=64 -> endpoint 1
            float expR15 = RefFinishUnquantize(uqR1, false);
            float expG15 = RefFinishUnquantize(uqG1, false);
            float expB15 = RefFinishUnquantize(uqB1, false);
            AssertPixelHDR("M10Interp_p15", bc6h, 3, 3, expR15, expG15, expB15);

            // Pixel 8 (index=8): weight=Weights4[8]=34
            int w8 = RefWeights4[8];
            float expR8 = RefFinishUnquantize(RefInterpolate(uqR0, uqR1, w8), false);
            float expG8 = RefFinishUnquantize(RefInterpolate(uqG0, uqG1, w8), false);
            float expB8 = RefFinishUnquantize(RefInterpolate(uqB0, uqB1, w8), false);
            AssertPixelHDR("M10Interp_p8", bc6h, 0, 2, expR8, expG8, expB8);

            CompareWithUnity("M10Interp", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 5. Mode 11 solid: 1 subset, 11-bit base + 9-bit delta, transformed
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode11_Solid")]
    public void TestMode11Solid()
    {
        // 11-bit value (max 2047)
        int rv = 1500,
            gv = 800,
            bv = 200;
        float er = RefSolidUnsigned(rv, 11);
        float eg = RefSolidUnsigned(gv, 11);
        float eb = RefSolidUnsigned(bv, 11);

        var blk = BuildSolidMode11(rv, gv, bv);
        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M11Solid", bc6h, 0, 0, er, eg, eb);
            AssertPixelHDR("M11Solid", bc6h, 2, 1, er, eg, eb);
            CompareWithUnity("M11Solid", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 6. Mode 12 solid: 1 subset, 12-bit base (reversed), 8-bit delta
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode12_Solid")]
    public void TestMode12Solid()
    {
        int rv = 3000,
            gv = 2000,
            bv = 1000;
        float er = RefSolidUnsigned(rv, 12);
        float eg = RefSolidUnsigned(gv, 12);
        float eb = RefSolidUnsigned(bv, 12);

        var blk = BuildSolidMode12(rv, gv, bv);
        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M12Solid", bc6h, 0, 0, er, eg, eb);
            AssertPixelHDR("M12Solid", bc6h, 3, 3, er, eg, eb);
            CompareWithUnity("M12Solid", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 7. Mode 13 solid: 1 subset, 16-bit base (reversed), 4-bit delta
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode13_Solid")]
    public void TestMode13Solid()
    {
        // 16-bit value (max 65535)
        int rv = 30000,
            gv = 15000,
            bv = 5000;
        float er = RefSolidUnsigned(rv, 16);
        float eg = RefSolidUnsigned(gv, 16);
        float eb = RefSolidUnsigned(bv, 16);

        var blk = BuildSolidMode13(rv, gv, bv);
        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M13Solid", bc6h, 0, 0, er, eg, eb);
            AssertPixelHDR("M13Solid", bc6h, 1, 2, er, eg, eb);
            CompareWithUnity("M13Solid", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 8. Mode 0 solid: 2 subsets, 10-bit base + 5/5/5 delta
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode0_Solid")]
    public void TestMode0Solid()
    {
        int rv = 400,
            gv = 200,
            bv = 100;
        float er = RefSolidUnsigned(rv, 10);
        float eg = RefSolidUnsigned(gv, 10);
        float eb = RefSolidUnsigned(bv, 10);

        var blk = BuildSolidMode0(rv, gv, bv);
        var (bc6h, data) = Make(blk);
        try
        {
            // Partition 0: pixels 0,1 are subset 0; pixels 2,3 are subset 1
            // Both subsets have same endpoints, so all pixels should be equal
            AssertPixelHDR("M0Solid", bc6h, 0, 0, er, eg, eb);
            AssertPixelHDR("M0Solid", bc6h, 3, 3, er, eg, eb);
            CompareWithUnity("M0Solid", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 9. Mode 1 solid: 2 subsets, 7-bit base + 6/6/6 delta
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode1_Solid")]
    public void TestMode1Solid()
    {
        int rv = 80,
            gv = 40,
            bv = 20;
        float er = RefSolidUnsigned(rv, 7);
        float eg = RefSolidUnsigned(gv, 7);
        float eb = RefSolidUnsigned(bv, 7);

        var blk = BuildSolidMode1(rv, gv, bv);
        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M1Solid", bc6h, 0, 0, er, eg, eb);
            AssertPixelHDR("M1Solid", bc6h, 2, 2, er, eg, eb);
            CompareWithUnity("M1Solid", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 10. Mode 5 solid: 2 subsets, 9-bit base + 5/5/5 delta
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode5_Solid")]
    public void TestMode5Solid()
    {
        int rv = 300,
            gv = 150,
            bv = 75;
        float er = RefSolidUnsigned(rv, 9);
        float eg = RefSolidUnsigned(gv, 9);
        float eb = RefSolidUnsigned(bv, 9);

        // Mode 5: 01110 = 0x0E
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x0E, 5); // mode 5
        // rw[8:0]
        w.Write(rv, 9);
        // by[4] = 0
        w.Write(0, 1);
        // gw[8:0]
        w.Write(gv, 9);
        // gy[4] = 0
        w.Write(0, 1);
        // bw[8:0]
        w.Write(bv, 9);
        // bz[4] = 0
        w.Write(0, 1);
        // rest all 0 (deltas + partition)

        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M5Solid", bc6h, 0, 0, er, eg, eb);
            AssertPixelHDR("M5Solid", bc6h, 3, 3, er, eg, eb);
            CompareWithUnity("M5Solid", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 11. Mode 2 solid: 2 subsets, 11-bit base, 5/4/4 delta
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode2_Solid")]
    public void TestMode2Solid()
    {
        // 11-bit values
        int rv = 1200,
            gv = 600,
            bv = 300;
        float er = RefSolidUnsigned(rv, 11);
        float eg = RefSolidUnsigned(gv, 11);
        float eb = RefSolidUnsigned(bv, 11);

        // Mode 2: 00010 = 0x02
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x02, 5); // mode 2
        // rw[9:0]
        w.Write(rv & 0x3FF, 10);
        // gw[9:0]
        w.Write(gv & 0x3FF, 10);
        // bw[9:0]
        w.Write(bv & 0x3FF, 10);
        // rx[4:0] = 0 (delta)
        w.Write(0, 5);
        // rw[10]
        w.Write((rv >> 10) & 1, 1);
        // gy[3:0] = 0
        w.Write(0, 4);
        // gx[3:0] = 0
        w.Write(0, 4);
        // gw[10]
        w.Write((gv >> 10) & 1, 1);
        // bz[0] = 0
        w.Write(0, 1);
        // gz[3:0] = 0
        w.Write(0, 4);
        // bx[3:0] = 0
        w.Write(0, 4);
        // bw[10]
        w.Write((bv >> 10) & 1, 1);
        // rest all 0

        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M2Solid", bc6h, 0, 0, er, eg, eb);
            AssertPixelHDR("M2Solid", bc6h, 3, 3, er, eg, eb);
            CompareWithUnity("M2Solid", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 12. Mode 3 solid: 2 subsets, 11-bit base, 4/5/4 delta
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode3_Solid")]
    public void TestMode3Solid()
    {
        int rv = 1000,
            gv = 500,
            bv = 250;
        float er = RefSolidUnsigned(rv, 11);
        float eg = RefSolidUnsigned(gv, 11);
        float eb = RefSolidUnsigned(bv, 11);

        // Mode 3: 00110 = 0x06
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x06, 5); // mode 3
        w.Write(rv & 0x3FF, 10);
        w.Write(gv & 0x3FF, 10);
        w.Write(bv & 0x3FF, 10);
        // rx[3:0] = 0
        w.Write(0, 4);
        // rw[10]
        w.Write((rv >> 10) & 1, 1);
        // gz[4] = 0
        w.Write(0, 1);
        // gy[3:0] = 0
        w.Write(0, 4);
        // gx[4:0] = 0
        w.Write(0, 5);
        // gw[10]
        w.Write((gv >> 10) & 1, 1);
        // gz[3:0] = 0
        w.Write(0, 4);
        // bx[3:0] = 0
        w.Write(0, 4);
        // bw[10]
        w.Write((bv >> 10) & 1, 1);
        // rest all 0

        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M3Solid", bc6h, 0, 0, er, eg, eb);
            AssertPixelHDR("M3Solid", bc6h, 3, 3, er, eg, eb);
            CompareWithUnity("M3Solid", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 13. Mode 4 solid: 2 subsets, 11-bit base, 4/4/5 delta
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode4_Solid")]
    public void TestMode4Solid()
    {
        int rv = 1100,
            gv = 700,
            bv = 350;
        float er = RefSolidUnsigned(rv, 11);
        float eg = RefSolidUnsigned(gv, 11);
        float eb = RefSolidUnsigned(bv, 11);

        // Mode 4: 01010 = 0x0A
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x0A, 5); // mode 4
        w.Write(rv & 0x3FF, 10);
        w.Write(gv & 0x3FF, 10);
        w.Write(bv & 0x3FF, 10);
        // rx[3:0] = 0
        w.Write(0, 4);
        // rw[10]
        w.Write((rv >> 10) & 1, 1);
        // by[4] = 0
        w.Write(0, 1);
        // gy[3:0] = 0
        w.Write(0, 4);
        // gx[3:0] = 0
        w.Write(0, 4);
        // gw[10]
        w.Write((gv >> 10) & 1, 1);
        // bz[0] = 0
        w.Write(0, 1);
        // gz[3:0] = 0
        w.Write(0, 4);
        // bx[4:0] = 0
        w.Write(0, 5);
        // bw[10]
        w.Write((bv >> 10) & 1, 1);
        // rest all 0

        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M4Solid", bc6h, 0, 0, er, eg, eb);
            AssertPixelHDR("M4Solid", bc6h, 3, 3, er, eg, eb);
            CompareWithUnity("M4Solid", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 14. Mode 6 solid: 2 subsets, 8-bit base, 6/5/5 delta
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode6_Solid")]
    public void TestMode6Solid()
    {
        int rv = 200,
            gv = 100,
            bv = 50;
        float er = RefSolidUnsigned(rv, 8);
        float eg = RefSolidUnsigned(gv, 8);
        float eb = RefSolidUnsigned(bv, 8);

        // Mode 6: 10010 = 0x12
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x12, 5); // mode 6
        w.Write(rv, 8);
        // gz[4] = 0, by[4] = 0
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(gv, 8);
        // bz[2] = 0, gy[4] = 0
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(bv, 8);
        // rest all 0

        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M6Solid", bc6h, 0, 0, er, eg, eb);
            AssertPixelHDR("M6Solid", bc6h, 3, 3, er, eg, eb);
            CompareWithUnity("M6Solid", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 15. Mode 7 solid: 2 subsets, 8-bit base, 5/6/5 delta
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode7_Solid")]
    public void TestMode7Solid()
    {
        int rv = 180,
            gv = 120,
            bv = 60;
        float er = RefSolidUnsigned(rv, 8);
        float eg = RefSolidUnsigned(gv, 8);
        float eb = RefSolidUnsigned(bv, 8);

        // Mode 7: 10110 = 0x16
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x16, 5); // mode 7
        w.Write(rv, 8);
        // bz[0] = 0, by[4] = 0
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(gv, 8);
        // gy[5] = 0, gy[4] = 0
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(bv, 8);
        // rest all 0

        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M7Solid", bc6h, 0, 0, er, eg, eb);
            AssertPixelHDR("M7Solid", bc6h, 3, 3, er, eg, eb);
            CompareWithUnity("M7Solid", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 16. Mode 8 solid: 2 subsets, 8-bit base, 5/5/6 delta
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode8_Solid")]
    public void TestMode8Solid()
    {
        int rv = 220,
            gv = 130,
            bv = 70;
        float er = RefSolidUnsigned(rv, 8);
        float eg = RefSolidUnsigned(gv, 8);
        float eb = RefSolidUnsigned(bv, 8);

        // Mode 8: 11010 = 0x1A
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x1A, 5); // mode 8
        w.Write(rv, 8);
        // bz[1] = 0, by[4] = 0
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(gv, 8);
        // by[5] = 0, gy[4] = 0
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(bv, 8);
        // rest all 0

        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M8Solid", bc6h, 0, 0, er, eg, eb);
            AssertPixelHDR("M8Solid", bc6h, 3, 3, er, eg, eb);
            CompareWithUnity("M8Solid", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 17. Mode 9 solid: 2 subsets, 6-bit base, 6/6/6 (no transform)
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode9_Solid")]
    public void TestMode9Solid()
    {
        int rv = 40,
            gv = 20,
            bv = 10;
        float er = RefSolidUnsigned(rv, 6);
        float eg = RefSolidUnsigned(gv, 6);
        float eb = RefSolidUnsigned(bv, 6);

        // Mode 9: 11110 = 0x1E
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x1E, 5); // mode 9
        w.Write(rv, 6);
        // gz[4] = 0, bz[0] = 0, bz[1] = 0, by[4] = 0
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(gv, 6);
        // gy[5] = 0, by[5] = 0, bz[2] = 0, gy[4] = 0
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(bv, 6);
        // gz[5] = 0, bz[3] = 0, bz[5] = 0, bz[4] = 0
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(0, 1);
        // rx, gy[3:0], gx, gz[3:0], bx, by[3:0], ry, rz all set to same value
        w.Write(rv, 6); // rx
        w.Write(0, 4); // gy[3:0] = 0 (but we need subset 1 to match)
        w.Write(gv, 6); // gx
        w.Write(0, 4); // gz[3:0] = 0
        w.Write(bv, 6); // bx
        w.Write(0, 4); // by[3:0] = 0
        w.Write(rv, 6); // ry
        w.Write(rv, 6); // rz (but ry/rz are subset 1 endpoints)

        // For a proper solid block, set subset 1 endpoints to same value too.
        // Mode 9 is non-transformed, so e2/e3 are directly read.
        // gy = e2g, gz = e3g, by = e2b, bz = e3b have scattered high bits.
        // For values < 16 (fit in 4 bits), the high bits are 0 and we can
        // set the low 4 bits correctly.
        // With gv=20 and bv=10, gv > 15 so gy[4] = 1, but we wrote 0 above.
        // Let's use smaller values where all endpoints fit without high bits.

        // Re-do with values that fit in 4 low bits for delta endpoints
        // Actually for mode 9 non-transformed, all 4 endpoints per channel
        // are 6-bit values. The issue is that gy, gz, by, bz have scattered
        // high bits. For a solid block where all endpoints = same value,
        // we need to properly set the scattered bits.
        // Let's just use Unity comparison instead for this mode.

        // Simpler approach: use values <= 15 so all high bits are 0
        rv = 10;
        gv = 8;
        bv = 5;
        er = RefSolidUnsigned(rv, 6);
        eg = RefSolidUnsigned(gv, 6);
        eb = RefSolidUnsigned(bv, 6);

        blk = new byte[16];
        w = new BitWriter(blk);
        w.Write(0x1E, 5); // mode 9
        // rw
        w.Write(rv, 6);
        // gz[4]=0, bz[0]=0, bz[1]=0, by[4]=0
        w.WriteN(0, 1, 4);
        // gw
        w.Write(gv, 6);
        // gy[5]=0, by[5]=0, bz[2]=0, gy[4]=0
        w.WriteN(0, 1, 4);
        // bw
        w.Write(bv, 6);
        // gz[5]=0, bz[3]=0, bz[5]=0, bz[4]=0
        w.WriteN(0, 1, 4);
        // rx = rv
        w.Write(rv, 6);
        // gy[3:0] = gv (low 4 bits for subset 1 g endpoint 0)
        w.Write(gv, 4);
        // gx = gv
        w.Write(gv, 6);
        // gz[3:0] = gv (low 4 bits for subset 1 g endpoint 1)
        w.Write(gv, 4);
        // bx = bv
        w.Write(bv, 6);
        // by[3:0] = bv
        w.Write(bv, 4);
        // ry = rv
        w.Write(rv, 6);
        // rz = rv
        w.Write(rv, 6);
        // partition = 0
        w.Write(0, 5);

        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M9Solid", bc6h, 0, 0, er, eg, eb);
            CompareWithUnity("M9Solid", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 18. Reserved mode: should decode to black
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Reserved")]
    public void TestReservedMode()
    {
        // 0x13 = 10011 is a reserved 5-bit code
        var blk = new byte[16];
        blk[0] = 0x13;
        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("Reserved", bc6h, 0, 0, 0f, 0f, 0f);
            AssertPixelHDR("Reserved", bc6h, 3, 3, 0f, 0f, 0f);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 19. Mode 0 partition test: 2 subsets with different colors
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode0_Partition")]
    public void TestMode0Partition()
    {
        // Use Unity comparison for this complex mode
        // Partition 13: pixels 0-7 = subset 0, pixels 8-15 = subset 1
        // Build a mode 0 block with base=400 and non-zero deltas
        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x00, 2); // mode 0
        // gy[4]=0, by[4]=0, bz[4]=0
        w.Write(0, 1);
        w.Write(0, 1);
        w.Write(0, 1);
        // rw=400, gw=200, bw=100
        w.Write(400, 10);
        w.Write(200, 10);
        w.Write(100, 10);
        // rx=5 (delta +5), gz[4]=0
        w.Write(5, 5);
        w.Write(0, 1);
        // gy[3:0]=0
        w.Write(0, 4);
        // gx=3
        w.Write(3, 5);
        // bz[0]=0
        w.Write(0, 1);
        // gz[3:0]=0
        w.Write(0, 4);
        // bx=2
        w.Write(2, 5);
        // bz[1]=0
        w.Write(0, 1);
        // by[3:0]=0
        w.Write(0, 4);
        // ry=0 (delta for subset 1)
        w.Write(0, 5);
        // bz[2]=0
        w.Write(0, 1);
        // rz=0
        w.Write(0, 5);
        // bz[3]=0
        w.Write(0, 1);
        // partition = 13
        w.Write(13, 5);

        CompareWithUnity("M0Part", blk);
    }

    // ================================================================
    // 20. Mode 11 delta test: different endpoints via delta encoding
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode11_Delta")]
    public void TestMode11Delta()
    {
        // Base=1000, delta_r=100, delta_g=-50, delta_b=200
        // After transform: e1r=1100, e1g=950, e1b=1200
        int baseR = 1000,
            baseG = 1000,
            baseB = 1000;
        int deltaR = 100,
            deltaG = -50,
            deltaB = 200;

        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x07, 5); // mode 11
        w.Write(baseR & 0x3FF, 10);
        w.Write(baseG & 0x3FF, 10);
        w.Write(baseB & 0x3FF, 10);
        // rx[8:0] = deltaR (signed 9-bit)
        w.Write(deltaR & 0x1FF, 9);
        w.Write((baseR >> 10) & 1, 1);
        // gx[8:0] = deltaG (signed 9-bit: -50 in 9 bits = 0x1CE)
        w.Write(deltaG & 0x1FF, 9);
        w.Write((baseG >> 10) & 1, 1);
        // bx[8:0] = deltaB
        w.Write(deltaB & 0x1FF, 9);
        w.Write((baseB >> 10) & 1, 1);
        // Index: pixel 0 anchor (3 bits) = 0, pixels 1-15 = 15 (max weight)
        w.Write(0, 3);
        for (int i = 1; i < 16; i++)
            w.Write(15, 4);

        // Expected: pixel 0 has index=0 (weight=0) -> base endpoint
        // pixel 15 has index=15 (weight=64) -> delta endpoint
        int e0r = baseR,
            e0g = baseG,
            e0b = baseB;
        int e1r = RefSignExtend(deltaR & 0x1FF, 9) + e0r;
        int e1g = RefSignExtend(deltaG & 0x1FF, 9) + e0g;
        int e1b = RefSignExtend(deltaB & 0x1FF, 9) + e0b;

        int mask = (1 << 11) - 1;
        e0r &= mask;
        e0g &= mask;
        e0b &= mask;
        e1r &= mask;
        e1g &= mask;
        e1b &= mask;

        int uqE0r = RefUnquantize(e0r, 11, false);
        int uqE0g = RefUnquantize(e0g, 11, false);
        int uqE0b = RefUnquantize(e0b, 11, false);
        int uqE1r = RefUnquantize(e1r, 11, false);
        int uqE1g = RefUnquantize(e1g, 11, false);
        int uqE1b = RefUnquantize(e1b, 11, false);

        float expR0 = RefFinishUnquantize(uqE0r, false);
        float expG0 = RefFinishUnquantize(uqE0g, false);
        float expB0 = RefFinishUnquantize(uqE0b, false);
        float expR15 = RefFinishUnquantize(uqE1r, false);
        float expG15 = RefFinishUnquantize(uqE1g, false);
        float expB15 = RefFinishUnquantize(uqE1b, false);

        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M11Delta_p0", bc6h, 0, 0, expR0, expG0, expB0);
            AssertPixelHDR("M11Delta_p15", bc6h, 3, 3, expR15, expG15, expB15);
            CompareWithUnity("M11Delta", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 21. Multi-block: 8x8 with 4 distinct Mode 10 blocks
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_MultiBlock")]
    public void TestMultiBlock()
    {
        var block00 = BuildSolidMode10(500, 0, 0); // red
        var block10 = BuildSolidMode10(0, 500, 0); // green
        var block01 = BuildSolidMode10(0, 0, 500); // blue
        var block11 = BuildSolidMode10(500, 500, 0); // yellow

        var allBlocks = new byte[64];
        Array.Copy(block00, 0, allBlocks, 0, 16);
        Array.Copy(block10, 0, allBlocks, 16, 16);
        Array.Copy(block01, 0, allBlocks, 32, 16);
        Array.Copy(block11, 0, allBlocks, 48, 16);

        float eR = RefSolidUnsigned(500, 10);
        float e0 = RefSolidUnsigned(0, 10);

        var (bc6h, data) = Make(allBlocks, 8, 8);
        try
        {
            AssertPixelHDR("Block00", bc6h, 1, 1, eR, e0, e0);
            AssertPixelHDR("Block10", bc6h, 5, 1, e0, eR, e0);
            AssertPixelHDR("Block01", bc6h, 1, 5, e0, e0, eR);
            AssertPixelHDR("Block11", bc6h, 5, 5, eR, eR, e0);
            CompareWithUnity("MultiBlock", allBlocks, 8, 8);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 22. Non-power-of-two dimensions (12x8 = 3x2 blocks)
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_NPOT")]
    public void TestNonPowerOfTwo()
    {
        int w = 12,
            h = 8;
        int blocksX = (w + 3) / 4;
        int blocksY = (h + 3) / 4;
        var allBlocks = new byte[blocksX * blocksY * 16];

        for (int by = 0; by < blocksY; by++)
        for (int bx = 0; bx < blocksX; bx++)
        {
            int idx = (by * blocksX + bx) * 16;
            int rv = 100 + bx * 200;
            int gv = 100 + by * 200;
            int bv = 300;

            var blk = BuildSolidMode10(rv, gv, bv);
            Array.Copy(blk, 0, allBlocks, idx, 16);
        }

        var (bc6h, data) = Make(allBlocks, w, h);
        try
        {
            for (int by = 0; by < blocksY; by++)
            for (int bx = 0; bx < blocksX; bx++)
            {
                int px = bx * 4 + 1;
                int py = by * 4 + 1;
                int rv = 100 + bx * 200;
                int gv = 100 + by * 200;
                float er = RefSolidUnsigned(rv, 10);
                float eg = RefSolidUnsigned(gv, 10);
                float eb = RefSolidUnsigned(300, 10);
                AssertPixelHDR($"NPOT({bx},{by})", bc6h, px, py, er, eg, eb);
            }
            CompareWithUnity("NPOT", allBlocks, w, h);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 23. Mip levels: 8x8 with 2 mips
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mip")]
    public void TestMipLevels()
    {
        var greenBlock = BuildSolidMode10(0, 600, 0);
        var blueBlock = BuildSolidMode10(0, 0, 800);

        // Mip 0: 8x8 = 4 blocks, Mip 1: 4x4 = 1 block
        var bytes = new byte[80];
        for (int i = 0; i < 4; i++)
            Array.Copy(greenBlock, 0, bytes, i * 16, 16);
        Array.Copy(blueBlock, 0, bytes, 64, 16);

        var (bc6h, data) = Make(bytes, 8, 8, 2);
        try
        {
            float eG = RefSolidUnsigned(600, 10);
            float eB = RefSolidUnsigned(800, 10);
            AssertPixelHDR("Mip0", bc6h, 0, 0, 0f, eG, 0f);
            Color mip1 = bc6h.GetPixel(0, 0, 1);
            assertFloatEquals("Mip1.b", mip1.b, eB, BC6HTol);
            assertFloatEquals("Mip1.r", mip1.r, 0f, BC6HTol);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 24. Coordinate clamping
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Clamp")]
    public void TestCoordClamping()
    {
        var blk = BuildSolidMode10(500, 300, 100);
        var (bc6h, data) = Make(blk);
        try
        {
            Color corner = bc6h.GetPixel(0, 0);
            assertColorEquals("ClampNeg", bc6h.GetPixel(-1, -1), corner, BC6HTol);
            assertColorEquals("ClampNegX", bc6h.GetPixel(-100, 0), corner, BC6HTol);
            Color far = bc6h.GetPixel(3, 3);
            assertColorEquals("ClampOver", bc6h.GetPixel(100, 100), far, BC6HTol);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 25. Constructor validation
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Ctor")]
    public void TestConstructorValidation()
    {
        // Correct size
        var goodData = new NativeArray<byte>(16, Allocator.Temp);
        try
        {
            new CPUTexture2D.BC6H(goodData, 4, 4, 1);
        }
        finally
        {
            goodData.Dispose();
        }

        // Too small
        var smallData = new NativeArray<byte>(15, Allocator.Temp);
        try
        {
            bool threw = false;
            try
            {
                new CPUTexture2D.BC6H(smallData, 4, 4, 1);
            }
            catch (Exception)
            {
                threw = true;
            }
            if (!threw)
                throw new Exception("BC6H.Ctor: expected exception for undersized data");
        }
        finally
        {
            smallData.Dispose();
        }

        // Too large
        var largeData = new NativeArray<byte>(17, Allocator.Temp);
        try
        {
            bool threw = false;
            try
            {
                new CPUTexture2D.BC6H(largeData, 4, 4, 1);
            }
            catch (Exception)
            {
                threw = true;
            }
            if (!threw)
                throw new Exception("BC6H.Ctor: expected exception for oversized data");
        }
        finally
        {
            largeData.Dispose();
        }

        // Multi-mip
        var mipData = new NativeArray<byte>(80, Allocator.Temp);
        try
        {
            new CPUTexture2D.BC6H(mipData, 8, 8, 2);
        }
        finally
        {
            mipData.Dispose();
        }
    }

    // ================================================================
    // 26. GetPixel32 rounds HDR to [0,255]
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Pixel32")]
    public void TestGetPixel32()
    {
        var blk = BuildSolidMode10(500, 300, 100);
        var (bc6h, data) = Make(blk);
        try
        {
            Color pixel = bc6h.GetPixel(0, 0);
            Color32 actual32 = bc6h.GetPixel32(0, 0);
            Color32 expected32 = pixel;
            assertColor32Equals("Pixel32", actual32, expected32, 0);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 27. GetRawTextureData returns correct bytes
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_RawData")]
    public void TestRawTextureData()
    {
        var blk = BuildSolidMode10(512, 256, 128);
        var (bc6h, data) = Make(blk);
        try
        {
            var raw = bc6h.GetRawTextureData<byte>();
            if (raw.Length != blk.Length)
                throw new Exception($"BC6H.RawData: length {raw.Length} != expected {blk.Length}");
            for (int i = 0; i < blk.Length; i++)
                if (raw[i] != blk[i])
                    throw new Exception($"BC6H.RawData[{i}]: {raw[i]} != expected {blk[i]}");
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 28. Comprehensive Unity comparison: gradient across all modes
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_VsUnity_Gradient")]
    public void TestVsUnityGradient()
    {
        // 16x16 texture = 4x4 blocks using Mode 10 with gradients
        var blockData = new byte[16 * 16];
        for (int by = 0; by < 4; by++)
        for (int bx = 0; bx < 4; bx++)
        {
            int r0 = bx * 200 + 50;
            int r1 = bx * 200 + 150;
            int g0 = by * 200 + 50;
            int g1 = by * 200 + 150;
            int b0 = 100;
            int b1 = 900;

            var block = BuildGradientMode10(r0, r1, g0, g1, b0, b1);
            Array.Copy(block, 0, blockData, (by * 4 + bx) * 16, 16);
        }

        var tex = new Texture2D(16, 16, TextureFormat.BC6H, false);
        tex.LoadRawTextureData(blockData);
        tex.Apply(false, false);

        var nativeCopy = new NativeArray<byte>(blockData, Allocator.Temp);
        try
        {
            var bc6h = new CPUTexture2D.BC6H(nativeCopy, 16, 16, 1);
            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                Color expected = tex.GetPixel(x, y);
                Color actual = bc6h.GetPixel(x, y);
                assertColorEquals($"Gradient({x},{y})", actual, expected, BC6HTol);
            }
        }
        finally
        {
            nativeCopy.Dispose();
            UnityEngine.Object.Destroy(tex);
        }
    }

    // ================================================================
    // 29. VsUnity multi-mode: compare various modes against Unity
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_VsUnity_Modes")]
    public void TestVsUnityModes()
    {
        // Build one block per mode and compare
        CompareWithUnity("VsM0", BuildSolidMode0(400, 200, 100));
        CompareWithUnity("VsM1", BuildSolidMode1(80, 40, 20));
        CompareWithUnity("VsM10", BuildSolidMode10(512, 256, 128));
        CompareWithUnity("VsM11", BuildSolidMode11(1500, 800, 200));
        CompareWithUnity("VsM12", BuildSolidMode12(3000, 2000, 1000));
        CompareWithUnity("VsM13", BuildSolidMode13(30000, 15000, 5000));
    }

    // ================================================================
    // 30. Alpha is always 1.0
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Alpha")]
    public void TestAlphaAlwaysOne()
    {
        var blk = BuildGradientMode10(0, 1023, 0, 1023, 0, 1023);
        var (bc6h, data) = Make(blk);
        try
        {
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                Color c = bc6h.GetPixel(x, y);
                assertFloatEquals($"Alpha({x},{y})", c.a, 1f, 0.0001f);
            }
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 31. Mode 10 asymmetric channels: R,G,B have different values
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode10_Asymmetric")]
    public void TestMode10AsymmetricChannels()
    {
        int rv = 1023,
            gv = 0,
            bv = 512;
        float er = RefSolidUnsigned(rv, 10);
        float eg = RefSolidUnsigned(gv, 10);
        float eb = RefSolidUnsigned(bv, 10);

        var blk = BuildSolidMode10(rv, gv, bv);
        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M10Asym", bc6h, 0, 0, er, eg, eb);
            CompareWithUnity("M10Asym", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 32. Mode 11 with negative delta
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode11_NegDelta")]
    public void TestMode11NegativeDelta()
    {
        // Base = 1500, delta = -200 in 9 bits
        int baseVal = 1500;
        int delta = -200;

        var blk = new byte[16];
        var w = new BitWriter(blk);
        w.Write(0x07, 5); // mode 11
        w.Write(baseVal & 0x3FF, 10);
        w.Write(baseVal & 0x3FF, 10);
        w.Write(baseVal & 0x3FF, 10);
        w.Write(delta & 0x1FF, 9);
        w.Write((baseVal >> 10) & 1, 1);
        w.Write(delta & 0x1FF, 9);
        w.Write((baseVal >> 10) & 1, 1);
        w.Write(delta & 0x1FF, 9);
        w.Write((baseVal >> 10) & 1, 1);
        // pixel 0 anchor = 0, pixel 15 = 15
        w.Write(0, 3);
        for (int i = 1; i < 16; i++)
            w.Write(15, 4);

        // pixel 0: base endpoint
        int e0 = baseVal;
        int e1 = RefSignExtend(delta & 0x1FF, 9) + e0;
        int mask = (1 << 11) - 1;
        e0 &= mask;
        e1 &= mask;
        float expBase = RefFinishUnquantize(RefUnquantize(e0, 11, false), false);
        float expDelta = RefFinishUnquantize(RefUnquantize(e1, 11, false), false);

        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M11NegD_p0", bc6h, 0, 0, expBase, expBase, expBase);
            AssertPixelHDR("M11NegD_p15", bc6h, 3, 3, expDelta, expDelta, expDelta);
            CompareWithUnity("M11NegD", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 33. Signed mode basic test
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Signed")]
    public void TestSignedMode()
    {
        // Mode 10, unsigned endpoint val=256, but decode as signed
        // In signed mode, Unquantize treats val as non-negative (256 < 511)
        int rv = 256;
        int uq = RefUnquantize(rv, 10, true);
        float expected = RefFinishUnquantize(uq, true);

        var blk = BuildSolidMode10(rv, rv, rv);
        var (bc6h, data) = Make(blk, signed: true);
        try
        {
            AssertPixelHDR("Signed", bc6h, 0, 0, expected, expected, expected);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 34. Signed mode zero
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Signed_Zero")]
    public void TestSignedZero()
    {
        var blk = BuildSolidMode10(0, 0, 0);
        var (bc6h, data) = Make(blk, signed: true);
        try
        {
            AssertPixelHDR("SignedZero", bc6h, 0, 0, 0f, 0f, 0f);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 35. Mode 12 ReverseBits test: verify high bits are correctly decoded
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode12_ReverseBits")]
    public void TestMode12ReverseBits()
    {
        // Test with a value where the high 2 bits matter
        // rv = 0b1100_0000_0000 = 3072 (12-bit), high 2 bits = 11
        // gv = 0b0100_0000_0000 = 1024, high 2 bits = 01
        // bv = 0b1000_0000_0000 = 2048, high 2 bits = 10
        int rv = 3072,
            gv = 1024,
            bv = 2048;
        float er = RefSolidUnsigned(rv, 12);
        float eg = RefSolidUnsigned(gv, 12);
        float eb = RefSolidUnsigned(bv, 12);

        var blk = BuildSolidMode12(rv, gv, bv);
        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M12Rev", bc6h, 0, 0, er, eg, eb);
            CompareWithUnity("M12Rev", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 36. Mode 13 ReverseBits test: verify high 6 bits are correctly decoded
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode13_ReverseBits")]
    public void TestMode13ReverseBits()
    {
        // 16-bit value with interesting high bits
        // rv = 0xABCD = 43981, high 6 bits = 0b101011 = 43
        // gv = 0x1234 = 4660, high 6 bits = 0b000100 = 4
        // bv = 0xFFFF = 65535, high 6 bits = 0b111111 = 63
        int rv = 0xABCD,
            gv = 0x1234,
            bv = 0xFFFF;
        float er = RefSolidUnsigned(rv, 16);
        float eg = RefSolidUnsigned(gv, 16);
        float eb = RefSolidUnsigned(bv, 16);

        var blk = BuildSolidMode13(rv, gv, bv);
        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M13Rev", bc6h, 0, 0, er, eg, eb, 0.01f);
            CompareWithUnity("M13Rev", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 37. Mode 11 max value
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Mode11_Max")]
    public void TestMode11Max()
    {
        // Max 11-bit value = 2047
        float eMax = RefSolidUnsigned(2047, 11);
        var blk = BuildSolidMode11(2047, 2047, 2047);
        var (bc6h, data) = Make(blk);
        try
        {
            AssertPixelHDR("M11Max", bc6h, 0, 0, eMax, eMax, eMax, 1f);
            CompareWithUnity("M11Max", blk);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 38. VsUnity comprehensive: 8x8 with mixed modes
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_VsUnity_Mixed")]
    public void TestVsUnityMixed()
    {
        var b0 = BuildSolidMode10(800, 100, 200);
        var b1 = BuildSolidMode11(1800, 400, 50);
        var b2 = BuildSolidMode0(300, 600, 900);
        var b3 = BuildGradientMode10(100, 900, 50, 700, 200, 800);

        var blockData = new byte[64];
        Array.Copy(b0, 0, blockData, 0, 16);
        Array.Copy(b1, 0, blockData, 16, 16);
        Array.Copy(b2, 0, blockData, 32, 16);
        Array.Copy(b3, 0, blockData, 48, 16);

        CompareWithUnity("VsMixed", blockData, 8, 8);
    }

    // ================================================================
    // 39. Bilinear filtering
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Bilinear")]
    public void TestBilinear()
    {
        var blk = BuildGradientMode10(100, 900, 50, 700, 200, 800);
        var (bc6h, data) = Make(blk);
        try
        {
            // Center of pixel (0,0) is at u=0.125, v=0.125 for a 4x4 texture
            Color center = bc6h.GetPixelBilinear(0.125f, 0.125f);
            Color direct = bc6h.GetPixel(0, 0);
            assertColorEquals("Bilinear_center", center, direct, BC6HTol);
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 40. Format property
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_Format")]
    public void TestFormatProperty()
    {
        var blk = BuildSolidMode10(0, 0, 0);
        var (bc6h, data) = Make(blk);
        try
        {
            if (bc6h.Format != TextureFormat.BC6H)
                throw new Exception(
                    $"BC6H.Format: expected {TextureFormat.BC6H}, got {bc6h.Format}"
                );
            if (bc6h.Width != 4)
                throw new Exception($"BC6H.Width: expected 4, got {bc6h.Width}");
            if (bc6h.Height != 4)
                throw new Exception($"BC6H.Height: expected 4, got {bc6h.Height}");
            if (bc6h.MipCount != 1)
                throw new Exception($"BC6H.MipCount: expected 1, got {bc6h.MipCount}");
        }
        finally
        {
            data.Dispose();
        }
    }

    // ---- Helper: build blocks spanning all 14 modes ----

    /// <summary>
    /// Builds a 16x16 texture (4x4 = 16 blocks) with one solid block per BC6H mode
    /// (0-13) plus 2 gradient blocks, covering all 14 modes.
    /// Returns the raw block data (256 bytes = 16 blocks * 16 bytes).
    /// Texture dimensions: 16x16 (4 blocks wide, 4 blocks tall).
    /// </summary>
    byte[] BuildAllModeBlocks()
    {
        var blocks = new byte[16 * 16];
        // Row 0 (y=0..3): modes 0, 1, 2, 3
        // Mode 0: 10-bit base, 5/5/5 delta. Values fit in 10 bits.
        Array.Copy(BuildSolidMode0(300, 600, 900), 0, blocks, 0 * 16, 16);
        // Mode 1: 7-bit base, 6/6/6 delta. Values fit in 7 bits (max 127).
        Array.Copy(BuildSolidMode1(100, 50, 120), 0, blocks, 1 * 16, 16);
        // Mode 2: 11-bit base (10+1), 5/4/4 delta. Values fit in 11 bits (max 2047).
        Array.Copy(BuildSolidMode2(1500, 800, 400), 0, blocks, 2 * 16, 16);
        // Mode 3: 11-bit base, 4/5/4 delta. Values fit in 11 bits.
        Array.Copy(BuildSolidMode3(1200, 600, 300), 0, blocks, 3 * 16, 16);

        // Row 1 (y=4..7): modes 4, 5, 6, 7
        // Mode 4: 11-bit base, 4/4/5 delta. Values fit in 11 bits.
        Array.Copy(BuildSolidMode4(900, 450, 1800), 0, blocks, 4 * 16, 16);
        // Mode 5: 9-bit base, 5/5/5 delta. Values fit in 9 bits (max 511).
        Array.Copy(BuildSolidMode5(400, 200, 300), 0, blocks, 5 * 16, 16);
        // Mode 6: 8-bit base, 6/5/5 delta. Values fit in 8 bits (max 255).
        Array.Copy(BuildSolidMode6(200, 100, 150), 0, blocks, 6 * 16, 16);
        // Mode 7: 8-bit base, 5/6/5 delta. Values fit in 8 bits.
        Array.Copy(BuildSolidMode7(180, 90, 130), 0, blocks, 7 * 16, 16);

        // Row 2 (y=8..11): modes 8, 9, 10, 11
        // Mode 8: 8-bit base, 5/5/6 delta. Values fit in 8 bits.
        Array.Copy(BuildSolidMode8(220, 110, 170), 0, blocks, 8 * 16, 16);
        // Mode 9: 6-bit base, 6/6/6, NOT transformed. Values fit in 6 bits (max 63).
        Array.Copy(BuildSolidMode9(50, 30, 40), 0, blocks, 9 * 16, 16);
        // Mode 10: 10-bit direct, no delta. Values fit in 10 bits.
        Array.Copy(BuildSolidMode10(800, 100, 500), 0, blocks, 10 * 16, 16);
        // Mode 11: 11-bit base (10+1), 9/9/9 delta, transformed. Values fit in 11 bits.
        Array.Copy(BuildSolidMode11(1800, 400, 50), 0, blocks, 11 * 16, 16);

        // Row 3 (y=12..15): modes 12, 13, + 2 gradient blocks
        // Mode 12: 12-bit base (10+2 reversed), 8/8/8 delta. Values fit in 12 bits.
        Array.Copy(BuildSolidMode12(2000, 1000, 500), 0, blocks, 12 * 16, 16);
        // Mode 13: 16-bit base (10+6 reversed), 4/4/4 delta. Values fit in 16 bits.
        Array.Copy(BuildSolidMode13(40000, 20000, 10000), 0, blocks, 13 * 16, 16);
        // Gradient blocks using mode 10 for additional coverage
        Array.Copy(BuildGradientMode10(100, 900, 50, 700, 200, 800), 0, blocks, 14 * 16, 16);
        Array.Copy(BuildGradientMode10(50, 500, 300, 800, 100, 600), 0, blocks, 15 * 16, 16);
        return blocks;
    }

    // ================================================================
    // 41. GetPixels returns the same colors as GetPixel for all 14 modes
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_GetPixels")]
    public void TestBC6HGetPixels()
    {
        // 16x16 = 4x4 blocks, one per BC6H mode (0-13) + 2 gradient blocks
        var allBlocks = BuildAllModeBlocks();

        var (bc6h, data) = Make(allBlocks, 16, 16);
        try
        {
            var pixels = bc6h.GetPixels();

            if (pixels.Length != 256)
                throw new Exception($"BC6H.GetPixels: expected 256 pixels, got {pixels.Length}");

            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                Color expected = bc6h.GetPixel(x, y);
                Color actual = pixels[y * 16 + x];
                assertColorEquals($"BC6H.GetPixels({x},{y})", actual, expected, 1e-6f);
            }
        }
        finally
        {
            data.Dispose();
        }
    }

    // ================================================================
    // 42. GetPixels32 returns the same colors as GetPixel32 for all 14 modes
    // ================================================================

    [TestInfo("CPUTexture2D_BC6H_GetPixels32")]
    public void TestBC6HGetPixels32()
    {
        // 16x16 = 4x4 blocks, one per BC6H mode (0-13) + 2 gradient blocks
        var allBlocks = BuildAllModeBlocks();

        var (bc6h, data) = Make(allBlocks, 16, 16);
        try
        {
            var pixels = bc6h.GetPixels32();

            if (pixels.Length != 256)
                throw new Exception($"BC6H.GetPixels32: expected 256 pixels, got {pixels.Length}");

            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                Color32 expected = bc6h.GetPixel32(x, y);
                Color32 actual = pixels[y * 16 + x];
                assertColor32Equals($"BC6H.GetPixels32({x},{y})", actual, expected, 0);
            }
        }
        finally
        {
            data.Dispose();
        }
    }
}
