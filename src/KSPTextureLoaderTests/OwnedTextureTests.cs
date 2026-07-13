using System;
using System.IO;
using KSP.Testing;
using KSPTextureLoader;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoaderTests;

/// <summary>
/// Integration tests for the <c>TextureLoader.LoadOwned*</c> family, which upload
/// raw (already-decoded) pixel data supplied by the caller rather than loading a
/// DDS file from disk.
///
/// Every texture type is exercised across two axes:
/// <list type="bullet">
///   <item><b>source</b>: the in-memory <c>NativeArray</c> overload, and the
///   <c>(path, offset, length)</c> file-region overload (written to a temp file
///   behind a few padding bytes so the <c>offset</c> parameter is actually
///   exercised); and</item>
///   <item><b>readability</b>: a <b>readable</b> load does a full pixel round-trip
///   (build a known buffer, upload, read it back, compare), while an
///   <b>unreadable</b> load only checks that the load completes and yields a
///   texture of the right shape/format with <see cref="Texture2D.isReadable"/> ==
///   false (reading pixels would throw). Unreadable is only covered on the
///   in-memory source since the file-region source path is independent of it.</item>
/// </list>
///
/// The input buffer is always RGBA32, a single mip, laid out layer-major where a
/// "layer" is one full <see cref="Dim"/>x<see cref="Dim"/> face/slice, rows
/// bottom-to-top (matching GetPixels indexing). The per-type meaning of a layer:
/// <list type="bullet">
///   <item>Texture2D: the single image (1 layer).</item>
///   <item>Texture3D: the depth slice <c>z</c>.</item>
///   <item>Texture2DArray: the array element.</item>
///   <item>Cubemap: the face 0..5.</item>
///   <item>CubemapArray: the flattened slice <c>cube * 6 + face</c>.</item>
/// </list>
/// </summary>
public class OwnedTextureTests : KSPTextureLoaderTestBase
{
    const int Dim = 4;
    const float FloatTol = 0.02f; // for float read-back paths (byte/255 quantization)
    const int Depth = 3; // Texture3D depth / Texture2DArray element count
    const int CubeCount = 2; // cubemaps in a CubemapArray
    #region Helpers

    // A distinctive color per (layer, x, y) so a mis-ordered slice/face/row is caught.
    static Color32 Texel(int layer, int x, int y) =>
        new Color32(
            (byte)(10 + x * 30),
            (byte)(10 + y * 30),
            (byte)(5 + layer * 18),
            (byte)(255 - layer * 8)
        );

