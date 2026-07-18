using KSPTextureUtils.Textures;
using Xunit;

namespace KSPTextureUtils.Tests;

/// <summary>
/// Synthetic pixel payloads for the shape tests. Every surface/mip gets its own
/// fill byte, so a payload that survives a round trip proves not just that the
/// byte count matched but that each face, slice and mip landed in the right place.
/// </summary>
internal static class ShapeFixtures
{
    /// <summary>
    /// The number of independent surfaces a shape stores back to back: six mip
    /// chains per cubemap, one per array slice. A 3D texture is a single surface
    /// whose mips each carry the whole slice stack, so it counts as one.
    /// </summary>
    public static int SurfaceCount(TextureKind kind, int layers) =>
        kind switch
        {
            TextureKind.Cubemap => 6,
            TextureKind.CubemapArray => 6 * layers,
            TextureKind.Texture2DArray => layers,
            _ => 1,
        };

    /// <summary>
    /// Build a payload of exactly the size the shape requires, tagging surface
    /// <c>s</c> mip <c>m</c> with the byte <c>s * 16 + m</c>.
    /// </summary>
    public static byte[] Payload(
        UnityTextureFormat format,
        TextureKind kind,
        int width,
        int height,
        int layers,
        int mips
    )
    {
        long total = TextureFormatInfo.ShapeSize(format, kind, width, height, layers, mips);
        var data = new byte[total];

        int surfaces = SurfaceCount(kind, layers);
        int pos = 0;
        for (int s = 0; s < surfaces; s++)
        {
            int w = width,
                h = height,
                d = kind == TextureKind.Texture3D ? layers : 1;
            for (int m = 0; m < mips; m++)
            {
                int n = (int)(TextureFormatInfo.MipSize(format, w, h) * d);
                Array.Fill(data, (byte)(s * 16 + m), pos, n);
                pos += n;
                w = Math.Max(1, w >> 1);
                h = Math.Max(1, h >> 1);
                d = Math.Max(1, d >> 1);
            }
        }

        Assert.Equal(data.Length, pos); // the fill must cover the shape exactly
        return data;
    }

    /// <summary>A temp directory that deletes itself at the end of a test.</summary>
    public sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("ksptexutil-tests-").FullName;

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException) { }
        }
    }
}
