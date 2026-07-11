using System;
using Unity.Burst.CompilerServices;
using Unity.Burst.Intrinsics;
using Unity.Mathematics;
using static Unity.Burst.Intrinsics.X86;

namespace KSPTextureLoader.Utils;

internal struct float8
{
    float4 lo;
    float4 hi;

    unsafe v256 v
    {
        get
        {
            var self = this;
            return *(v256*)&self;
        }
    }

    public unsafe float this[int i]
    {
        [IgnoreWarning(1370)]
        readonly get
        {
            if ((uint)i >= 8)
                throw new IndexOutOfRangeException();

            fixed (float8* self = &this)
                return ((float*)self)[i];
        }
        [IgnoreWarning(1370)]
        set
        {
            if ((uint)i >= 8)
                throw new IndexOutOfRangeException();

            fixed (float8* self = &this)
                ((float*)self)[i] = value;
        }
    }

    public float8(v256 v)
    {
        lo = new(v.Float0, v.Float1, v.Float2, v.Float3);
        hi = new(v.Float4, v.Float5, v.Float6, v.Float7);
    }

    public float8(float x)
        : this(new v256(x)) { }

    public float8(float4 lo, float4 hi)
    {
        this.lo = lo;
        this.hi = hi;
    }

    public float8(float e0, float e1, float e2, float e3, float e4, float e5, float e6, float e7)
        : this(new v256(e0, e1, e2, e3, e4, e5, e6, e7)) { }

    public static float8 fma(float8 a, float8 b, float8 c)
    {
        if (Fma.IsFmaSupported)
            return Fma.mm256_fmadd_ps(a.v, b.v, c.v);
        else
            return a * b + c;
    }

    public static implicit operator float8(v256 v) => new(v);

    public static implicit operator v256(float8 v) => v.v;

    public static float8 operator +(float8 a, float8 b)
    {
        if (Avx.IsAvxSupported)
            return Avx.mm256_add_ps(a, b);

        return new(a.lo + b.lo, a.hi + b.hi);
    }

    public static float8 operator +(float8 a, float b) => a + new float8(b);

    public static float8 operator +(float a, float8 b) => new float8(a) + b;

    public static float8 operator -(float8 a, float8 b)
    {
        if (Avx.IsAvxSupported)
            return Avx.mm256_sub_ps(a, b);

        return new(a.lo - b.lo, a.hi - b.hi);
    }

    public static float8 operator -(float8 a, float b) => a - new float8(b);

    public static float8 operator -(float a, float8 b) => new float8(a) - b;

    public static float8 operator *(float8 a, float8 b)
    {
        if (Avx.IsAvxSupported)
            return Avx.mm256_mul_ps(a, b);

        return new(a.lo * b.lo, a.hi * b.hi);
    }

    public static float8 operator *(float8 a, float b) => a * new float8(b);

    public static float8 operator *(float a, float8 b) => new float8(a) * b;

    public static float8 operator /(float8 a, float8 b)
    {
        if (Avx.IsAvxSupported)
            return Avx.mm256_div_ps(a, b);

        return new(a.lo / b.lo, a.hi / b.hi);
    }

    public static float8 operator /(float8 a, float b) => a / new float8(b);

    public static float8 operator /(float a, float8 b) => new float8(a) / b;

    public static float8 operator %(float8 a, float8 b) => new(a.lo % b.lo, a.hi % b.hi);

    public static float8 operator %(float8 a, float b) => a % new float8(b);

    public static float8 operator %(float a, float8 b) => new float8(a) % b;

    public static float8 operator -(float8 a)
    {
        if (Avx.IsAvxSupported)
            return Avx.mm256_xor_ps(a, Avx.mm256_set1_ps(-0.0f));

        return new(-a.lo, -a.hi);
    }

    public static float8 operator +(float8 a) => a;

    public static float8 operator ++(float8 a) => a + new float8(1f);

    public static float8 operator --(float8 a) => a - new float8(1f);
}
