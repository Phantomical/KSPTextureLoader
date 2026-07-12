using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing.Processors.Transforms;

namespace KSPTextureUtils.Textures;

/// <summary>
/// A Kaiser-windowed sinc resampler.
/// </summary>
///
/// <remarks>
/// The kernel is <c>sinc(x) * I0(beta * sqrt(1 - (x/radius)^2)) / I0(beta)</c> for
/// <c>|x| &lt; radius</c>, where <c>I0</c> is the zeroth-order modified Bessel
/// function.
/// </remarks>
internal readonly struct KaiserResampler : IResampler
{
    readonly float radius;
    readonly float beta;
    readonly float invI0Beta;

    /// <summary>Constructs the default mipmap-tuned kernel (radius 3, beta 4).</summary>
    public KaiserResampler()
        : this(3f, 4f) { }

    /// <param name="radius">Filter support radius (NVTT uses 3).</param>
    /// <param name="beta">Kaiser window shape parameter (NVTT uses 4).</param>
    public KaiserResampler(float radius, float beta)
    {
        this.radius = radius;
        this.beta = beta;
        invI0Beta = 1f / BesselI0(beta);
    }

    public float Radius => radius;

    public float GetValue(float x)
    {
        if (x < 0f)
            x = -x;
        if (x >= radius)
            return 0f;

        float t = x / radius;
        float window = BesselI0(beta * MathF.Sqrt(1f - t * t)) * invI0Beta;
        return SinC(x) * window;
    }

    public void ApplyTransform<TPixel>(IResamplingTransformImageProcessor<TPixel> processor)
        where TPixel : unmanaged, IPixel<TPixel> => processor.ApplyTransform(in this);

    /// <summary>Normalised sinc: <c>sin(pi x) / (pi x)</c>, with <c>sinc(0) = 1</c>.</summary>
    static float SinC(float x)
    {
        if (x == 0f)
            return 1f;
        float px = MathF.PI * x;
        return MathF.Sin(px) / px;
    }

    /// <summary>
    /// Zeroth-order modified Bessel function of the first kind, via its power series
    /// <c>sum (x^2/4)^k / (k!)^2</c>. Converges quickly for the small arguments used
    /// here (|x| &lt;= beta).
    /// </summary>
    static float BesselI0(float x)
    {
        float sum = 1f;
        float term = 1f;
        float halfXSquared = x * x / 4f;
        for (int k = 1; k < 64; k++)
        {
            term *= halfXSquared / (k * k);
            sum += term;
            if (term < sum * 1e-7f)
                break;
        }
        return sum;
    }
}
