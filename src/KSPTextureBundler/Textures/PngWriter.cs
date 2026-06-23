using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace KSPTextureBundler.Textures;

/// <summary>
/// Encodes a texture's top mip back to a PNG — the inverse of <see cref="PngReader"/>.
/// <c>build</c> decodes PNG inputs to <see cref="UnityTextureFormat.RGBA32"/>, so a
/// bundle built from a PNG round-trips back to a PNG losslessly. Only the formats a
/// PNG-built texture can hold are supported; everything else stays a DDS.
/// </summary>
internal static class PngWriter
{
    public static bool CanWrite(UnityTextureFormat format) =>
        format == UnityTextureFormat.RGBA32;

    public static byte[] Write(
        UnityTextureFormat format,
        int width,
        int height,
        ReadOnlySpan<byte> data
    )
    {
        if (format != UnityTextureFormat.RGBA32)
            throw new NotSupportedException($"cannot write {format} to PNG");

        // PNG holds a single image; take only the top mip. Unity's RGBA32 layout is
        // tightly-packed R,G,B,A in ImageSharp's row order, so the bytes map directly.
        int topMip = width * height * 4;
        using var image = Image.LoadPixelData<Rgba32>(data.Slice(0, topMip), width, height);
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }
}
