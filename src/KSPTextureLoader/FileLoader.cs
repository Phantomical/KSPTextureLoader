using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KSPTextureLoader.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;

namespace KSPTextureLoader;

internal static class FileLoader
{
    static readonly ProfilerMarker ReadFileContentsMarker = new("ReadFileContents");

    public struct FileReadInfo
    {
        public string path;
        public long offset;
        public long length;
    }

    internal static Task<LargeNativeArray<byte>> ReadFileContentsAsync(FileReadInfo info) =>
        ReadFileContentsAsync(Task.FromResult(info));

    internal static Task<LargeNativeArray<byte>> ReadFileContentsAsync(Task<FileReadInfo> info)
    {
        if (Config.Instance.UseAsyncReadManager)
            return ReadFileContentsUnity(info);

        return Task.Run(async () =>
        {
            var finfo = await info;
            var data = await AllocatorUtil.CreateNativeArrayHGlobalAsync<byte>(
                finfo.length,
                NativeArrayOptions.UninitializedMemory
            );

            try
            {
                if (ReadFileContentsManaged(finfo.path, finfo.offset, data))
                    return data;
            }
            catch
            {
                data.DisposeExt();
                throw;
            }

            data.DisposeExt();

            // If managed seeking fails then fall back to AsyncReadManager.
            return await ReadFileContentsUnity(finfo);
        });
    }

    /// <summary>
    /// Read <c>data.Length</c> bytes starting at <paramref name="fileOffset"/> within
    /// <paramref name="path"/> into <paramref name="data"/> using managed file IO.
    /// </summary>
    /// <returns>Whether the read worked properly, false otherwise.</returns>
    static bool ReadFileContentsManaged(string path, long fileOffset, LargeNativeArray<byte> data)
    {
        using var scope = ReadFileContentsMarker.Auto();

        using var reader = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            // disable the internal buffer
            bufferSize: 1
        );

        // Seek has historically not been reliable on all systems (notably Win10),
        // so we check where we actually ended up instead of trusting it.
        long position = reader.Seek(fileOffset, SeekOrigin.Begin);

        // We have encountered problems with seek in the past. Double check
        // that we are at the right offset and bail out if not.
        if (position != fileOffset)
        {
            WarnSeekFailed(
                path,
                $"seeking to offset {fileOffset} ended up at offset {position} instead"
            );
            return false;
        }

        long offset = 0;
        long length = data.Length;
        var buffer = new byte[64 * 1024];

        while (offset < length)
        {
            int count = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, length - offset));
            if (count > length - offset || count <= 0)
                throw new Exception(
                    $"the length of the file changed while it was being read (read {offset + count} bytes but expected {length} bytes)"
                );

            data.CopyRangeFrom(offset, buffer, count);
            offset += count;
        }

        return true;
    }

    static void WarnSeekFailed(string path, string message)
    {
        AsyncUtil.LaunchMainThreadTask(() =>
        {
            Debug.LogWarning(
                $"[KSPTextureLoader] {message} when reading {path}. Falling back to AsyncReadManager."
            );
        });
    }

    static async Task<LargeNativeArray<byte>> ReadFileContentsUnity(Task<FileReadInfo> infoTask) =>
        await ReadFileContentsUnity(await infoTask);

    static async Task<LargeNativeArray<byte>> ReadFileContentsUnity(FileReadInfo info)
    {
        var data = await AllocatorUtil.CreateNativeArrayHGlobalAsync<byte>(
            info.length,
            NativeArrayOptions.UninitializedMemory
        );

        try
        {
            // Unity 6 reads the command from a job after Read returns (2019.4
            // copied it during the call), so it can't live on this stack frame:
            // it has to stay allocated until the read completes.
            var command = AllocatorUtil.CreateNativeArrayHGlobal<ReadCommand>(1);

            ReadHandle handle;
            unsafe
            {
                var cmd = (ReadCommand*)NativeArrayUnsafeUtility.GetUnsafePtr(command);
                *cmd = new ReadCommand
                {
                    Buffer = data.GetUnsafePtr(),
                    Offset = info.offset,
                    Size = data.Length,
                };
                handle = AsyncReadManager.Read(info.path, cmd, 1);
                command.DisposeExt(handle.JobHandle);
            }

            using var hguard = handle;
            await AsyncUtil.WaitFor(handle.JobHandle);

            if (handle.Status != ReadStatus.Complete)
                throw new Exception("Failed to read texture data from file");

            return data;
        }
        catch
        {
            data.DisposeExt();
            throw;
        }
    }
}
