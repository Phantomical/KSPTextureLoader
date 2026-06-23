using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace KSPTextureBundler.Textures;

/// <summary>
/// Decodes a PNG to a single-mip <see cref="UnityTextureFormat.RGBA32"/> texture.
/// Unity's RGBA32 stream layout is tightly-packed R,G,B,A bytes per pixel, in the
/// same row order ImageSharp produces, so the decoded pixels are copied directly.
/// </summary>
internal static class PngReader
{
    public static (SourceTexture? texture, SkippedTexture? skip) Read(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        try
        {
            using var image = Image.Load<Rgba32>(path);
            int width = image.Width;
            int height = image.Height;

            var data = new byte[(long)width * height * 4];
            image.CopyPixelDataTo(data);

            return (
                new SourceTexture
                {
                    Name = name,
                    Width = width,
                    Height = height,
                    MipCount = 1,
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
}
