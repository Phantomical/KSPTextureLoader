using System;
using System.IO;
using System.Threading.Tasks;
using KSPTextureLoader.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using Unity.Profiling;

namespace KSPTextureLoader;

internal static class FileLoader
{
    static readonly ProfilerMarker ReadFileContentsMarker = new("ReadFileContents");

    public struct FileReadInfo
    {
        public string path;
        public long offset;
        public int length;
    }

    internal static Task<NativeArray<byte>> ReadFileContentsAsync(FileReadInfo info) =>
        ReadFileContentsAsync(Task.FromResult(info));

    internal static Task<NativeArray<byte>> ReadFileContentsAsync(Task<FileReadInfo> info)
    {
        if (Config.Instance.UseAsyncReadManager)
            return ReadFileContentsUnity(info).Unwrap();

        return Task.Run(async () =>
        {
            var finfo = await info;
            var data = await AllocatorUtil.CreateNativeArrayHGlobalAsync<byte>(
                finfo.length,
                NativeArrayOptions.UninitializedMemory
            );

            try
            {
                ReadFileContentsManaged(finfo.path, finfo.offset, data);
                return data;
            }
            catch
            {
                data.DisposeExt();
                throw;
            }
        });
    }

    static unsafe void ReadFileContentsManaged(string path, long fileOffset, NativeArray<byte> data)
    {
        using var scope = ReadFileContentsMarker.Auto();

        using var reader = File.OpenRead(path);
        var ptr = (byte*)data.GetUnsafePtr();

        int offset = 0;
        int length = data.Length;
        var buffer = new byte[64 * 1024];

        // Seek doesn't appear to reliably actually set the stream to the right
        // position on some systems (notably Win10).
        //
        // We sidestep this by just reading from the start, since all offsets
        // used for this job are fairly small.
        while (offset < fileOffset)
        {
            var remaining = (int)fileOffset - offset;
            int count = reader.Read(buffer, 0, Math.Min(remaining, buffer.Length));
            offset += count;

            if (count == 0)
                throw new Exception("unexpected EOF when reading file");
        }

        offset = 0;

        while (offset < length)
        {
            int count = reader.Read(buffer, 0, buffer.Length);
            if (count > length - offset || count <= 0)
                throw new Exception(
                    $"the length of the file changed while it was being read (read {offset + count} bytes but expected {length} bytes)"
                );

            data.CopyRangeFrom(offset, buffer, count);
            offset += count;
        }
    }

    static async Task<Task<NativeArray<byte>>> ReadFileContentsUnity(Task<FileReadInfo> infoTask)
    {
        var info = await infoTask;
        var data = await AllocatorUtil.CreateNativeArrayHGlobalAsync<byte>(
            info.length,
            NativeArrayOptions.UninitializedMemory
        );

        try
        {
            ReadHandle handle;
            unsafe
            {
                var command = new ReadCommand
                {
                    Buffer = data.GetUnsafePtr(),
                    Offset = info.offset,
                    Size = data.Length,
                };
                handle = AsyncReadManager.Read(info.path, &command, 1);
            }

            var task = AsyncUtil.WaitFor(handle.JobHandle);

            // This runs on the thread pool so we don't have to wait for a main
            // thread update before we run anything else.
            return Task.Run(async () =>
            {
                using var hguard = handle;
                await task;

                if (handle.Status != ReadStatus.Complete)
                    throw new Exception("Failed to read texture data from file");

                return data;
            });
        }
        catch
        {
            data.DisposeExt();
            throw;
        }
    }
}
