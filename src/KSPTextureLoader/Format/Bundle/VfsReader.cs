using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using KSPTextureLoader.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using Unity.Profiling;

namespace KSPTextureLoader.Format.Bundle;

/// <summary>A single byte range to read from a path.</summary>
internal readonly struct ReadRange(long offset, long size)
{
    public readonly long Offset = offset;
    public readonly long Size = size;
}

/// <summary>
/// The result of a batched <see cref="VfsReader.ReadRangesAsync"/>: one
/// contiguous buffer holding every requested range back-to-back. Each range can
/// be read through its own <see cref="EndianBinaryReader"/> via
/// <see cref="ReaderFor"/>. Owns the backing allocation; dispose once done.
/// </summary>
internal sealed class RangeReadResult : IDisposable
{
    LargeNativeArray<byte> data;
    readonly long[] offsets;
    readonly long[] sizes;

    public RangeReadResult(LargeNativeArray<byte> data, long[] offsets, long[] sizes)
    {
        this.data = data;
        this.offsets = offsets;
        this.sizes = sizes;
    }

    public int Count => offsets.Length;

    /// <summary>A reader over the i-th requested range. Valid until disposal.</summary>
    public unsafe EndianBinaryReader ReaderFor(int index) =>
        new(data.GetUnsafePtr() + offsets[index], sizes[index]);

    public void Dispose() => data.DisposeExt();
}

/// <summary>
/// Reads a byte range from a path through Unity's <see cref="AsyncReadManager"/>.
/// </summary>
internal static class VfsReader
{
    static readonly ProfilerMarker ReadMarker = new("VfsRead");
    static readonly ProfilerMarker ReadRangesMarker = new("VfsReadRanges");

    /// <summary>
    /// Read <paramref name="size"/> bytes starting at <paramref name="offset"/>
    /// from <paramref name="path"/> into a freshly allocated buffer.
    /// </summary>
    public static async Task<LargeNativeArray<byte>> ReadAsync(string path, long offset, long size)
    {
        if (size < 0)
            throw new ArgumentOutOfRangeException(nameof(size));

        var data = await AllocatorUtil.CreateNativeArrayHGlobalAsync<byte>(
            size,
            NativeArrayOptions.UninitializedMemory
        );

        if (size == 0)
            return data;

        try
        {
            using var scope = ReadMarker.Auto();

            ReadHandle handle;
            unsafe
            {
                var command = new ReadCommand
                {
                    Buffer = data.GetUnsafePtr(),
                    Offset = offset,
                    Size = size,
                };
                handle = AsyncReadManager.Read(path, &command, 1);
            }

            using var hguard = handle;
            using (scope.Suspend())
                await AsyncUtil.WaitFor(handle.JobHandle);

            if (handle.Status != ReadStatus.Complete)
                throw new IOException(
                    $"AsyncReadManager failed to read {size} bytes at {offset} from \"{path}\" "
                        + $"(status {handle.Status})"
                );

            return data;
        }
        catch
        {
            data.DisposeExt();
            throw;
        }
    }

    /// <summary>
    /// Read several byte ranges from <paramref name="path"/> in a single
    /// <see cref="AsyncReadManager"/> dispatch (one <c>ReadCommand</c> per range).
    /// The ranges are packed back-to-back into one allocation; use
    /// <see cref="RangeReadResult.ReaderFor"/> to read each one.
    /// </summary>
    public static async Task<RangeReadResult> ReadRangesAsync(
        string path,
        IReadOnlyList<ReadRange> ranges
    )
    {
        if (ranges is null)
            throw new ArgumentNullException(nameof(ranges));

        int n = ranges.Count;
        var offsets = new long[n];
        var sizes = new long[n];
        long total = 0;
        for (int i = 0; i < n; ++i)
        {
            var range = ranges[i];
            if (range.Size < 0)
                throw new ArgumentOutOfRangeException(nameof(ranges));

            offsets[i] = total;
            sizes[i] = range.Size;
            total += range.Size;
        }

        var data = await AllocatorUtil.CreateNativeArrayHGlobalAsync<byte>(
            total,
            NativeArrayOptions.UninitializedMemory
        );

        if (total == 0)
            return new RangeReadResult(data, offsets, sizes);

        try
        {
            using var scope = ReadRangesMarker.Auto();

            ReadHandle handle;

            // The command array can be large (one entry per object), so keep it
            // off the stack. AsyncReadManager copies the commands internally at
            // dispatch, so it is safe to free this right after the Read call.
            var commands = new NativeArray<ReadCommand>(
                n,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory
            );
            try
            {
                unsafe
                {
                    byte* basePtr = data.GetUnsafePtr();
                    var cmds = (ReadCommand*)NativeArrayUnsafeUtility.GetUnsafePtr(commands);
                    for (int i = 0; i < n; ++i)
                    {
                        cmds[i] = new ReadCommand
                        {
                            Buffer = basePtr + offsets[i],
                            Offset = ranges[i].Offset,
                            Size = sizes[i],
                        };
                    }

                    handle = AsyncReadManager.Read(path, cmds, (uint)n);
                }
            }
            finally
            {
                commands.Dispose();
            }

            using var hguard = handle;
            using (scope.Suspend())
                await AsyncUtil.WaitFor(handle.JobHandle);

            if (handle.Status != ReadStatus.Complete)
                throw new IOException(
                    $"AsyncReadManager failed to read {n} ranges ({total} bytes) from \"{path}\" "
                        + $"(status {handle.Status})"
                );

            return new RangeReadResult(data, offsets, sizes);
        }
        catch
        {
            data.DisposeExt();
            throw;
        }
    }
}
