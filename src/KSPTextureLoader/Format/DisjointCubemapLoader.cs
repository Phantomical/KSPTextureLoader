using System;
using System.Threading.Tasks;
using KSPTextureLoader.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using static KSPTextureLoader.Format.DDS.DDSUtil;

namespace KSPTextureLoader.Format;

/// <summary>
/// Slow paths for assembling a <see cref="Cubemap"/> out of six separately
/// loaded face textures. The fast path (a streamed asset bundle spanning all
/// six DDS files) lives in <see cref="DDSLoader.TryLoadDisjointCubemapViaBundle"/>.
/// </summary>
internal static class DisjointCubemapLoader
{
    #region GPU
    /// <summary>
    /// Combine six face textures into a cubemap using
    /// <see cref="Graphics.CopyTexture(Texture, int, int, Texture, int, int)"/>.
    /// Requires <see cref="TextureUtils.CanGpuCopyTexture2dToCubemap"/>.
    /// </summary>
    ///
    /// <remarks>
    /// CopyTexture copies the CPU-side data as well when both textures are
    /// readable, so the faces only need to be readable if the cubemap should be.
    /// </remarks>
    internal static Cubemap CombineWithCopyTexture(Texture2D[] faces, bool readable)
    {
        var first = faces[0];
        int size = first.width;
        var format = first.graphicsFormat;
        int mips = first.mipmapCount;

        for (int i = 0; i < faces.Length; ++i)
        {
            var face = faces[i];
            ValidateSize((CubemapFace)i, face.width, face.height, size);

            if (face.graphicsFormat != format)
                throw new Exception(
                    $"Cubemap face {(CubemapFace)i} has format {face.graphicsFormat} but face {CubemapFace.PositiveX} has format {format}. All faces must have the same format."
                );

            if (readable && !face.isReadable)
                throw new Exception(
                    $"Cubemap face {(CubemapFace)i} is not readable but a readable cubemap was requested"
                );

            mips = Math.Min(mips, face.mipmapCount);
        }

        var cube = TextureUtils.CreateUninitializedCubemap(size, mips, format);
        using var guard = new TextureDisposeGuard(cube);

        // Make sure the GPU texture exists and drop the CPU copy so that
        // nothing uploads the uninitialized CPU data over what we copy.
        if (!readable)
            cube.Apply(false, true);

        for (int face = 0; face < 6; ++face)
        {
            for (int mip = 0; mip < mips; ++mip)
                Graphics.CopyTexture(faces[face], 0, mip, cube, face, mip);
        }

        guard.Clear();
        return cube;
    }
    #endregion

    #region CPU
    /// <summary>
    /// Combine six CPU face textures into a cubemap without using the GPU.
    /// </summary>
    ///
    /// <remarks>
    /// If every face has the same format (and that format can be sampled on
    /// this platform) then the raw face data is copied straight into the
    /// cubemap. Otherwise the faces are decoded into an RGBA32 cubemap.
    ///
    /// Must be called on the main thread.
    /// </remarks>
    internal static async Task<Cubemap> CombineOnCpu(
        CPUTexture2D[] faces,
        TextureLoadOptions options
    )
    {
        var first = faces[0];
        int size = first.Width;
        int mips = first.MipCount;
        bool sameFormat = true;

        for (int i = 0; i < faces.Length; ++i)
        {
            var face = faces[i];
            ValidateSize((CubemapFace)i, face.Width, face.Height, size);

            sameFormat &= face.Format == first.Format;
            mips = Math.Min(mips, face.MipCount);
        }

        // CPU textures don't carry a color space, so follow the options and
        // default to linear, same as CPUTexture2D.CompileToTexture.
        bool srgb = options.Linear == false;

        GraphicsFormat format = GraphicsFormat.None;
        if (sameFormat)
        {
            format = GraphicsFormatUtility.GetGraphicsFormat(first.Format, srgb);
            if (format == GraphicsFormat.None || !IsSampleable(format))
                sameFormat = false;
        }

        if (!sameFormat)
            format = GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.RGBA32, srgb);

        var cube = TextureUtils.CreateUninitializedCubemap(size, mips, format);
        using var guard = new TextureDisposeGuard(cube);
        cube.name = first.Name;

        // SetPixelData does its bounds checks using ints, and so throws
        // spurious errors once the face data sits past int.MaxValue within
        // the cubemap (e.g. 16k RGBA32 cubemaps). In that case we fill a
        // staging Texture2D one face at a time and use Graphics.CopyTexture
        // to copy it over, which copies the CPU-side data regardless of GPU
        // copy support.
        long faceChainSize = GetFaceMipChainSize(size, size, mips, format);
        bool useStaging = faceChainSize * 6 > int.MaxValue;

        Texture2D staging = null;
        using var stagingGuard = new TextureDisposeGuard(null);
        if (useStaging)
        {
            staging = TextureUtils.CreateUninitializedTexture2D(size, size, mips, format);
            stagingGuard.Set(staging);
        }

#if !KSP_RETOOLED
        if (options.Hint != TextureLoadHint.Synchronous)
        {
            // This will wait until the textures have been uploaded by the graphics
            // thread so that it won't make a copy when we write to their CPU data.
            await AsyncUtil.RunOnGraphicsThread(static () => { });
        }
#endif

