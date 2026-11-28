using System;
using System.IO;
using KSP.Testing;
using KSPTextureLoader;
using KSPTextureLoader.Format;
using KSPTextureLoader.Utils;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace KSPTextureLoaderTests;

/// <summary>
/// Tests for <see cref="TextureLoader.LoadDisjointCubemap(CubemapFaces, TextureLoadOptions)"/>,
/// which builds a cubemap out of six separate face textures.
///
/// Each test writes its own set of DX10 DDS faces into a unique directory
/// under GameData. Every (face, mip) is filled with a distinct solid color so
/// that a face written to the wrong slot, or a mip that was never written, is
/// caught.
/// </summary>
public class DisjointCubemapTests : KSPTextureLoaderTestBase
{
    const int Dim = 8;
    const int Mips = 4;
    const float Tol = 0.004f;

    const uint DXGI_FORMAT_R8G8B8A8_UNORM = 28;
    const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;

    static Color32 FaceColor(int face, int mip) =>
        new((byte)(20 + face * 40), (byte)(32 + mip * 64), (byte)(224 - mip * 48), 255);

    #region Helpers
    sealed class FaceSet : IDisposable
    {
        public readonly string Directory;
        public readonly CubemapFaces Faces;

        public FaceSet(Func<int, uint> formatForFace, Func<int, int> mipsForFace)
        {
            var relDir = $"KSPTextureLoaderTests/PluginData/disjoint_{Guid.NewGuid():N}";
            Directory = Path.Combine(PathUtil.GameDataDir, relDir);
            System.IO.Directory.CreateDirectory(Directory);

            var faces = new CubemapFaces();
            for (int face = 0; face < 6; ++face)
            {
                var name = $"face{face}.dds";
                WriteFace(
                    Path.Combine(Directory, name),
                    face,
                    formatForFace(face),
                    mipsForFace(face)
                );
                faces[(CubemapFace)face] = $"{relDir}/{name}";
            }

            Faces = faces;
        }

        public void Dispose()
        {
            TextureLoader.ImmediatelyDestroyUnusedTextures();

            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (Exception e)
            {
                // A streamed bundle may still hold the files open until it
                // gets unloaded.
                Debug.LogWarning(
                    $"[KSPTextureLoaderTests] Could not clean up {Directory}: {e.Message}"
                );
            }
        }
    }

    static FaceSet CreateFaces(uint format = DXGI_FORMAT_R8G8B8A8_UNORM) =>
        new(_ => format, _ => Mips);

    static void WriteFace(string path, int face, uint dxgiFormat, int mips)
    {
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);

        // DDS_HEADER
        w.Write(0x20534444u); // "DDS "
        w.Write(124u); // dwSize
        w.Write(0x1u | 0x2u | 0x4u | 0x1000u | 0x20000u | 0x80000u); // dwFlags
        w.Write((uint)Dim); // dwHeight
        w.Write((uint)Dim); // dwWidth
        w.Write((uint)(Dim * Dim * 4)); // dwPitchOrLinearSize
        w.Write(0u); // dwDepth
        w.Write((uint)mips); // dwMipMapCount
        for (int i = 0; i < 11; ++i)
            w.Write(0u); // dwReserved1

        // DDS_PIXELFORMAT
        w.Write(32u); // dwSize
        w.Write(0x4u); // DDPF_FOURCC
        w.Write(0x30315844u); // "DX10"
        for (int i = 0; i < 5; ++i)
            w.Write(0u); // bit count + masks

        w.Write(0x1000u | 0x400000u | 0x8u); // dwCaps: TEXTURE | MIPMAP | COMPLEX
        for (int i = 0; i < 4; ++i)
            w.Write(0u); // dwCaps2..4, dwReserved2

        // DDS_HEADER_DXT10
        w.Write(dxgiFormat);
        w.Write(3u); // D3D10_RESOURCE_DIMENSION_TEXTURE2D
        w.Write(0u); // miscFlag
        w.Write(1u); // arraySize
        w.Write(0u); // miscFlags2