    static Color TexelColor(int layer, int x, int y)
    {
        Color32 c = Texel(layer, x, y);
        return new Color(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
    }

    // Build a RGBA32, single-mip buffer of `layers` faces/slices, each Dim x Dim,
    // laid out layer-major then row-major (index = ((layer*h + y)*w + x)).
    static byte[] BuildBytes(int layers)
    {
        var bytes = new byte[layers * Dim * Dim * 4];
        for (int layer = 0; layer < layers; layer++)
        for (int y = 0; y < Dim; y++)
        for (int x = 0; x < Dim; x++)
        {
            var c = Texel(layer, x, y);
            int idx = ((layer * Dim + y) * Dim + x) * 4;
            bytes[idx + 0] = c.r;
            bytes[idx + 1] = c.g;
            bytes[idx + 2] = c.b;
            bytes[idx + 3] = c.a;
        }

        return bytes;
    }

    static NativeArray<byte> BuildData(int layers)
    {
        var bytes = BuildBytes(layers);
        var na = new NativeArray<byte>(bytes.Length, Allocator.Persistent);
        na.CopyFrom(bytes);
        return na;
    }

    // Write `payload` into a temp file behind FilePadLen sentinel bytes so a load
    // that ignores `offset` reads garbage. Returns the region to hand the loader.
    const int FilePadLen = 7;

    static string WriteTempRegionFile(byte[] payload, out long offset, out long length)
    {
        offset = FilePadLen;
        length = payload.Length;

        var full = new byte[FilePadLen + payload.Length];
        for (int i = 0; i < FilePadLen; i++)
            full[i] = 0xAB;
        Array.Copy(payload, 0, full, FilePadLen, payload.Length);

        var path = Path.Combine(Path.GetTempPath(), $"ksptl_owned_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, full);
        return path;
    }

    static T Load<T>(TextureLoadTask<T> task)
        where T : Texture
    {
        task.WaitUntilComplete();
        return task.GetTexture();
    }

    void AssertInt(string name, int actual, int expected)
    {
        if (actual != expected)
            throw new Exception($"TEST {name}: FAIL! {actual} != {expected}");
    }

    void AssertFormat(string name, TextureFormat actual, TextureFormat expected)
    {
        if (actual != expected)
            throw new Exception($"TEST {name}: FAIL! format {actual} != {expected}");
    }

    void AssertUnreadable(string name, bool isReadable)
    {
        if (isReadable)
            throw new Exception($"TEST {name}: FAIL! expected a non-readable texture");
    }

    #endregion

    #region Config factories

    static Texture2DConfig Config2D(bool readable) =>
        new()
        {
            Width = Dim,
            Height = Dim,
            MipCount = 1,
            Format = ExtendedTextureFormat.RGBA32,
            Readable = readable,
            Linear = true,
        };

    static Texture3DConfig Config3D(bool readable) =>
        new()
        {
            Width = Dim,
            Height = Dim,
            Depth = Depth,
            MipCount = 1,
            Format = ExtendedTextureFormat.RGBA32,
            Readable = readable,
            Linear = true,
        };

    static Texture2DArrayConfig Config2DArray(bool readable) =>
        new()
        {
            Width = Dim,
            Height = Dim,
            Count = Depth,
            MipCount = 1,
            Format = ExtendedTextureFormat.RGBA32,
            Readable = readable,
            Linear = true,
        };

    static CubemapConfig ConfigCubemap(bool readable) =>
        new()
        {
            Size = Dim,
            MipCount = 1,
            Format = ExtendedTextureFormat.RGBA32,
            Readable = readable,
            Linear = true,
        };

    static CubemapArrayConfig ConfigCubemapArray(bool readable) =>
        new()
        {
            Size = Dim,
            CubemapCount = CubeCount,
            MipCount = 1,
            Format = ExtendedTextureFormat.RGBA32,
            Readable = readable,
            Linear = true,
        };

    #endregion

    #region Verifiers

    void VerifyShape2D(string tag, Texture2D tex)
    {
        AssertInt($"{tag}.width", tex.width, Dim);
        AssertInt($"{tag}.height", tex.height, Dim);
        AssertFormat($"{tag}.format", tex.format, TextureFormat.RGBA32);
    }

    void VerifyReadable2D(string tag, Texture2D tex)
    {
        VerifyShape2D(tag, tex);
        var px = tex.GetPixels32();
        for (int y = 0; y < Dim; y++)
        for (int x = 0; x < Dim; x++)
            assertColor32Equals($"{tag}({x},{y})", px[y * Dim + x], Texel(0, x, y));
    }

    void VerifyShape3D(string tag, Texture3D tex)
    {
        AssertInt($"{tag}.width", tex.width, Dim);
        AssertInt($"{tag}.height", tex.height, Dim);
        AssertInt($"{tag}.depth", tex.depth, Depth);
        AssertFormat($"{tag}.format", tex.format, TextureFormat.RGBA32);
    }

    void VerifyReadable3D(string tag, Texture3D tex)
    {
        VerifyShape3D(tag, tex);
        var px = tex.GetPixels32(0);
        for (int z = 0; z < Depth; z++)
        for (int y = 0; y < Dim; y++)
        for (int x = 0; x < Dim; x++)
            assertColor32Equals(
                $"{tag}({x},{y},{z})",
                px[x + y * Dim + z * Dim * Dim],
                Texel(z, x, y)
            );
    }

    void VerifyShape2DArray(string tag, Texture2DArray tex)
    {
        AssertInt($"{tag}.width", tex.width, Dim);
        AssertInt($"{tag}.height", tex.height, Dim);
        AssertInt($"{tag}.depth", tex.depth, Depth);
        AssertFormat($"{tag}.format", tex.format, TextureFormat.RGBA32);
    }

    void VerifyReadable2DArray(string tag, Texture2DArray tex)
    {
        VerifyShape2DArray(tag, tex);
        for (int element = 0; element < Depth; element++)
        {
            var px = tex.GetPixels32(element, 0);
            for (int y = 0; y < Dim; y++)
            for (int x = 0; x < Dim; x++)
                assertColor32Equals(
                    $"{tag}[{element}]({x},{y})",
                    px[y * Dim + x],
                    Texel(element, x, y)
                );
        }
    }

    void VerifyShapeCubemap(string tag, Cubemap tex)
    {
        AssertInt($"{tag}.width", tex.width, Dim);
        AssertInt($"{tag}.height", tex.height, Dim);
        AssertFormat($"{tag}.format", tex.format, TextureFormat.RGBA32);
    }

    void VerifyReadableCubemap(string tag, Cubemap tex)
    {
        VerifyShapeCubemap(tag, tex);
        // Cubemap has no GetPixels32; use the float read-back with a tolerance.
        for (int face = 0; face < 6; face++)
        {
            var px = tex.GetPixels((CubemapFace)face, 0);
            for (int y = 0; y < Dim; y++)
            for (int x = 0; x < Dim; x++)
                assertColorEquals(
                    $"{tag}[{face}]({x},{y})",
                    px[y * Dim + x],
                    TexelColor(face, x, y),
                    FloatTol
                );
        }
    }

    void VerifyShapeCubemapArray(string tag, CubemapArray tex)
    {
        AssertInt($"{tag}.width", tex.width, Dim);
        AssertInt($"{tag}.height", tex.height, Dim);
        AssertInt($"{tag}.cubemapCount", tex.cubemapCount, CubeCount);
        AssertFormat($"{tag}.format", tex.format, TextureFormat.RGBA32);
    }

    void VerifyReadableCubemapArray(string tag, CubemapArray tex)
    {
        VerifyShapeCubemapArray(tag, tex);
        for (int cube = 0; cube < CubeCount; cube++)
        for (int face = 0; face < 6; face++)
        {
            int layer = cube * 6 + face;
            var px = tex.GetPixels32((CubemapFace)face, cube, 0);
            for (int y = 0; y < Dim; y++)
            for (int x = 0; x < Dim; x++)
                assertColor32Equals(
                    $"{tag}[{cube},{face}]({x},{y})",
                    px[y * Dim + x],
                    Texel(layer, x, y)
                );
        }
    }

    #endregion

    #region Texture2D

    [TestInfo("OwnedTexture_Texture2D_Memory")]
    public void TestTexture2DMemory()
    {
        var data = BuildData(1);
        try
        {
            var tex = Load(TextureLoader.LoadOwnedTexture2D(Config2D(true), data));
            try
            {
                VerifyReadable2D("tex2d", tex);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
        finally
        {
            data.Dispose();
        }
    }

    [TestInfo("OwnedTexture_Texture2D_Unreadable")]
    public void TestTexture2DUnreadable()
    {
        var data = BuildData(1);
        try
        {
            var tex = Load(TextureLoader.LoadOwnedTexture2D(Config2D(false), data));
            try
            {
                VerifyShape2D("tex2d_u", tex);
                AssertUnreadable("tex2d_u", tex.isReadable);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
        finally
        {
            data.Dispose();
        }
    }

    [TestInfo("OwnedTexture_Texture2D_File")]
    public void TestTexture2DFile()
    {
        var path = WriteTempRegionFile(BuildBytes(1), out long offset, out long length);
        try
        {
            var tex = Load(TextureLoader.LoadOwnedTexture2D(Config2D(true), path, offset, length));
            try
            {
                VerifyReadable2D("tex2d_file", tex);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion

    #region Texture3D

    [TestInfo("OwnedTexture_Texture3D_Memory")]
    public void TestTexture3DMemory()
    {
        var data = BuildData(Depth);
        try
        {
            var tex = Load(TextureLoader.LoadOwnedTexture3D(Config3D(true), data));
            try
            {
                VerifyReadable3D("tex3d", tex);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
        finally
        {
            data.Dispose();
        }
    }

    [TestInfo("OwnedTexture_Texture3D_Unreadable")]
    public void TestTexture3DUnreadable()
    {
        var data = BuildData(Depth);
        try
        {
            var tex = Load(TextureLoader.LoadOwnedTexture3D(Config3D(false), data));
            try
            {
                VerifyShape3D("tex3d_u", tex);
                AssertUnreadable("tex3d_u", tex.isReadable);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
        finally
        {
            data.Dispose();
        }
    }

    [TestInfo("OwnedTexture_Texture3D_File")]
    public void TestTexture3DFile()
    {
        var path = WriteTempRegionFile(BuildBytes(Depth), out long offset, out long length);
        try
        {
            var tex = Load(TextureLoader.LoadOwnedTexture3D(Config3D(true), path, offset, length));
            try
            {
                VerifyReadable3D("tex3d_file", tex);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion

    #region Texture2DArray

    [TestInfo("OwnedTexture_Texture2DArray_Memory")]
    public void TestTexture2DArrayMemory()
    {
        var data = BuildData(Depth);
        try
        {
            var tex = Load(TextureLoader.LoadOwnedTexture2DArray(Config2DArray(true), data));
            try
            {
                VerifyReadable2DArray("tex2darr", tex);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
        finally
        {
            data.Dispose();
        }
    }

    [TestInfo("OwnedTexture_Texture2DArray_Unreadable")]
    public void TestTexture2DArrayUnreadable()
    {
        var data = BuildData(Depth);
        try
        {
            var tex = Load(TextureLoader.LoadOwnedTexture2DArray(Config2DArray(false), data));
            try
            {
                VerifyShape2DArray("tex2darr_u", tex);
                AssertUnreadable("tex2darr_u", tex.isReadable);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
        finally
        {
            data.Dispose();
        }
    }

    [TestInfo("OwnedTexture_Texture2DArray_File")]
    public void TestTexture2DArrayFile()
    {
        var path = WriteTempRegionFile(BuildBytes(Depth), out long offset, out long length);
        try
        {
            var tex = Load(
                TextureLoader.LoadOwnedTexture2DArray(Config2DArray(true), path, offset, length)
            );
            try
            {
                VerifyReadable2DArray("tex2darr_file", tex);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion

    #region Cubemap

    [TestInfo("OwnedTexture_Cubemap_Memory")]
    public void TestCubemapMemory()
    {
        var data = BuildData(6);
        try
        {
            var tex = Load(TextureLoader.LoadOwnedCubemap(ConfigCubemap(true), data));
            try
            {
                VerifyReadableCubemap("cube", tex);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
        finally
        {
            data.Dispose();
        }
    }

    [TestInfo("OwnedTexture_Cubemap_Unreadable")]
    public void TestCubemapUnreadable()
    {
        var data = BuildData(6);
        try
        {
            var tex = Load(TextureLoader.LoadOwnedCubemap(ConfigCubemap(false), data));
            try
            {
                VerifyShapeCubemap("cube_u", tex);
                AssertUnreadable("cube_u", tex.isReadable);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
        finally
        {
            data.Dispose();
        }
    }

    [TestInfo("OwnedTexture_Cubemap_File")]
    public void TestCubemapFile()
    {
        var path = WriteTempRegionFile(BuildBytes(6), out long offset, out long length);
        try
        {
            var tex = Load(
                TextureLoader.LoadOwnedCubemap(ConfigCubemap(true), path, offset, length)
            );
            try
            {
                VerifyReadableCubemap("cube_file", tex);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion

    #region CubemapArray

    [TestInfo("OwnedTexture_CubemapArray_Memory")]
    public void TestCubemapArrayMemory()
    {
        var data = BuildData(CubeCount * 6); // layer = cube * 6 + face
        try
        {
            var tex = Load(TextureLoader.LoadOwnedCubemapArray(ConfigCubemapArray(true), data));
            try
            {
                VerifyReadableCubemapArray("cubearr", tex);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
        finally
        {
            data.Dispose();
        }
    }

    [TestInfo("OwnedTexture_CubemapArray_Unreadable")]
    public void TestCubemapArrayUnreadable()
    {
        var data = BuildData(CubeCount * 6);
        try
        {
            var tex = Load(TextureLoader.LoadOwnedCubemapArray(ConfigCubemapArray(false), data));
            try
            {
                VerifyShapeCubemapArray("cubearr_u", tex);
                AssertUnreadable("cubearr_u", tex.isReadable);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
        finally
        {
            data.Dispose();
        }
    }

    [TestInfo("OwnedTexture_CubemapArray_File")]
    public void TestCubemapArrayFile()
    {
        var path = WriteTempRegionFile(BuildBytes(CubeCount * 6), out long offset, out long length);
        try
        {
            var tex = Load(
                TextureLoader.LoadOwnedCubemapArray(ConfigCubemapArray(true), path, offset, length)
            );
            try
            {
                VerifyReadableCubemapArray("cubearr_file", tex);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion
}