        if (useStaging)
        {
            var stagingData = staging.GetRawTextureData<byte>();
            for (int face = 0; face < 6; ++face)
            {
                JobHandle handle = default;
                long offset = 0;
                for (int mip = 0; mip < mips; ++mip)
                {
                    int mipSize = Get2DMipMapSize(size, size, mip, format);
                    var dst = stagingData.GetSubArray((int)offset, mipSize);
                    handle = JobHandle.CombineDependencies(
                        handle,
                        sameFormat
                            ? ScheduleCopy(GetRawMip(faces[face], mip, format), dst)
                            : faces[face].GetPixels32(dst.Reinterpret<Color32>(1), mip)
                    );
                    offset += mipSize;
                }

                await WaitForJobs(handle);
                Graphics.CopyTexture(staging, 0, cube, face);
            }
        }
        else
        {
#if KSP_RETOOLED
            // We can write directly into the cubemap's CPU data, so all faces
            // can be filled in parallel.
            JobHandle handle = default;
            for (int face = 0; face < 6; ++face)
            {
                for (int mip = 0; mip < mips; ++mip)
                {
                    handle = JobHandle.CombineDependencies(
                        handle,
                        sameFormat
                            ? ScheduleCopy(
                                GetRawMip(faces[face], mip, format),
                                cube.GetPixelData<byte>(mip, (CubemapFace)face)
                            )
                            : faces[face]
                                .GetPixels32(
                                    cube.GetPixelData<Color32>(mip, (CubemapFace)face),
                                    mip
                                )
                    );
                }
            }

            await WaitForJobs(handle);
#else
            // Unity 2019.4 has no way to write directly into a cubemap's CPU
            // data, so we go through SetPixelData one face/mip at a time.
            if (sameFormat)
            {
                for (int face = 0; face < 6; ++face)
                {
                    for (int mip = 0; mip < mips; ++mip)
                    {
                        var src = GetRawMip(faces[face], mip, format);
                        cube.SetPixelData(src, mip, (CubemapFace)face);
                    }
                }
            }
            else
            {
                using var pixels = new NativeArray<Color32>(
                    size * size,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );

                for (int face = 0; face < 6; ++face)
                {
                    for (int mip = 0; mip < mips; ++mip)
                    {
                        int dim = Math.Max(size >> mip, 1);
                        var mipPixels = pixels.GetSubArray(0, dim * dim);
                        await WaitForJobs(faces[face].GetPixels32(mipPixels, mip));
                        cube.SetPixelData(mipPixels, mip, (CubemapFace)face);
                    }
                }
            }
#endif
        }

        cube.Apply(false, options.Unreadable);
        guard.Clear();
        return cube;
    }

    static NativeArray<byte> GetRawMip(CPUTexture2D face, int mip, GraphicsFormat format)
    {
        var data = face.GetRawTextureData();

        int offset = 0;
        for (int i = 0; i < mip; ++i)
            offset += Get2DMipMapSize(face.Width, face.Height, i, format);

        int mipSize = Get2DMipMapSize(face.Width, face.Height, mip, format);
        if ((long)offset + mipSize > data.Length)
            throw new Exception(
                $"CPU texture {face.Name} does not contain enough data for mip {mip}"
            );

        return data.GetSubArray(offset, mipSize);
    }

    static bool IsSampleable(GraphicsFormat format) =>
#if UNITY_6_7
        SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Sample);
#else
        SystemInfo.IsFormatSupported(format, FormatUsage.Sample);
#endif

    static void ValidateSize(CubemapFace face, int width, int height, int size)
    {
        if (width != height)
            throw new Exception(
                $"Cubemap face {face} is {width}x{height}, but cubemap faces must be square"
            );

        if (width != size)
            throw new Exception(
                $"Cubemap face {face} is {width}x{height} but face {CubemapFace.PositiveX} is {size}x{size}. All faces must be the same size."
            );
    }

    static Task WaitForJobs(JobHandle handle)
    {
        JobHandle.ScheduleBatchedJobs();
        return AsyncUtil.WaitFor(handle);
    }

    // Large enough that per-job overhead doesn't matter, small enough that a
    // 1 GB face gets split across all worker threads.
    const int CopyChunkSize = 1 << 20;

    static unsafe JobHandle ScheduleCopy(NativeArray<byte> src, NativeArray<byte> dst)
    {
        if (src.Length != dst.Length)
            throw new InvalidOperationException(
                $"source and destination lengths do not match (source {src.Length}, destination {dst.Length})"
            );

        var job = new ParallelCopyJob
        {
            src = (byte*)src.GetUnsafeReadOnlyPtr(),
            dst = (byte*)dst.GetUnsafePtr(),
            length = src.Length,
        };

        int chunks = (src.Length + CopyChunkSize - 1) / CopyChunkSize;
        if (!BurstUtil.JobsAllowed)
        {
            for (int i = 0; i < chunks; ++i)
                job.Execute(i);
            return default;
        }

        return job.Schedule(chunks, 1);
    }

    unsafe struct ParallelCopyJob : IJobParallelFor
    {
        [NativeDisableUnsafePtrRestriction]
        public byte* src;

        [NativeDisableUnsafePtrRestriction]
        public byte* dst;

        public int length;

        public void Execute(int index)
        {
            long start = (long)index * CopyChunkSize;
            long count = Math.Min(CopyChunkSize, length - start);
            UnsafeUtility.MemCpy(dst + start, src + start, count);
        }
    }
    #endregion
}