        bool bgra = dxgiFormat == DXGI_FORMAT_B8G8R8A8_UNORM;
        for (int mip = 0; mip < mips; ++mip)
        {
            var c = FaceColor(face, mip);
            int dim = Math.Max(Dim >> mip, 1);
            for (int i = 0; i < dim * dim; ++i)
            {
                if (bgra)
                    w.Write(new[] { c.b, c.g, c.r, c.a });
                else
                    w.Write(new[] { c.r, c.g, c.b, c.a });
            }
        }
    }

    static void AssertShape(Cubemap cube, int mips, GraphicsFormat format)
    {
        if (cube.width != Dim || cube.height != Dim)
            throw new Exception($"expected {Dim}x{Dim} cubemap but got {cube.width}x{cube.height}");
        if (cube.mipmapCount != mips)
            throw new Exception($"expected {mips} mip levels but got {cube.mipmapCount}");
        if (cube.graphicsFormat != format)
            throw new Exception($"expected format {format} but got {cube.graphicsFormat}");
    }

    void AssertFaces(Cubemap cube, bool gpuReadback)
    {
        for (int mip = 0; mip < cube.mipmapCount; ++mip)
        {
            int dim = Math.Max(Dim >> mip, 1);
            for (int face = 0; face < 6; ++face)
            {
                var pixels = gpuReadback
                    ? CubemapConversionTests.ReadBackFace(cube, (CubemapFace)face, mip)
                    : cube.GetPixels((CubemapFace)face, mip);
                if (pixels.Length != dim * dim)
                    throw new Exception(
                        $"face {face} mip {mip}: expected {dim * dim} pixels but got {pixels.Length}"
                    );

                Color expected = FaceColor(face, mip);
                for (int i = 0; i < pixels.Length; ++i)
                    assertColorEquals($"face{face}.mip{mip}[{i}]", pixels[i], expected, Tol);
            }
        }
    }

    static Cubemap CombineOnCpu(FaceSet set, TextureLoadOptions options)
    {
        var handles = new CPUTextureHandle[6];
        using var guard = new ArrayDisposeGuard<CPUTextureHandle>(handles);

        var cpuOptions = new TextureLoadOptions { Hint = TextureLoadHint.Synchronous };
        var faces = new CPUTexture2D[6];
        for (int i = 0; i < 6; ++i)
        {
            handles[i] = TextureLoader.LoadCPUTexture(set.Faces[(CubemapFace)i], cpuOptions);
            faces[i] = handles[i].GetTexture();
        }

        var task = AsyncUtil.LaunchMainThreadTask(() =>
            DisjointCubemapLoader.CombineOnCpu(faces, options)
        );
        return TextureLoader.Context.RunUntilComplete(task, null);
    }
    #endregion

    [TestInfo("DisjointCubemap_Bundle")]
    public void TestBundle()
    {
        using var set = CreateFaces();
        using var handle = TextureLoader.LoadDisjointCubemap(
            set.Faces,
            new TextureLoadOptions { Unreadable = true, Hint = TextureLoadHint.Asynchronous }
        );
        var cube = handle.GetTexture();

        if (cube.isReadable)
            throw new Exception("expected an unreadable cubemap");
        AssertShape(cube, Mips, GraphicsFormat.R8G8B8A8_UNorm);
        AssertFaces(cube, gpuReadback: true);
    }

    [TestInfo("DisjointCubemap_CopyTexture_Readable")]
    public void TestCopyTextureReadable()
    {
        if (!TextureUtils.CanGpuCopyTexture2dToCubemap)
            return;

        using var set = CreateFaces();
        using var handle = TextureLoader.LoadDisjointCubemap(
            set.Faces,
            new TextureLoadOptions { Unreadable = false, Hint = TextureLoadHint.Synchronous }
        );
        var cube = handle.GetTexture();

        if (!cube.isReadable)
            throw new Exception("expected a readable cubemap");
        AssertShape(cube, Mips, GraphicsFormat.R8G8B8A8_UNorm);

        // Both the CPU and the GPU copies need to have been written.
        AssertFaces(cube, gpuReadback: false);
        AssertFaces(cube, gpuReadback: true);
    }

    [TestInfo("DisjointCubemap_MismatchedMips")]
    public void TestMismatchedMips()
    {
        // Not eligible for the bundle path, so this falls back to loading each
        // face and truncates to the smallest mip count.
        using var set = new FaceSet(_ => DXGI_FORMAT_R8G8B8A8_UNORM, f => f == 3 ? 2 : Mips);
        using var handle = TextureLoader.LoadDisjointCubemap(
            set.Faces,
            new TextureLoadOptions { Unreadable = true, Hint = TextureLoadHint.Asynchronous }
        );
        var cube = handle.GetTexture();

        AssertShape(cube, 2, GraphicsFormat.R8G8B8A8_UNorm);
        AssertFaces(cube, gpuReadback: true);
    }

    [TestInfo("DisjointCubemap_Cached")]
    public void TestCached()
    {
        using var set = CreateFaces();
        var options = new TextureLoadOptions { Hint = TextureLoadHint.Synchronous };

        using var a = TextureLoader.LoadDisjointCubemap(set.Faces, options);
        using var b = TextureLoader.LoadDisjointCubemap(set.Faces, options);

        if (!ReferenceEquals(a.GetTexture(), b.GetTexture()))
            throw new Exception("expected both loads to share the same cubemap");
        if (!a.Path.StartsWith("disjoint-cubemap:KSPTextureLoaderTests/PluginData/disjoint_"))
            throw new Exception($"unexpected cache key {a.Path}");
    }

    [TestInfo("DisjointCubemap_Cpu_SameFormat")]
    public void TestCpuSameFormat()
    {
        using var set = CreateFaces();
        var cube = CombineOnCpu(
            set,
            new TextureLoadOptions
            {
                Unreadable = false,
                Linear = true,
                Hint = TextureLoadHint.Synchronous,
            }
        );

        try
        {
            AssertShape(cube, Mips, GraphicsFormat.R8G8B8A8_UNorm);
            AssertFaces(cube, gpuReadback: false);
            AssertFaces(cube, gpuReadback: true);
        }
        finally
        {
            UnityEngine.Object.Destroy(cube);
        }
    }

    [TestInfo("DisjointCubemap_MultiFileRegionStream")]
    public void TestMultiFileRegionStream()
    {
        // Three files whose regions sit behind some padding, plus an empty
        // region in the middle, presented as one stream of bytes 0..N.
        int[] lengths = [5, 0, 13, 7];
        var regions = new KSPTextureLoader.Format.Bundle.MultiFileRegionStream.Region[
            lengths.Length
        ];
        var paths = new string[lengths.Length];
        int total = 0;
        try
        {
            for (int i = 0; i < lengths.Length; ++i)
            {
                int pad = 3 + i;
                var bytes = new byte[pad + lengths[i] + 2];
                for (int j = 0; j < bytes.Length; ++j)
                    bytes[j] = 0xEE;
                for (int j = 0; j < lengths[i]; ++j)
                    bytes[pad + j] = (byte)(total + j);

                paths[i] = Path.Combine(Path.GetTempPath(), $"ksptl_mfrs_{Guid.NewGuid():N}.bin");
                File.WriteAllBytes(paths[i], bytes);
                regions[i] = new(paths[i], pad, lengths[i]);
                total += lengths[i];
            }

            using var stream = new KSPTextureLoader.Format.Bundle.MultiFileRegionStream(regions);
            if (stream.Length != total)
                throw new Exception($"expected length {total} but got {stream.Length}");

            // Read in odd-sized chunks so reads straddle region boundaries.
            var result = new byte[total];
            int read = 0;
            while (read < total)
            {
                int n = stream.Read(result, read, Math.Min(4, total - read));
                if (n <= 0)
                    throw new Exception($"stream ended early after {read} bytes");
                read += n;
            }

            for (int i = 0; i < total; ++i)
            {
                if (result[i] != i)
                    throw new Exception($"byte {i}: expected {i} but got {result[i]}");
            }

            if (stream.Read(result, 0, 1) != 0)
                throw new Exception("expected EOF at the end of the stream");

            // Seek backwards into the middle of the third region.
            stream.Seek(8, SeekOrigin.Begin);
            var one = new byte[1];
            if (stream.Read(one, 0, 1) != 1 || one[0] != 8)
                throw new Exception($"after seek: expected 8 but got {one[0]}");
        }
        finally
        {
            foreach (var path in paths)
            {
                if (path is not null)
                    File.Delete(path);
            }
        }
    }

    [TestInfo("DisjointCubemap_Cpu_MixedFormats")]
    public void TestCpuMixedFormats()
    {
        // Alternating RGBA32 and BGRA32 faces forces the RGBA32 decode path.
        using var set = new FaceSet(
            f => f % 2 == 0 ? DXGI_FORMAT_R8G8B8A8_UNORM : DXGI_FORMAT_B8G8R8A8_UNORM,
            _ => Mips
        );
        var cube = CombineOnCpu(
            set,
            new TextureLoadOptions
            {
                Unreadable = false,
                Linear = true,
                Hint = TextureLoadHint.Synchronous,
            }
        );

        try
        {
            AssertShape(cube, Mips, GraphicsFormat.R8G8B8A8_UNorm);
            AssertFaces(cube, gpuReadback: false);
            AssertFaces(cube, gpuReadback: true);
        }
        finally
        {
            UnityEngine.Object.Destroy(cube);
        }
    }
}
