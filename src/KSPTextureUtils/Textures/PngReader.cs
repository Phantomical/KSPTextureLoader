using System.Numerics;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.ColorSpaces.Companding;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace KSPTextureUtils.Textures;

/// <summary>
/// Decodes a PNG to an <see cref="UnityTextureFormat.RGBA32"/> texture with a full
/// generated mip chain. Unity's RGBA32 stream layout is tightly-packed R,G,B,A
/// bytes per pixel, in the same row order ImageSharp produces, so the decoded
/// pixels are copied directly; generated mips are concatenated largest first in the
/// same layout.
///
/// Mips are produced with a Kaiser-windowed sinc filter (see
/// <see cref="KaiserResampler"/>) operating in linear light: the PNG is sRGB, so
/// its colour channels are expanded to linear before resampling and re-companded to
/// sRGB afterwards. Resampling in the encoded (gamma) space would darken downscaled
/// results.
/// </summary>
internal static class PngReader
{
    /// <summary>
    /// Load a PNG's base image as tightly-packed RGBA32, with no mip chain — for
    /// callers (e.g. the palette converter) that only need the source pixels.
    /// Returns a null pixel array and a message when the file can't be decoded.
    /// </summary>
    public static (byte[]? pixels, int width, int height, string? error) ReadRgba32(string path)
    {
        try
        {
            using var image = Image.Load<Rgba32>(path);
            var pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);
            return (pixels, image.Width, image.Height, null);
        }
        catch (Exception ex)
        {
            return (null, 0, 0, $"PNG decode failed: {ex.Message}");
        }
    }

    public static (SourceTexture? texture, SkippedTexture? skip) Read(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        try
        {
            using var image = Image.Load<Rgba32>(path);
            int width = image.Width;
            int height = image.Height;

            int mipCount = MipChainLength(width, height);
            var data = new byte[
                TextureFormatInfo.MipChainSize(UnityTextureFormat.RGBA32, width, height, mipCount)
            ];

            // Mip 0 is the source pixels copied verbatim (no resampling, so the base
            // level is a lossless copy of the input).
            int offset = (int)TextureFormatInfo.MipSize(UnityTextureFormat.RGBA32, width, height);
            image.CopyPixelDataTo(data.AsSpan(0, offset));

            if (mipCount > 1)
            {
                // Downsample in linear light: clone to full-precision float pixels and
                // expand sRGB -> linear once, then resample each smaller level from this
                // source. Resampling from full resolution (rather than chaining halvings)
                // lets the Kaiser kernel see the original texels at every level.
                using var linear = image.CloneAs<RgbaVector>();
                Compand(linear, expand: true);

                var kaiser = new KaiserResampler();
                int w = width,
                    h = height;
                for (int level = 1; level < mipCount; level++)
                {
                    w = Math.Max(1, w >> 1);
                    h = Math.Max(1, h >> 1);

                    using var mip = linear.Clone(ctx => ctx.Resize(w, h, kaiser));
                    Compand(mip, expand: false); // linear -> sRGB

                    using var mip8 = mip.CloneAs<Rgba32>();
                    int size = (int)TextureFormatInfo.MipSize(UnityTextureFormat.RGBA32, w, h);
                    mip8.CopyPixelDataTo(data.AsSpan(offset, size));
                    offset += size;
                }
            }

            return (
                new SourceTexture
                {
                    Name = name,
                    Width = width,
                    Height = height,
                    MipCount = mipCount,
                    Format = UnityTextureFormat.RGBA32,
                    ColorSpace = 1,
                    Data = data,
                    SourcePath = path,
                },
                null
            );
        }
        catch (Exception ex)
        {
            return (
                null,
                new SkippedTexture
                {
                    SourcePath = path,
                    Reason = SkipReason.Invalid,
                    Detail = $"PNG decode failed: {ex.Message}",
                }
            );
        }
    }

    /// <summary>
    /// Apply sRGB companding to every pixel's colour channels in place.
    /// <paramref name="expand"/> = true expands sRGB -> linear; false compresses
    /// linear -> sRGB. <see cref="RgbaVector"/> is laid out as R,G,B,A floats, so its
    /// rows reinterpret directly as <see cref="Vector4"/>; <see cref="SRgbCompanding"/>
    /// transforms only X/Y/Z and leaves the alpha channel untouched.
    /// </summary>
    static void Compand(Image<RgbaVector> image, bool expand)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Vector4> row = MemoryMarshal.Cast<RgbaVector, Vector4>(accessor.GetRowSpan(y));
                if (expand)
                    SRgbCompanding.Expand(row);
                else
                    SRgbCompanding.Compress(row);
            }
        });
    }

    /// <summary>
    /// Number of mip levels in a full chain for <paramref name="width"/> x
    /// <paramref name="height"/>: <c>floor(log2(max(w, h))) + 1</c>, matching the
    /// chain Unity builds (down to the 1x1 level).
    /// </summary>
    static int MipChainLength(int width, int height)
    {
        int size = Math.Max(width, height);
        int levels = 1;
        while (size > 1)
        {
            size >>= 1;
            levels++;
        }
        return levels;
    }
}
